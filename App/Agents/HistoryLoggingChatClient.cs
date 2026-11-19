using Microsoft.Extensions.AI;
using Shared;

namespace App.Agents;

/// <summary>
/// Logs the history exactly as the model receives it, once per model call (inside the tool-calling loop too).
/// </summary>
/// <remarks>
/// It sits directly above the model, below any other decorator, so for the ephemeral setup it shows the history after
/// earlier skill content was stubbed.
/// </remarks>
internal sealed class HistoryLoggingChatClient(IChatClient innerClient, string label, ILoggerFactory loggerFactory)
    : DelegatingChatClient(innerClient)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("App.History");

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = Log(messages, options);
        return base.GetResponseAsync(list, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = Log(messages, options);
        return base.GetStreamingResponseAsync(list, options, cancellationToken);
    }

    private IReadOnlyList<ChatMessage> Log(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        _logger.LogInformation(
            "[{Label}] history sent to the model ({Count} messages):\n{History}",
            label,
            list.Count,
            HistoryFormatter.Format(list, options?.Instructions));
        return list;
    }
}
