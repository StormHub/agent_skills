using System.Text;
using Microsoft.Extensions.AI;

namespace App.Extensions;

/// <summary>
/// Renders a message list one line per content item, so you can see what a history holds: who said what, which tools
/// were called with which arguments, and how big each result is. Results of <c>load_skill</c> and
/// <c>read_skill_resource</c> are tagged <c>SKILL</c> so you can see whether skill content stays in the history.
/// </summary>
internal static class HistoryFormatter
{
    private const int MaxPreview = 160;

    public static string Format(IReadOnlyList<ChatMessage> messages, string? instructions = null)
    {
        var skillCalls = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Where(c => c.Name is "load_skill" or "read_skill_resource")
            .Select(c => c.CallId)
            .ToHashSet();

        var callNames = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .GroupBy(c => c.CallId)
            .ToDictionary(g => g.Key, g => g.First().Name);

        var sb = new StringBuilder();
        if (instructions is not null)
        {
            sb.AppendLine($"  instructions ({instructions.Length} chars): {Preview(instructions)}");
        }

        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            string prefix = $"  [{i}] {message.Role.Value,-9}";

            if (message.Contents.Count == 0)
            {
                sb.AppendLine($"{prefix} (empty)");
                continue;
            }

            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrWhiteSpace(text.Text):
                        sb.AppendLine($"{prefix} {Preview(text.Text)}");
                        break;

                    case FunctionCallContent call:
                        var args = call.Arguments is null
                            ? ""
                            : string.Join(", ", call.Arguments.Select(a => $"{a.Key}={a.Value}"));
                        sb.AppendLine($"{prefix} -> {call.Name}({Preview(args)})");
                        break;

                    case FunctionResultContent result:
                        string body = result.Result?.ToString() ?? "";
                        string name = callNames.GetValueOrDefault(result.CallId) ?? "?";
                        string tag = skillCalls.Contains(result.CallId) ? " SKILL" : "";
                        sb.AppendLine($"{prefix} <- {name}{tag} ({body.Length} chars): {Preview(body)}");
                        break;

                    case TextContent:
                        break;

                    default:
                        sb.AppendLine($"{prefix} ({content.GetType().Name})");
                        break;
                }
            }
        }

        return sb.ToString();
    }

    private static string Preview(string text)
    {
        string flat = text.ReplaceLineEndings(" ");
        return flat.Length <= MaxPreview ? flat : string.Concat(flat.AsSpan(0, MaxPreview), "...");
    }
}
