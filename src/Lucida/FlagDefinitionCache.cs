using System.Text.Json;

namespace Lucida;

/// <summary>
/// Lets many workers share one definitions fetch (Redis, a database, files...). Before each poll the client asks
/// <see cref="ShouldFetchFlagDefinitions"/>: when false it adopts <see cref="FlagDefinitions"/> instead of calling the
/// server; the worker that did fetch calls back through <see cref="OnFlagDefinitionsReceived"/>. A provider that throws
/// is treated as "fetch yourself".
/// </summary>
public interface IFlagDefinitionCacheProvider
{
    /// <summary>Cached definitions, or null when nothing is cached.</summary>
    IReadOnlyList<JsonElement>? FlagDefinitions();

    /// <summary>True when this process should call the server (for example it won the fetch lock).</summary>
    bool ShouldFetchFlagDefinitions();

    /// <summary>Stores freshly fetched definitions for the other workers.</summary>
    void OnFlagDefinitionsReceived(IReadOnlyList<JsonElement> definitions);

    /// <summary>Releases locks and connections.</summary>
    void Shutdown();
}

/// <summary>Reference provider: process-local, fetches again once <c>ttl</c> has passed.</summary>
public sealed class InMemoryFlagDefinitionCache(TimeSpan? ttl = null) : IFlagDefinitionCacheProvider
{
    private readonly TimeSpan _ttl = ttl ?? TimeSpan.FromSeconds(30);
    private readonly object _lock = new();
    private IReadOnlyList<JsonElement>? _defs;
    private long _at;

    public IReadOnlyList<JsonElement>? FlagDefinitions() { lock (_lock) return _defs; }

    public bool ShouldFetchFlagDefinitions()
    {
        lock (_lock) return _defs is null || System.Diagnostics.Stopwatch.GetElapsedTime(_at) >= _ttl;
    }

    public void OnFlagDefinitionsReceived(IReadOnlyList<JsonElement> definitions)
    {
        lock (_lock)
        {
            _defs = definitions;
            _at = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public void Shutdown() { }
}
