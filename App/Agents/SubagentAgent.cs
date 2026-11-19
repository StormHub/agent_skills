using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace App.Agents;

/// <summary>
/// Approach A: the main agent has no skills. A worker agent has them and is exposed to the main agent as a tool with
/// <see cref="AIAgentExtensions.AsAIFunction"/>. Without a session argument, AsAIFunction runs the worker in a new
/// session on every call, so the skill content it loads is dropped when the call returns. Only the worker's text reply
/// reaches the main conversation (and the Client's history).
/// </summary>
internal static class SubagentAgent
{
    private const string MainInstructions =
        "You are a helpful shopping assistant. You have no product data of your own. For anything that needs product " +
        "data (searching the catalog, prices, discounts, promotions), call skill_worker. Its request must be complete " +
        "and self-contained, because skill_worker cannot see this conversation: include product names and ids from " +
        "earlier results, budgets and preferences. Never invent product data.";

    private const string WorkerInstructions =
        "You are a shopping specialist working for another assistant, not for the end user. Complete the request with " +
        "your skills and the http_request tool. Reply only with the facts the assistant needs: product names, product " +
        "ids, list prices, discounts, promotions and final prices. Always include product ids. Be concise.";

    private const string WorkerDescription =
        "Shopping specialist that searches the product catalog and looks up prices, discounts and promotions. " +
        "Pass a complete, self-contained request including any product names, product ids, budgets and preferences " +
        "from the conversation, since it cannot see the conversation.";

    public static AIAgent Create(IServiceProvider services)
    {
        var chatClient = services.GetRequiredService<IChatClient>();

        AIAgent worker = chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "skill_worker",
            Description = WorkerDescription,
            ChatOptions = new ChatOptions
            {
                Instructions = WorkerInstructions,
                Tools = [AgentComponents.CreateHttpTool(services)],
            },
            AIContextProviders = [AgentComponents.CreateSkillsProvider(services)],
        });

        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_subagent",
            ChatOptions = new ChatOptions
            {
                Instructions = MainInstructions,
                Tools = [worker.AsAIFunction()],
            },
        });
    }
}
