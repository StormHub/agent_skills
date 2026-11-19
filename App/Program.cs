using App.Agents;
using App.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenAI(builder.Configuration);
builder.Services.AddAIAgent(builder.Configuration);
builder.Services.AddAGUIServer();

var app = builder.Build();

// One endpoint per setup, so the same conversation can be tried against each (see README.md).
app.MapAGUIServer("/chat", app.Services.GetRequiredKeyedService<AIAgent>(AgentKeys.Baseline));
app.MapAGUIServer("/chat/subagent", app.Services.GetRequiredKeyedService<AIAgent>(AgentKeys.Subagent));
app.MapAGUIServer("/chat/ephemeral", app.Services.GetRequiredKeyedService<AIAgent>(AgentKeys.Ephemeral));

await app.RunAsync();
