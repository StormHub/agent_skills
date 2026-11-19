using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace App.Agents;

/// <summary>
/// Approach B: the same agent as the baseline, but every request to the model goes through
/// <see cref="EarlierTurnSkillContentChatClient"/>, which stubs skill content loaded in earlier turns. A skill is fully
/// visible in the turn that loads it (including the whole tool-calling loop) and is reloaded if a later turn needs it.
/// </summary>
internal static class EphemeralAgent
{
    public static AIAgent Create(IServiceProvider services)
    {
        // The agent adds its own FunctionInvokingChatClient on top, so this runs for every model call, inside the
        // tool loop too: FunctionInvokingChatClient -> EarlierTurnSkillContentChatClient -> model
        IChatClient chatClient = services.GetRequiredService<IChatClient>()
            .AsBuilder()
            .Use(inner => new EarlierTurnSkillContentChatClient(inner))
            .Build();

        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_ephemeral",
            ChatOptions = new ChatOptions
            {
                Instructions = AgentComponents.ShoppingInstructions,
                Tools = [AgentComponents.CreateHttpTool(services)],
            },
            AIContextProviders = [AgentComponents.CreateSkillsProvider(services)],
        });
    }
}
