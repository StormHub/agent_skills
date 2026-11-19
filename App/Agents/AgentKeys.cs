namespace App.Agents;

/// <summary>DI keys of the agent setups. Resolve one with <c>GetRequiredKeyedService&lt;AIAgent&gt;(key)</c>.</summary>
internal static class AgentKeys
{
    /// <summary>Skills on the agent; loaded skill content stays in the conversation history.</summary>
    public const string Baseline = "baseline";

    /// <summary>Approach A: skills only on a worker agent that the main agent calls as a tool.</summary>
    public const string Subagent = "subagent";

    /// <summary>Approach B: skills on the agent, with skill content from earlier turns stubbed out.</summary>
    public const string Ephemeral = "ephemeral";
}
