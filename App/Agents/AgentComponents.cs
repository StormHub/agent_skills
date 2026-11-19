using System.Text;
using App.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace App.Agents;

/// <summary>The pieces the agent setups share: instructions, the skills provider and the http_request tool.</summary>
internal static class AgentComponents
{
    public const string ShoppingInstructions = "You are a helpful shopping assistant.";

    /// <summary>
    /// Creates a skills provider backed by the Api's MCP skills. Register it on the agent, not on the chat client:
    /// the agent wraps the chat client with its own FunctionInvokingChatClient, so tools added by a provider below
    /// that layer (load_skill, read_skill_resource) are never seen by it and fail with
    /// 'Requested function "load_skill" not found'.
    /// </summary>
    public static AgentSkillsProvider CreateSkillsProvider(IServiceProvider services)
    {
        var clientAccessor = services.GetRequiredService<McpClientAccessor>();

        return new AgentSkillsProviderBuilder()
            .UseSource(loggerFactory => new DeferredMcpSkillsSource(clientAccessor, loggerFactory))
            .UseLoggerFactory(services.GetRequiredService<ILoggerFactory>())
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
    }

    /// <summary>Creates the tool that calls the endpoints the skills describe, on the Api host.</summary>
    public static AIFunction CreateHttpTool(IServiceProvider services)
    {
        var httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

        return AIFunctionFactory.Create(
            (string method, string path, string? body = null, CancellationToken cancellationToken = default) =>
                SendAsync(httpClientFactory, method, path, body, cancellationToken),
            name: "http_request",
            description: "Send an HTTP request to an endpoint described by a skill, on the same host that serves the skills. " +
                         "Pass the method (GET, POST, PUT, PATCH, DELETE), the path and query only (for example " +
                         "/catalog?q=laptop or /pricing/p_laptop_dell), and a JSON body when the endpoint takes one. " +
                         "Returns the status code and response body.");
    }

    /// <summary>Sends a request through the Api's named HttpClient. Only relative paths on the Api host are accepted.</summary>
    private static async Task<string> SendAsync(
        IHttpClientFactory httpClientFactory, string method, string path, string? body, CancellationToken cancellationToken)
    {
        if (!path.StartsWith('/') || path.StartsWith("//"))
        {
            return "Error: path must be relative to the skills host and start with '/', for example /catalog?q=laptop.";
        }

        // The client's BaseAddress is .../mcp; a path starting with '/' resolves against the host root.
        var httpClient = httpClientFactory.CreateClient(nameof(McpClient));
        using var request = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), path);
        if (!string.IsNullOrEmpty(body))
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return $"HTTP {(int)response.StatusCode}\n{content}";
    }
}
