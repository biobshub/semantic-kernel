﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.Agents.Persistent;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.Agents.AzureAI.Internal;
using Microsoft.SemanticKernel.Agents.Extensions;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Diagnostics;

namespace Microsoft.SemanticKernel.Agents.AzureAI;

/// <summary>
/// Provides a specialized <see cref="Agent"/> based on an Azure AI agent.
/// </summary>
public sealed partial class AzureAIAgent : Agent
{
    /// <summary>
    /// Provides tool definitions used when associating a file attachment to an input message:
    /// <see cref="FileReferenceContent.Tools"/>.
    /// </summary>
    public static class Tools
    {
        /// <summary>
        /// The code-interpreter tool.
        /// </summary>
        public static readonly string CodeInterpreter = "code_interpreter";

        /// <summary>
        /// The file-search tool.
        /// </summary>
        public const string FileSearch = "file_search";
    }

    /// <summary>
    /// The metadata key that identifies code-interpreter content.
    /// </summary>
    public const string CodeInterpreterMetadataKey = "code";

    /// <summary>
    /// Gets the assistant definition.
    /// </summary>
    public PersistentAgent Definition { get; private init; }

    /// <summary>
    /// Gets the polling behavior for run processing.
    /// </summary>
    public RunPollingOptions PollingOptions { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureAIAgent"/> class.
    /// </summary>
    /// <param name="model">The agent model definition.</param>
    /// <param name="client">An <see cref="PersistentAgentsClient"/> instance.</param>
    /// <param name="plugins">Optional collection of plugins to add to the kernel.</param>
    /// <param name="templateFactory">An optional factory to produce the <see cref="IPromptTemplate"/> for the agent.</param>
    /// <param name="templateFormat">The format of the prompt template used when "templateFactory" parameter is supplied.</param>
    public AzureAIAgent(
        PersistentAgent model,
        PersistentAgentsClient client,
        IEnumerable<KernelPlugin>? plugins = null,
        IPromptTemplateFactory? templateFactory = null,
        string? templateFormat = null)
    {
        this.Client = client;
        this.Definition = model;
        this.Description = this.Definition.Description;
        this.Id = this.Definition.Id;
        this.Name = this.Definition.Name;
        this.Instructions = this.Definition.Instructions;

        if (templateFactory != null)
        {
            Verify.NotNullOrWhiteSpace(templateFormat);

            PromptTemplateConfig templateConfig = new(this.Instructions)
            {
                TemplateFormat = templateFormat
            };

            this.Template = templateFactory.Create(templateConfig);
        }

        if (plugins != null)
        {
            this.Kernel.Plugins.AddRange(plugins);
        }
    }

    /// <summary>
    /// The associated client.
    /// </summary>
    public PersistentAgentsClient Client { get; }

    /// <inheritdoc/>
    public override IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.InvokeAsync(
            messages,
            thread,
            options is null ?
                null :
                options is AzureAIAgentInvokeOptions azureAIAgentInvokeOptions ? azureAIAgentInvokeOptions : new AzureAIAgentInvokeOptions(options),
            cancellationToken);
    }

    /// <summary>
    /// Invoke the agent with the provided message and arguments.
    /// </summary>
    /// <param name="messages">The messages to pass to the agent.</param>
    /// <param name="thread">The conversation thread to continue with this invocation. If not provided, creates a new thread.</param>
    /// <param name="options">Optional parameters for agent invocation.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>An async list of response items that each contain a <see cref="ChatMessageContent"/> and an <see cref="AgentThread"/>.</returns>
    /// <remarks>
    /// To continue this thread in the future, use an <see cref="AgentThread"/> returned in one of the response items.
    /// </remarks>
    public async IAsyncEnumerable<AgentResponseItem<ChatMessageContent>> InvokeAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AzureAIAgentInvokeOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Verify.NotNull(messages);

        AzureAIAgentThread azureAIAgentThread = await this.EnsureThreadExistsWithMessagesAsync(
            messages,
            thread,
            () => new AzureAIAgentThread(this.Client),
            cancellationToken).ConfigureAwait(false);

