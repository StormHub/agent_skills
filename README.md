# Agent Skills demo: keeping skill content out of the history

A shopping assistant built with Microsoft Agent Framework. The skills (`catalog-search`, `pricing`) are served over
MCP by **Api**, the agent runs in **App** behind an AG-UI endpoint, and **Client** is a console chat.

By default a skill the model loads with `load_skill` comes back as an ordinary tool result. That result then
stays in the conversation and is sent to the model again on every later turn. App serves three setups side by side
so you can compare how much each one sends.

| Endpoint | Setup | What happens to loaded skill content |
|---|---|---|
| `/chat` | **Baseline** | Stays in the history and is resent on every later turn. |
| `/chat/subagent` | **A: subagent** | Skills live only on a worker agent that the main agent calls as a tool. The worker runs in a new session per call, so skill content never reaches the main conversation; only the worker's reply does. |
| `/chat/ephemeral` | **B: current turn only** | Skills stay on the main agent. Before each model call, skill content loaded in *earlier* turns is replaced with a one-line stub. The model reloads a skill when a later turn needs it. |

## Where the history lives

AG-UI is stateless. App keeps no conversation. The **Client** keeps the history, including the tool calls and results
App streams back, and sends all of it on every request. That is why neither approach uses a server-side
`ChatHistoryProvider`:

- **A** never produces skill content in the main conversation, so there is nothing for the Client to keep.
- **B** filters at the last moment, in an `IChatClient` decorator just before the model
  (`App/Agents/EarlierTurnSkillContentChatClient.cs`). The Client still keeps and uploads the full text, but the
  model doesn't see old skill content.

## Run it

```bash
dotnet run --project Api      # http://localhost:5288 (skills over MCP, plus /catalog and /pricing)
dotnet run --project App      # http://localhost:5000 (set OpenAIClient:* in user secrets or appsettings)
dotnet run --project Client   # console chat against http://localhost:5000/chat
```

The Client targets `/chat`. To try another setup, point it at `/chat/subagent` or `/chat/ephemeral`.

## What the history looks like

Illustrative and abridged; what the model writes will differ. One line per text, tool call or tool result, with
`SKILL` marking skill content. `App/Extensions/HistoryFormatter.cs` renders a message list in this form if you want to
look at one yourself (for example in the debugger: `HistoryFormatter.Format(messages)`).

**Baseline**: after a few turns, both skills are still there in full, on every model call:

```
  [0] user      Show me laptops under 1000.
  [1] assistant -> load_skill(skillName=catalog-search)
  [2] tool      <- load_skill SKILL (1290 chars): --- name: catalog-search description: Search the product catalog ...
  [3] assistant -> http_request(method=GET, path=/catalog?q=laptop&maxPrice=1000)
  [4] tool      <- http_request (210 chars): HTTP 200 [{"id":"p_laptop_dell","name":"Dell XPS 13",...
  [5] assistant Here is a laptop under $1000: Dell XPS 13 ...
  [6] user      Is the Dell one on sale? What's the final price?
  [7] assistant -> load_skill(skillName=pricing)
  [8] tool      <- load_skill SKILL (1180 chars): --- name: pricing description: Get the list price, active promotion ...
  ...
```

**B: current turn only**: what the model receives has the same structure, but skill results from earlier turns are
stubs. The Client's own history still holds the full text, because the stubbing happens in App just before the model.

```
  [2] tool      <- load_skill SKILL (142 chars): [Skill 'catalog-search' was loaded in an earlier turn. Its content was removed ...
  [8] tool      <- load_skill SKILL (135 chars): [Skill 'pricing' was loaded in an earlier turn. Its content was removed ...
```

**A: subagent**: the main conversation (and so the Client's history) never contains skill content, only calls to
the worker and its replies:

```
  [0] user      Show me laptops under 1000.
  [1] assistant -> skill_worker(query=Find laptops priced at or below 1000. Return names, ids and prices.)
  [2] tool      <- skill_worker (96 chars): Dell XPS 13 (id p_laptop_dell), $999.
  [3] assistant Here is a laptop under $1000: Dell XPS 13 ...
  [4] user      Is the Dell one on sale? What's the final price?
  [5] assistant -> skill_worker(query=Get the price and any active discount for product id p_laptop_dell ...)
  ...
```

The worker starts a new, short history on every call, and that is the only place skills are loaded:

```
  [0] user      Get the price and any active discount for product id p_laptop_dell ...
  [1] assistant -> load_skill(skillName=pricing)
  [2] tool      <- load_skill SKILL (1180 chars): ...
```

The demo skills are small (about 1.2 KB each). With real skills the baseline's `SKILL` lines get much bigger, and so
does the difference.

## Trade-offs

| | Baseline | A: subagent | B: current turn only |
|---|---|---|---|
| Skill content resent on later turns | Yes, every turn | Never | Never (stub only) |
| Cost when a later turn needs a skill again | None (already there) | Worker reloads it in its own call | Model reloads it |
| Extra model calls | None | At least one worker call per delegation | None |
| The skill sees the conversation | Yes | No, only the request the main agent writes | Yes |
| Moving parts | None | Worker agent, `AsAIFunction()` | One `IChatClient` decorator |
| Good for | Short chats | Self-contained tasks ("find X", "price Y") | Skills that shape how the agent talks to the user |

Things to watch:

- **A depends on the brief.** The worker only sees the request string. In `SubagentAgent`, `MainInstructions` tells the main
  agent to include product names, ids, budgets and preferences, and `WorkerInstructions` tells the worker to always
  return product ids so follow-ups ("is the second one on sale?") still work.
- **A needs approvals off for the read-only skill tools.** A nested agent called as a tool can't surface an approval
  request. `load_skill` and `read_skill_resource` already have approval disabled here.
- **B keeps the call/result pair** and replaces only the result. Dropping the pair would leave an orphaned tool call,
  which providers reject.
- **B only acts on the history it is given.** If the model provider keeps the history itself (for example the
  Responses API with stored output), the decorator never sees the old messages. This demo uses Chat Completions with
  history sent by the Client, so it works.

## Layout

Each setup is its own keyed `AIAgent` singleton (keys in `AgentKeys`), registered in
`DependencyInjection.AddAIAgent` and mapped to its endpoint in `Program.cs`:

```csharp
var agent = app.Services.GetRequiredKeyedService<AIAgent>(AgentKeys.Ephemeral);
```

```
Api/                                              MCP server with the skills, plus /catalog and /pricing
App/Agents/AgentKeys.cs                           DI keys: baseline, subagent, ephemeral
App/Agents/BaselineAgent.cs                       baseline
App/Agents/SubagentAgent.cs                       approach A
App/Agents/EphemeralAgent.cs                      approach B
App/Agents/EarlierTurnSkillContentChatClient.cs   approach B: stubs earlier skill content
App/Agents/AgentComponents.cs                     shared instructions, skills provider, http_request tool
App/Mcp/McpClientAccessor.cs                      shared MCP client, connected on first use
App/Mcp/DeferredMcpSkillsSource.cs                MCP skills source that waits for that client
App/Extensions/DependencyInjection.cs             registrations
App/Extensions/HistoryFormatter.cs                renders a message list one line per item
Client/                                           console test client
```

The MCP client connects on first use rather than at startup, because DI factories are synchronous. If the Api is not
running, the first request fails and the next one tries again.
