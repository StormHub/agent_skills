using App.Agents;
using App.Mcp;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using OpenAI;
using System.ClientModel;
using System.ClientModel.Primitives;

namespace App.Extensions;

internal static class DependencyInjection
{
    public static IServiceCollection AddOpenAI(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration["OpenAIClient:BaseUrl"] ?? throw new InvalidOperationException("OpenAIClient BaseUrl required");
        var model = configuration["OpenAIClient:Model"] ?? throw new InvalidOperationException("OpenAIClient Model required");
        var apiKey = configuration["OpenAIClient:ApiKey"] ?? throw new InvalidOperationException("OpenAIClient ApiKey required");

        services.AddHttpClient(nameof(OpenAIClient));
        services.AddTransient(provider =>
        {
            var httpClient = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(OpenAIClient));

            var url = baseUrl.TrimEnd('/');
            if (!url.EndsWith("openai/v1"))
            {
                url = $"{baseUrl}/openai/v1";
            }
            var openAIOptions = new OpenAIClientOptions
            {
                Endpoint = new Uri(url),
                Transport = new HttpClientPipelineTransport(httpClient)
            };

            var credential = new ApiKeyCredential(apiKey);
            var client = new OpenAIClient(credential, openAIOptions);
            var chatClient = client.GetChatClient(model);
            return chatClient.AsIChatClient();
        });

        return services;
    }

    public static IServiceCollection AddAIAgent(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["ServerApi:Url"] ?? throw new InvalidOperationException("McpClient url required");
        var baseUri = new Uri(url);
        var uri = new Uri(baseUri, "/mcp");

        services.AddHttpClient(nameof(McpClient))
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = uri;
            }); 
        services.AddSingleton(provider => new McpClientAccessor(
            uri,
            provider.GetRequiredService<IHttpClientFactory>(),
            provider.GetRequiredService<ILoggerFactory>()));

        // One agent per setup, told apart by key (see AgentKeys).
        services.AddKeyedSingleton<AIAgent>(AgentKeys.Baseline, (provider, _) => BaselineAgent.Create(provider));
        services.AddKeyedSingleton<AIAgent>(AgentKeys.Subagent, (provider, _) => SubagentAgent.Create(provider));
        services.AddKeyedSingleton<AIAgent>(AgentKeys.Ephemeral, (provider, _) => EphemeralAgent.Create(provider));

        return services;
    }
}
