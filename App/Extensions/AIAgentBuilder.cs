using System.Text;
using App.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace App.Extensions;

/// <summary>The three agents served side by side so the way they handle skill content can be compared.</summary>
/// <param name="Baseline">Skills on the main agent; loaded skill content stays in the conversation history.</param>
/// <param name="Subagent">Approach A: skills only on a worker agent that the main agent calls as a tool.</param>
/// <param name="Ephemeral">Approach B: skills on the main agent, with skill content from earlier turns stubbed out.</param>
internal sealed record AgentSet(AIAgent Baseline, AIAgent Subagent, AIAgent Ephemeral);

internal sealed class AIAgentBuilder(IChatClient chatClient, IHttpClientFactory httpClientFactory, Uri endpoint, ILoggerFactory loggerFactory)
{
    private const string ShoppingInstructions = "You are a helpful shopping assistant.";

    private const string SubagentMainInstructions =
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

    // One HttpClient for everything sent to the Api: the MCP transport and the endpoints the skills describe.
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(McpClient));

    public async Task<AgentSet> Build(CancellationToken token = default)
    {
        var mcpClient = await CreateMcpClient(token);
        return new AgentSet(
            Baseline: BuildBaseline(mcpClient),
            Subagent: BuildSubagent(mcpClient),
            Ephemeral: BuildEphemeral(mcpClient));
    }

    /// <summary>The original setup: every loaded skill is resent to the model on every later turn.</summary>
    private AIAgent BuildBaseline(McpClient mcpClient)
    {
        var tally = new TokenTally("baseline");
        return Instrument(chatClient, "baseline", tally).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_baseline",
            ChatOptions = new ChatOptions
            {
                Instructions = ShoppingInstructions,
                Tools = [CreateHttpTool()],
            },
            AIContextProviders = [CreateSkillsProvider(mcpClient)],
        });
    }

    /// <summary>
    /// Approach A. The main agent has no skills; a worker agent has them and is exposed as a tool with
    /// <see cref="AIAgentExtensions.AsAIFunction"/>. Without a session argument, AsAIFunction runs the worker in a new
    /// session on every call, so the skill content it loads is dropped when the call returns. Only the worker's text
    /// reply reaches the main conversation (and the Client's history).
    /// </summary>
    private AIAgent BuildSubagent(McpClient mcpClient)
    {
        var tally = new TokenTally("subagent");

        AIAgent worker = Instrument(chatClient, "subagent/worker", tally).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "skill_worker",
            Description = WorkerDescription,
            ChatOptions = new ChatOptions
            {
                Instructions = WorkerInstructions,
                Tools = [CreateHttpTool()],
            },
            AIContextProviders = [CreateSkillsProvider(mcpClient)],
        });

        return Instrument(chatClient, "subagent/main", tally).AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_subagent",
            ChatOptions = new ChatOptions
            {
                Instructions = SubagentMainInstructions,
                Tools = [worker.AsAIFunction()],
            },
        });
    }

    /// <summary>
    /// Approach B. Same agent as the baseline, but every request to the model goes through
    /// <see cref="EarlierTurnSkillContentChatClient"/>, which stubs skill content loaded in earlier turns. A skill is
    /// fully visible in the turn that loads it (including the whole tool-calling loop) and is reloaded if a later
    /// turn needs it again.
    /// </summary>
    private AIAgent BuildEphemeral(McpClient mcpClient)
    {
        var tally = new TokenTally("ephemeral");

        // ChatClientBuilder.Use: the first registration is the outermost. The agent adds its own
        // FunctionInvokingChatClient on top, so this pipeline runs for every model call, inside the tool loop too:
        //   FunctionInvokingChatClient -> EarlierTurnSkillContentChatClient -> UsageLoggingChatClient -> model
        IChatClient client = chatClient
            .AsBuilder()
            .Use(inner => new EarlierTurnSkillContentChatClient(inner))
            .Use(inner => new UsageLoggingChatClient(inner, "ephemeral", tally, loggerFactory))
            .Build();

        return client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "shopping_ephemeral",
            ChatOptions = new ChatOptions
            {
                Instructions = ShoppingInstructions,
                Tools = [CreateHttpTool()],
            },
            AIContextProviders = [CreateSkillsProvider(mcpClient)],
        });
    }

    private IChatClient Instrument(IChatClient client, string label, TokenTally tally) =>
        client.AsBuilder()
            .Use(inner => new UsageLoggingChatClient(inner, label, tally, loggerFactory))
            .Build();

    private async Task<McpClient> CreateMcpClient(CancellationToken cancellationToken) =>
        await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "api",
                    Endpoint = endpoint,
                },
                _httpClient,
                loggerFactory),
            loggerFactory: loggerFactory,
            cancellationToken: cancellationToken);

    // Register the skills provider on the agent, not on the chat client. The agent wraps the chat client with
    // its own FunctionInvokingChatClient, so tools added by a provider below that layer (load_skill,
    // read_skill_resource) are never seen by it and fail with 'Requested function "load_skill" not found'.
    private static AgentSkillsProvider CreateSkillsProvider(McpClient mcpClient) =>
        new AgentSkillsProviderBuilder()
            .UseMcpSkills(mcpClient)
            .DisableCaching() // let the MCP source honor the server's TTLs
            // load_skill and read_skill_resource require approval by default. The run then ends with an approval
            // interrupt that the Client does not handle, so the skill is never loaded. Both tools only read skill
            // text from our own Api, so approval is turned off for them. This matters even more for the subagent:
            // a nested agent called as a tool has no way to surface an approval request.
            .UseOptions(options =>
            {
                options.DisableLoadSkillApproval = true;
                options.DisableReadSkillResourceApproval = true;
            })
            .Build();

    private AIFunction CreateHttpTool() =>
        AIFunctionFactory.Create(
            SendAsync,
            name: "http_request",
            description: "Send an HTTP request to an endpoint described by a skill, on the same host that serves the skills. " +
                         "Pass the method (GET, POST, PUT, PATCH, DELETE), the path and query only (for example " +
                         "/catalog?q=laptop or /pricing/p_laptop_dell), and a JSON body when the endpoint takes one. " +
                         "Returns the status code and response body.");

    /// <summary>Sends a request through the shared HttpClient. Only relative paths on the Api host are accepted.</summary>
    private async Task<string> SendAsync(string method, string path, string? body = null, CancellationToken cancellationToken = default)
    {
        if (!path.StartsWith('/') || path.StartsWith("//"))
        {
            return "Error: path must be relative to the skills host and start with '/', for example /catalog?q=laptop.";
        }

        // The client's BaseAddress is .../mcp; a path starting with '/' resolves against the host root.
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), path);
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return $"HTTP {(int)response.StatusCode}\n{content}";
    }
}
