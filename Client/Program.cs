using AGUI.Client;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

// Pick the setup to talk to with --Endpoint (default: the baseline):
//   http://localhost:5000/chat            baseline: loaded skills stay in history
//   http://localhost:5000/chat/subagent   approach A: skills isolated in a worker agent
//   http://localhost:5000/chat/ephemeral  approach B: skill content kept for the current turn only
// Replay a fixed conversation with --Script <file> (one user message per line) to compare setups on equal terms.
// Type :history to print the conversation this Client keeps (and sends in full on every turn); a script prints it at
// the end. The history the model actually receives is logged by App (category App.History).
const string DefaultEndpoint = "http://localhost:5000/chat";

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
        {
            services.AddHttpClient("local");
            services.AddTransient<AIAgent>(provider =>
            {
                var factory = provider.GetRequiredService<IHttpClientFactory>();
                var httpClient = factory.CreateClient("local");
                var jsonSerializerOptions = new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    NumberHandling = JsonNumberHandling.AllowReadingFromString,
                };
                jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());


                var chatClient = new AGUIChatClient(
                    new AGUIChatClientOptions(httpClient, context.Configuration["Endpoint"] ?? DefaultEndpoint)
                    { 
                        JsonSerializerOptions = jsonSerializerOptions
                    });

                var agent = chatClient.AsAIAgent(
                    name: "local-client",
                    description: "AG-UI Client Agent");

                return agent;
            });
        })
     .UseConsoleLifetime()
    .Build();

await host.StartAsync();
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

await using var scope = host.Services.CreateAsyncScope();
var agent = scope.ServiceProvider.GetRequiredService<AIAgent>();
var agentSession = await agent.CreateSessionAsync();
var configuration = host.Services.GetRequiredService<IConfiguration>();
Console.WriteLine($"Endpoint: {configuration["Endpoint"] ?? DefaultEndpoint}");

// Scripted mode: send each non-empty, non-comment line of the file as a user message, then exit.
Queue<string>? script = configuration["Script"] is { Length: > 0 } scriptPath
    ? new Queue<string>(File.ReadAllLines(scriptPath).Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#')))
    : null;

List<ChatMessage> messages = [];
while (!lifetime.ApplicationStopping.IsCancellationRequested)
{
    Console.Write("\nUser (:history, :q or quit to exit): ");
    string? message;
    if (script is null)
    {
        message = Console.ReadLine();
    }
    else
    {
        message = script.Count > 0 ? script.Dequeue() : "quit";
        Console.WriteLine(message);
    }
    if (string.IsNullOrWhiteSpace(message))
    {
        Console.WriteLine("Request cannot be empty.");
        continue;
    }

    if (message is ":history")
    {
        PrintHistory();
        continue;
    }

    if (message is ":q" or "quit")
    {
        if (script is not null)
        {
            PrintHistory();
        }

        lifetime.StopApplication();
        break;
    }

    messages.Add(new(ChatRole.User, message));

    // Call RunStreamingAsync to get streaming updates
    var isFirstUpdate = true;
    string? threadId = null;
    var updates = new List<ChatResponseUpdate>();
    await foreach (var update in agent.RunStreamingAsync(messages, agentSession, cancellationToken: lifetime.ApplicationStopping))
    {
        // Use AsChatResponseUpdate to access ChatResponseUpdate properties
        var chatUpdate = update.AsChatResponseUpdate();
        updates.Add(chatUpdate);
        if (chatUpdate.ConversationId != null) threadId = chatUpdate.ConversationId;

        // Display run started information from the first update
        if (isFirstUpdate && threadId != null && update.ResponseId != null)
        {
            // Console.WriteLine($"\n[Run Started - Thread: {threadId}, Run: {update.ResponseId}]");
            isFirstUpdate = false;
        }

        // Display different content types with appropriate formatting
        foreach (var content in update.Contents)
            switch (content)
            {
                case TextContent textContent:
                    Console.Write(textContent.Text);
                    break;

                case FunctionCallContent functionCallContent:
                    var arguments =
                        functionCallContent.Arguments?.Select(x => $"Name: {x.Key} \n Value: {x.Value}") ?? [];
                    Console.WriteLine(
                        $"\n[Function Call - Name: {functionCallContent.Name}, \n Arguments: {string.Join('\n', arguments)}]");
                    break;

                case FunctionResultContent functionResultContent:
                    Console.WriteLine(functionResultContent.Exception != null
                        ? $"\n[Function Result - Exception: {functionResultContent.Exception}]"
                        : $"\n[Function Result - Result: {functionResultContent.Result}]");
                    break;

                case ErrorContent errorContent:
                    var code = errorContent.AdditionalProperties?["Code"] as string ?? "Unknown";
                    Console.WriteLine($"\n[Error - Code: {code}, Message: {errorContent.Message}]");
                    break;
            }
    }

    if (updates.Count > 0 && !updates[^1].Contents.Any(c => c is TextContent))
    {
        var lastUpdate = updates[^1];
        Console.WriteLine();
        Console.WriteLine($"[Run Ended - Thread: {threadId}, Run: {lastUpdate.ResponseId}]");
    }

    messages.Clear();
    Console.WriteLine();
}

await host.WaitForShutdownAsync(lifetime.ApplicationStopping);

// The Client's ChatClientAgent keeps the conversation in its session (InMemoryChatHistoryProvider). AG-UI servers
// are stateless, so this whole list, tool calls and results included, is sent to App on every turn.
void PrintHistory()
{
    if (agentSession.TryGetInMemoryChatHistory(out var history))
    {
        Console.WriteLine($"\n--- Client history ({history.Count} messages, sent to App on every turn) ---");
        Console.Write(HistoryFormatter.Format(history));
        Console.WriteLine("---");
    }
    else
    {
        Console.WriteLine("\n(no history yet)");
    }
}
