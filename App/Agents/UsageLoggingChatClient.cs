using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace App.Agents;

/// <summary>Running token totals for one agent setup, shared by every model client that setup uses.</summary>
internal sealed class TokenTally(string name)
{
    private long _calls;
    private long _inputTokens;
    private long _outputTokens;

    public string Name { get; } = name;

    public (long Calls, long InputTokens, long OutputTokens) Add(UsageDetails? usage) =>
        (Interlocked.Increment(ref _calls),
         Interlocked.Add(ref _inputTokens, usage?.InputTokenCount ?? 0),
         Interlocked.Add(ref _outputTokens, usage?.OutputTokenCount ?? 0));
}

/// <summary>
/// Logs what each model call sends and what it costs, so the three setups can be compared on the same conversation.
/// </summary>
/// <remarks>
/// Character counts are measured on the messages this client actually forwards, so for the ephemeral setup they show
/// the history after skill content was stubbed. Token counts come from the provider's usage report when it sends one.
/// </remarks>
internal sealed class UsageLoggingChatClient(IChatClient innerClient, string label, TokenTally tally, ILoggerFactory loggerFactory)
    : DelegatingChatClient(innerClient)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("App.Usage");

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var response = await base.GetResponseAsync(list, options, cancellationToken);
        Log(list, options, response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        UsageDetails? usage = null;

        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    usage ??= new UsageDetails();
                    usage.Add(usageContent.Details);
                }
            }

            yield return update;
        }

        Log(list, options, usage);
    }

    private void Log(IReadOnlyList<ChatMessage> messages, ChatOptions? options, UsageDetails? usage)
    {
        long skillChars = 0;
        long toolResultChars = 0;
        long totalChars = options?.Instructions?.Length ?? 0;

        var skillCallIds = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => c.Name is "load_skill" or "read_skill_resource")
            .Select(c => c.CallId)
            .ToHashSet();

        foreach (var content in messages.SelectMany(m => m.Contents))
        {
            switch (content)
            {
                case TextContent text:
                    totalChars += text.Text.Length;
                    break;

                case FunctionCallContent call:
                    totalChars += call.Name.Length + (call.Arguments?.Sum(a => a.Key.Length + (a.Value?.ToString()?.Length ?? 0)) ?? 0);
                    break;

                case FunctionResultContent result:
                    int length = result.Result?.ToString()?.Length ?? 0;
                    totalChars += length;
                    toolResultChars += length;
                    if (skillCallIds.Contains(result.CallId))
                    {
                        skillChars += length;
                    }

                    break;
            }
        }

        var (calls, inputTotal, outputTotal) = tally.Add(usage);

        _logger.LogInformation(
            "[{Label}] model call: {Messages} messages, ~{Chars} chars sent ({ToolChars} in tool results, {SkillChars} of them skill content); " +
            "tokens in/out: {Input}/{Output}. {Setup} totals after {Calls} calls: {InputTotal} in / {OutputTotal} out",
            label,
            messages.Count,
            totalChars,
            toolResultChars,
            skillChars,
            usage?.InputTokenCount?.ToString() ?? "n/a",
            usage?.OutputTokenCount?.ToString() ?? "n/a",
            tally.Name,
            calls,
            inputTotal,
            outputTotal);
    }
}
