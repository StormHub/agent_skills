using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace App.Agents;

/// <summary>
/// The original setup: skills on the agent. Every loaded skill comes back as a tool result, stays in the history, and
/// is sent to the model again on every later turn.
/// </summary>
internal static class BaselineAgent
{
    public static AIAgent Create(IServiceProvider services) =>
        services.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_baseline",
            ChatOptions = new ChatOptions
            {
                Instructions = AgentComponents.ShoppingInstructions,
                Tools = [AgentComponents.CreateHttpTool(services)],
            },
            AIContextProviders = [AgentComponents.CreateSkillsProvider(services)],
        });
}
