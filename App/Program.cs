using App.Extensions;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenAI(builder.Configuration);
builder.Services.AddAIAgent(builder.Configuration);
builder.Services.AddAGUIServer();

var app = builder.Build();
var agentBuilder = app.Services.GetRequiredService<AIAgentBuilder>();
var agents = await agentBuilder.Build();

// Three setups side by side, so the same conversation can be replayed against each one (see README.md).
app.MapAGUIServer("/chat", agents.Baseline);            // skills on the agent; loaded skills stay in history
app.MapAGUIServer("/chat/subagent", agents.Subagent);   // approach A: skills isolated in a worker agent
app.MapAGUIServer("/chat/ephemeral", agents.Ephemeral); // approach B: skill content kept for the current turn only

await app.RunAsync();
