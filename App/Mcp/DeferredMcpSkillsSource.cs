using Microsoft.Agents.AI;

namespace App.Mcp;

/// <summary>
/// The MCP skills source, created on first use. It waits for the shared MCP client, then delegates to
/// <see cref="AgentMcpSkillsSource"/>, so an agent can be built synchronously in a DI factory.
/// </summary>
internal sealed class DeferredMcpSkillsSource(McpClientAccessor clientAccessor, ILoggerFactory? loggerFactory) : AgentSkillsSource
{
    private AgentMcpSkillsSource? _inner;

    public override async Task<IList<AgentSkill>> GetSkillsAsync(
        AgentSkillsSourceContext context, CancellationToken cancellationToken = default)
    {
        var inner = Volatile.Read(ref _inner);
        if (inner is null)
        {
            var client = await clientAccessor.GetAsync(cancellationToken);
            var created = new AgentMcpSkillsSource(client, loggerFactory: loggerFactory);
            inner = Interlocked.CompareExchange(ref _inner, created, null) ?? created;
            if (!ReferenceEquals(inner, created))
            {
                created.Dispose(); // another request got there first
            }
        }

        return await inner.GetSkillsAsync(context, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner?.Dispose();
        }

        base.Dispose(disposing);
    }
}
