using ModelContextProtocol.Client;

namespace App.Mcp;

/// <summary>
/// Creates the MCP client for the Api on first use and shares it. Connecting is asynchronous, and DI factories are
/// not, so agents are registered with <see cref="DeferredMcpSkillsSource"/>, which asks for the client only when
/// skills are first needed. A failed attempt is not cached, so the next request tries again.
/// </summary>
internal sealed class McpClientAccessor(Uri endpoint, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile McpClient? _client;

    public async ValueTask<McpClient> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_client is { } client)
        {
            return client;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _client ??= await McpClient.CreateAsync(
                new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Name = "api",
                        Endpoint = endpoint,
                    },
                    httpClientFactory.CreateClient(nameof(McpClient)),
                    loggerFactory),
                loggerFactory: loggerFactory,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
        {
            await client.DisposeAsync();
        }

        _lock.Dispose();
    }
}