        Kernel kernel = this.GetKernel(options);
#pragma warning disable SKEXP0110, SKEXP0130 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        if (this.UseImmutableKernel)
        {
            kernel = kernel.Clone();
        }

        // Get the context contributions from the AIContextProviders.
        AIContext providersContext = await azureAIAgentThread.AIContextProviders.ModelInvokingAsync(messages, cancellationToken).ConfigureAwait(false);

        // Check for compatibility AIContextProviders and the UseImmutableKernel setting.
        if (providersContext.AIFunctions is { Count: > 0 } && !this.UseImmutableKernel)
        {
            throw new InvalidOperationException("AIContextProviders with AIFunctions are not supported when Agent UseImmutableKernel setting is false.");
        }

        kernel.Plugins.AddFromAIContext(providersContext, "Tools");
#pragma warning restore SKEXP0110, SKEXP0130 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        string mergedAdditionalInstructions = FormatAdditionalInstructions(providersContext, options);
        var extensionsContextOptions = options is null ?
            new AzureAIAgentInvokeOptions() { AdditionalInstructions = mergedAdditionalInstructions } :
            new AzureAIAgentInvokeOptions(options) { AdditionalInstructions = mergedAdditionalInstructions };

        var invokeResults = ActivityExtensions.RunWithActivityAsync(
            () => ModelDiagnostics.StartAgentInvocationActivity(this.Id, this.GetDisplayName(), this.Description),
            () => InternalInvokeAsync(),
            cancellationToken);

        async IAsyncEnumerable<ChatMessageContent> InternalInvokeAsync()
        {
            await foreach ((bool isVisible, ChatMessageContent message) in AgentThreadActions.InvokeAsync(
                this,
                this.Client,
                azureAIAgentThread.Id!,
                extensionsContextOptions?.ToAzureAIInvocationOptions(),
                this.Logger,
                kernel,
                options?.KernelArguments,
                cancellationToken).ConfigureAwait(false))
            {
                // The thread and the caller should be notified of all messages regardless of visibility.
                await this.NotifyThreadOfNewMessage(azureAIAgentThread, message, cancellationToken).ConfigureAwait(false);
                if (options?.OnIntermediateMessage is not null)
                {
                    await options.OnIntermediateMessage(message).ConfigureAwait(false);
                }

                if (isVisible)
                {
                    yield return message;
                }
            }
        }

