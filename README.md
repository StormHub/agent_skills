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
dotnet run --project Client   # interactive, talks to /chat
```

Pick a setup with `--Endpoint`, and replay the same conversation with `--Script`:

```bash
dotnet run --project Client -- --Endpoint http://localhost:5000/chat           --Script Client/compare.txt
dotnet run --project Client -- --Endpoint http://localhost:5000/chat/subagent  --Script Client/compare.txt
dotnet run --project Client -- --Endpoint http://localhost:5000/chat/ephemeral --Script Client/compare.txt
```

`Client/compare.txt` loads both skills in the first two turns, then has turns that need no skill, then turns that
need the skills again.

## Reading the comparison

App logs one line per model call (logger `App.Usage`):

```
[ephemeral] model call: 9 messages, ~2140 chars sent (310 in tool results, 236 of them skill content); tokens in/out: 812/95. ephemeral totals after 6 calls: 4630 in / 610 out
```

- **Skill content** is measured on what is actually sent. In the baseline it grows with each skill loaded and stays
  there. In B it drops to the stub size once the turn that loaded the skill is over. In A it is zero on
  `subagent/main` and appears only on `subagent/worker`.
- **Totals** add up every model call for the setup. Subagent totals include the worker's calls. Restart App between
  runs to reset them.
- Token counts come from the provider's usage report; `n/a` means it didn't send one.

The demo skills are small (about 1.2 KB each), so the difference here is modest. It scales with the size of SKILL.md
and with the number of turns after a skill is loaded.

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

- **A depends on the brief.** The worker only sees the request string. `SubagentMainInstructions` tells the main
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

```
Api/                                   MCP server with the skills, plus /catalog and /pricing
App/Extensions/AIAgentBuilder.cs       builds the baseline, subagent and ephemeral agents
App/Agents/EarlierTurnSkillContentChatClient.cs   approach B
App/Agents/UsageLoggingChatClient.cs   per-call logging and totals used for the comparison
Client/Program.cs                      console client (--Endpoint, --Script)
Client/compare.txt                     conversation used for the comparison
```
