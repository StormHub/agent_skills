using Microsoft.Extensions.AI;

namespace App.Agents;

/// <summary>
/// Approach B: keeps loaded skill content for the current turn only.
/// </summary>
/// <remarks>
/// <para>
/// AG-UI is stateless: the Client keeps the conversation (including every tool result the server streams back) and
/// sends all of it on every request. So the place to stop old skill content reaching the model is here, just before
/// each model call, rather than in a server-side ChatHistoryProvider.
/// </para>
/// <para>
/// The current turn starts at the last user message. Results of <c>load_skill</c> and <c>read_skill_resource</c> calls
/// made before that point are replaced with a one-line stub. The call/result pair is kept so the history stays valid
/// for the provider, and the stub tells the model it can load the skill again. Everything in the current turn,
/// including the whole tool-calling loop, is passed through unchanged.
/// </para>
/// </remarks>
internal sealed class EarlierTurnSkillContentChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    // Same values as AgentSkillsProvider.LoadSkillToolName / ReadSkillResourceToolName.
    private const string LoadSkill = "load_skill";
    private const string ReadSkillResource = "read_skill_resource";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(StubEarlierSkillContent(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(StubEarlierSkillContent(messages), options, cancellationToken);

    internal static IReadOnlyList<ChatMessage> StubEarlierSkillContent(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        int currentTurnStart = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
            {
                currentTurnStart = i;
                break;
            }
        }

        if (currentTurnStart <= 0)
        {
            return list;
        }

        // Skill calls made in earlier turns, by call id.
        Dictionary<string, string> stubs = [];
        for (int i = 0; i < currentTurnStart; i++)
        {
            foreach (var call in list[i].Contents.OfType<FunctionCallContent>())
            {
                if (call.Name is LoadSkill or ReadSkillResource)
                {
                    stubs[call.CallId] = CreateStub(call);
                }
            }
        }

        if (stubs.Count == 0)
        {
            return list;
        }

        var result = new List<ChatMessage>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var message = list[i];
            if (i >= currentTurnStart
                || !message.Contents.Any(c => c is FunctionResultContent r && stubs.ContainsKey(r.CallId)))
            {
                result.Add(message);
                continue;
            }

            var contents = message.Contents
                .Select(c => c is FunctionResultContent r && stubs.TryGetValue(r.CallId, out var stub)
                    ? new FunctionResultContent(r.CallId, stub)
                    : c)
                .ToList();

            result.Add(new ChatMessage(message.Role, contents)
            {
                AuthorName = message.AuthorName,
                MessageId = message.MessageId,
                CreatedAt = message.CreatedAt,
                AdditionalProperties = message.AdditionalProperties,
            });
        }

        return result;
    }

    private static string CreateStub(FunctionCallContent call)
    {
        string skill = Argument(call, "skillName") ?? "unknown";
        return call.Name == ReadSkillResource
            ? $"[Resource '{Argument(call, "resourceName") ?? "unknown"}' of skill '{skill}' was read in an earlier turn. " +
              $"Its content was removed to save context; call {ReadSkillResource} again if you need it.]"
            : $"[Skill '{skill}' was loaded in an earlier turn. Its content was removed to save context; " +
              $"call {LoadSkill} again before relying on its instructions.]";
    }

    private static string? Argument(FunctionCallContent call, string name) =>
        call.Arguments is not null && call.Arguments.TryGetValue(name, out var value) ? value?.ToString() : null;
}