        // Notify the thread of new messages and return them to the caller.
        await foreach (var result in invokeResults.ConfigureAwait(false))
        {
            yield return new(result, azureAIAgentThread);
        }
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AgentInvokeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return this.InvokeStreamingAsync(
            messages,
            thread,
            options is null ?
                null :
                options is AzureAIAgentInvokeOptions azureAIAgentInvokeOptions ? azureAIAgentInvokeOptions : new AzureAIAgentInvokeOptions(options),
            cancellationToken);
    }

    /// <summary>
    /// Invoke the agent with the provided message and arguments.
    /// </summary>
    /// <param name="messages">The messages to pass to the agent.</param>
    /// <param name="thread">The conversation thread to continue with this invocation. If not provided, creates a new thread.</param>
    /// <param name="options">Optional parameters for agent invocation.</param>
    /// <param name="cancellationToken">The <see cref="CancellationToken"/> to monitor for cancellation requests. The default is <see cref="CancellationToken.None"/>.</param>
    /// <returns>An async list of response items that each contain a <see cref="StreamingChatMessageContent"/> and an <see cref="AgentThread"/>.</returns>
    /// <remarks>
    /// To continue this thread in the future, use an <see cref="AgentThread"/> returned in one of the response items.
    /// </remarks>
    public async IAsyncEnumerable<AgentResponseItem<StreamingChatMessageContent>> InvokeStreamingAsync(
        ICollection<ChatMessageContent> messages,
        AgentThread? thread = null,
        AzureAIAgentInvokeOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Verify.NotNull(messages);

        AzureAIAgentThread azureAIAgentThread = await this.EnsureThreadExistsWithMessagesAsync(
            messages,
            thread,
            () => new AzureAIAgentThread(this.Client),
            cancellationToken).ConfigureAwait(false);

        Kernel kernel = this.GetKernel(options);
#pragma warning disable SKEXP0110, SKEXP0130 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        if (this.UseImmutableKernel)
        {
            kernel = kernel.Clone();
        }

        // Get the context contributions from the AIContextProviders.
        AIContext providersContext = await azureAIAgentThread.AIContextProviders.ModelInvokingAsync(messages, cancellationToken).ConfigureAwait(false);

        // Check for compatibility AIContextProviders and the UseImmutableKernel setting.
        if (providersContext.AIFunctions is { Count: > 0 } && !this.UseImmutableKernel)
        {
            throw new InvalidOperationException("AIContextProviders with AIFunctions are not supported when Agent UseImmutableKernel setting is false.");
        }

        kernel.Plugins.AddFromAIContext(providersContext, "Tools");
#pragma warning restore SKEXP0110, SKEXP0130 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        string mergedAdditionalInstructions = FormatAdditionalInstructions(providersContext, options);
        var extensionsContextOptions = options is null ?
            new AzureAIAgentInvokeOptions() { AdditionalInstructions = mergedAdditionalInstructions } :
            new AzureAIAgentInvokeOptions(options) { AdditionalInstructions = mergedAdditionalInstructions };

        // Invoke the Agent with the thread that we already added our message to, and with
        // a chat history to receive complete messages.
        ChatHistory newMessagesReceiver = [];
        var invokeResults = ActivityExtensions.RunWithActivityAsync(
            () => ModelDiagnostics.StartAgentInvocationActivity(this.Id, this.GetDisplayName(), this.Description),
            () => AgentThreadActions.InvokeStreamingAsync(
                this,
                this.Client,
                azureAIAgentThread.Id!,
                newMessagesReceiver,
                extensionsContextOptions.ToAzureAIInvocationOptions(),
                this.Logger,
                kernel,
                options?.KernelArguments,
                cancellationToken),
            cancellationToken);

        // Return the chunks to the caller.
        await foreach (var result in invokeResults.ConfigureAwait(false))
        {
            yield return new(result, azureAIAgentThread);
        }

        // Notify the thread of any new messages that were assembled from the streaming response.
        foreach (var newMessage in newMessagesReceiver)
        {
            await this.NotifyThreadOfNewMessage(azureAIAgentThread, newMessage, cancellationToken).ConfigureAwait(false);

            if (options?.OnIntermediateMessage is not null)
            {
                await options.OnIntermediateMessage(newMessage).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    protected override IEnumerable<string> GetChannelKeys()
    {
        // Distinguish from other channel types.
        yield return typeof(AzureAIChannel).FullName!;
        // Distinguish based on client instance.
        yield return this.Client.GetHashCode().ToString();
    }

    /// <inheritdoc/>
    protected override async Task<AgentChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        this.Logger.LogAzureAIAgentCreatingChannel(nameof(CreateChannelAsync), nameof(AzureAIChannel));

        string threadId = await AgentThreadActions.CreateThreadAsync(this.Client, cancellationToken).ConfigureAwait(false);

        this.Logger.LogInformation("[{MethodName}] Created assistant thread: {ThreadId}", nameof(CreateChannelAsync), threadId);

        AzureAIChannel channel =
            new(this.Client, threadId)
            {
                Logger = this.ActiveLoggerFactory.CreateLogger<AzureAIChannel>()
            };

        this.Logger.LogAzureAIAgentCreatedChannel(nameof(CreateChannelAsync), nameof(AzureAIChannel), threadId);

        return channel;
    }

    internal Task<string?> GetInstructionsAsync(Kernel kernel, KernelArguments? arguments, CancellationToken cancellationToken)
    {
        return this.RenderInstructionsAsync(kernel, arguments, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<AgentChannel> RestoreChannelAsync(string channelState, CancellationToken cancellationToken)
    {
        string threadId = channelState;

        this.Logger.LogAzureAIAgentRestoringChannel(nameof(RestoreChannelAsync), nameof(AzureAIChannel), threadId);

        PersistentAgentThread thread = await this.Client.Threads.GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);

        this.Logger.LogAzureAIAgentRestoredChannel(nameof(RestoreChannelAsync), nameof(AzureAIChannel), threadId);

        return new AzureAIChannel(this.Client, thread.Id);
    }
}
