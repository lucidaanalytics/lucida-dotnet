using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucida;

public sealed class LucidaOptions
{
    public string Host { get; init; } = "https://capture.lucidace.com";
    public int FlushAt { get; init; } = 20;
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxQueueSize { get; init; } = 1000;
    public int MaxRetries { get; init; } = 5;
    public TimeSpan RetryBase { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Secret key (lc_sk_...): needed to load flag definitions.</summary>
    public string? SecretKey { get; init; }
    public bool Disabled { get; init; }
    /// <summary>Inject a handler to observe or fake the network.</summary>
    public HttpMessageHandler? Handler { get; init; }
    /// <summary>Offline test mode: a flag fixture (<c>{"flags": [...]}</c> or a bare array). Events are kept in <see cref="LucidaClient.Captured"/>, nothing is sent.</summary>
    public string? TestModeFixtureJson { get; init; }
    /// <summary>Log delivery and flag activity to <see cref="DebugSink"/> (default: stderr).</summary>
    public bool Debug { get; init; }
    public Action<string>? DebugSink { get; init; }
    /// <summary>Per-request timeout for every HTTP call (default 10s).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Called with the cause of a failed background send or flag fetch; exceptions it throws are swallowed.</summary>
    public Action<Exception>? OnError { get; init; }
    /// <summary>Edit or drop events before they are queued: return null to drop. If it throws, the original event is kept.</summary>
    public Func<JsonObject, JsonObject?>? BeforeSend { get; init; }
    /// <summary>Events per request, independent of <see cref="FlushAt"/> (the flush trigger). Capped at 1000.</summary>
    public int MaxBatchSize { get; init; } = 1000;
    /// <summary>Poll flag definitions in the background (needs <see cref="SecretKey"/>); null disables. Floor 5s.</summary>
    public TimeSpan? FlagPollInterval { get; init; }
    /// <summary>Timeout for flag requests (definitions poll and remote evaluation); null uses <see cref="RequestTimeout"/>.</summary>
    public TimeSpan? FlagTimeout { get; init; }
    /// <summary>Send <c>$geoip_disable=true</c> on every event.</summary>
    public bool DisableGeoip { get; init; }
    /// <summary>Super properties on every event (also <see cref="LucidaClient.Register"/>); context and per-call properties win.</summary>
    public IReadOnlyDictionary<string, object?>? DefaultProperties { get; init; }
    /// <summary>Never ask the server for flags undecidable locally (they read as unknown).</summary>
    public bool OnlyEvaluateLocally { get; init; }
    /// <summary>App contexts of this process; flags scoped to other app contexts are dropped from results. A <see cref="FlagContext"/> may override.</summary>
    public List<string>? AppContexts { get; init; }
    /// <summary>Shares one definitions fetch between workers; see <see cref="IFlagDefinitionCacheProvider"/>.</summary>
    public IFlagDefinitionCacheProvider? FlagDefinitionCache { get; init; }
    /// <summary>
    /// <c>{flagKey: value}</c> (<c>bool</c> or variant <c>string</c>) served with reason <c>"fallback"</c> when a flag cannot be evaluated
    /// (network error, nothing loaded, quota limited, unknown key). Fallback reads send no exposure.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Fallbacks { get; init; }
}

/// <summary>One historical event for <see cref="LucidaClient.ImportEventsAsync"/>. <c>DistinctId</c> and <c>Event</c> are required.</summary>
public sealed record ImportEvent(string DistinctId, string Event, IReadOnlyDictionary<string, object?>? Properties = null,
    DateTimeOffset? Timestamp = null, string? Uuid = null, IReadOnlyDictionary<string, object?>? Set = null, IReadOnlyDictionary<string, object?>? SetOnce = null);

/// <summary>Server SDK. Calls name their distinct id, or inherit it from an open <see cref="LucidaContext"/>. Delivery is at-least-once.</summary>
public sealed partial class LucidaClient : IDisposable
{
    public const string Lib = "lucida-dotnet";
    public const string Version = "0.1.0";
    private const int MinGzipBytes = 1024;
    private const int MaxBatch = 1000;

    private readonly string _apiKey;
    private readonly LucidaOptions _o;
    private readonly HttpClient? _http;
    private readonly ConcurrentQueue<JsonObject> _queue = new();
    private readonly List<JsonObject> _captured = new();
    private readonly Timer? _timer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Reporter _log;
    private readonly FlagSync _sync;
    private readonly IdentityState _identity = new();
    private int _dropped;

    public LucidaClient(string apiKey, LucidaOptions? options = null)
    {
        _apiKey = apiKey;
        _o = options ?? new LucidaOptions();
        _log = new Reporter(_o.Debug, _o.DebugSink, _o.OnError);
        if (_o.TestModeFixtureJson is null && !_o.Disabled)
            _http = new HttpClient(_o.Handler ?? new HttpClientHandler()) { Timeout = Timeout.InfiniteTimeSpan }; // per-request timeouts below
        _sync = new FlagSync(_http, _o.Host, apiKey, _o.SecretKey, _log, _o.FlagTimeout ?? _o.RequestTimeout) { Cache = _o.FlagDefinitionCache };
        if (_o.DefaultProperties is not null) _identity.Register(_o.DefaultProperties);
        if (_o.TestModeFixtureJson is { } fx) _sync.Set(fx);
        if (_http is null) return;
        _timer = new Timer(_ => _ = FlushAsync(), null, _o.FlushInterval, _o.FlushInterval);
        if (_o.FlagPollInterval is { } poll) _sync.StartPolling(poll);
    }

    public bool IsTestMode => _o.TestModeFixtureJson is not null;
    /// <summary>Test mode only: every captured event, including exposures.</summary>
    public IReadOnlyList<JsonObject> Captured { get { lock (_captured) return _captured.ToList(); } }
    /// <summary>Events discarded because the queue was full or the server kept failing.</summary>
    public int DroppedEvents => Volatile.Read(ref _dropped);

    // events ---------------------------------------------------------------------------------

    /// <summary>
    /// A null <paramref name="distinctId"/> falls back to the open <see cref="LucidaContext"/>, then (personless only) a random UUID v4.
    /// <paramref name="personless"/> sends <c>$process_person_profile=false</c>: no person is created or updated and <c>$set</c> is ignored by the server.
    /// </summary>
    public void Capture(string? distinctId, string eventName, IReadOnlyDictionary<string, object?>? properties = null,
        IReadOnlyDictionary<string, string>? groups = null, DateTimeOffset? timestamp = null, string? uuid = null,
        IReadOnlyDictionary<string, object?>? set = null, IReadOnlyDictionary<string, object?>? setOnce = null, bool personless = false,
        FlagSnapshot? flags = null, bool sendFeatureFlags = false)
    {
        var ctx = LucidaContext.Current;
        var id = distinctId ?? ctx?.DistinctId ?? (personless ? Guid.NewGuid().ToString() : null)
            ?? throw new ArgumentException("distinctId is required (or open a LucidaContext)", nameof(distinctId));
        var props = new JsonObject();
        _identity.MergeSuper(props);
        if (ctx is not null)
        {
            if (ctx.SessionId is not null) props["$session_id"] = ctx.SessionId;
            Merge(props, ctx.Props);
        }
        Merge(props, properties); // explicit args win
        var flagValues = flags?.Values ?? (sendFeatureFlags ? LocalValues(new FlagContext { DistinctId = id, Groups = groups?.ToDictionary(g => g.Key, g => g.Value) }) : null);
        if (flagValues is not null)
            foreach (var (k, v) in FlagSnapshot.EventProps(null, flagValues)) props.TryAdd(k, JsonSerializer.SerializeToNode(v));
        if (personless) props["$process_person_profile"] = false;
        if (_o.DisableGeoip) props["$geoip_disable"] = true;
        JsonObject? e = Wire(id, eventName, props, timestamp, uuid, set, setOnce);
        if (groups is { Count: > 0 }) e["$groups"] = JsonSerializer.SerializeToNode(groups);
        if (_o.BeforeSend is { } hook)
        {
            try { e = hook((JsonObject)e.DeepClone()); }
            catch (Exception ex) { _log.Error("BeforeSend threw; keeping the original event", ex); }
            if (e is null) return;
        }
        Enqueue(e);
    }

    /// <summary>Capture with no distinct id and no person profile: a random UUID v4 id is generated.</summary>
    public void CapturePersonless(string eventName, IReadOnlyDictionary<string, object?>? properties = null) =>
        Capture(null, eventName, properties, personless: true);

    private static void Merge(JsonObject into, IReadOnlyDictionary<string, object?>? from)
    {
        if (from is null) return;
        foreach (var kv in from) into[kv.Key] = JsonSerializer.SerializeToNode(kv.Value);
    }

    private static JsonObject Wire(string id, string eventName, JsonObject props, DateTimeOffset? ts, string? uuid,
        IReadOnlyDictionary<string, object?>? set, IReadOnlyDictionary<string, object?>? setOnce)
    {
        props["$lib"] = Lib;
        props["$lib_version"] = Version;
        var e = new JsonObject
        {
            ["uuid"] = uuid ?? Uuid7(),
            ["event"] = eventName,
            ["distinct_id"] = id,
            ["timestamp"] = Iso(ts),
            ["properties"] = props,
        };
        if (set is { Count: > 0 }) e["$set"] = JsonSerializer.SerializeToNode(set);
        if (setOnce is { Count: > 0 }) e["$set_once"] = JsonSerializer.SerializeToNode(setOnce);
        return e;
    }

    /// <summary>
    /// Historical import through <c>POST /v1/s2s/batch</c> with the secret key; timestamps are kept. Chunks of
    /// <see cref="LucidaOptions.MaxBatchSize"/>; returns the number accepted. In test mode the events land in <see cref="Captured"/>.
    /// </summary>
    /// <exception cref="LucidaException">Without a secret key, or on the first rejected chunk.</exception>
    public async Task<int> ImportEventsAsync(IEnumerable<ImportEvent> events)
    {
        if (_o.SecretKey is null) throw new LucidaException("ImportEventsAsync requires SecretKey");
        if (_o.Disabled) return 0;
        var wire = events.Select(ev => Wire(ev.DistinctId, ev.Event, Props(ev.Properties), ev.Timestamp, ev.Uuid, ev.Set, ev.SetOnce)).ToList();
        if (IsTestMode)
        {
            lock (_captured) _captured.AddRange(wire);
            return wire.Count;
        }
        var size = BatchSize;
        var sent = 0;
        for (var i = 0; i < wire.Count; i += size)
        {
            var chunk = wire.Skip(i).Take(size).ToList();
            var payload = new JsonObject { ["batch"] = new JsonArray(chunk.Select(e => (JsonNode?)e.DeepClone()).ToArray()), ["sent_at"] = Iso(null) };
            var (status, _) = await PostAsync("/v1/s2s/batch", payload.ToJsonString(), _o.SecretKey).ConfigureAwait(false);
            if (status is < 200 or >= 300) throw new LucidaException($"import failed: status {status} after {sent} events");
            sent += chunk.Count;
        }
        return sent;
    }

    private static JsonObject Props(IReadOnlyDictionary<string, object?>? p)
    {
        var o = new JsonObject();
        Merge(o, p);
        return o;
    }

    private int BatchSize => Math.Max(1, Math.Min(MaxBatch, _o.MaxBatchSize));

    /// <summary>An identical call (same id and properties) already sent by this client is skipped.</summary>
    public void Identify(string distinctId, IReadOnlyDictionary<string, object?>? properties = null, IReadOnlyDictionary<string, object?>? propertiesSetOnce = null)
    {
        if (_identity.First("identify", distinctId, properties, propertiesSetOnce))
            Capture(distinctId, "$identify", set: properties, setOnce: propertiesSetOnce);
    }

    public void Alias(string distinctId, string alias) =>
        Capture(distinctId, "$create_alias", new Dictionary<string, object?> { ["alias"] = alias, ["distinct_id"] = distinctId });

    public void GroupIdentify(string groupType, string groupKey, IReadOnlyDictionary<string, object?>? properties = null, string? distinctId = null)
    {
        if (!_identity.First("group", groupType, groupKey, distinctId, properties)) return;
        Capture(distinctId ?? $"${groupType}_{groupKey}", "$groupidentify", new Dictionary<string, object?>
        {
            ["$group_type"] = groupType,
            ["$group_key"] = groupKey,
            ["$group_set"] = properties ?? new Dictionary<string, object?>(),
        });
    }

    /// <summary>Like <see cref="Identify"/>, then delivers at once; true when the queue was sent.</summary>
    public Task<bool> IdentifyAsync(string distinctId, IReadOnlyDictionary<string, object?>? properties = null, IReadOnlyDictionary<string, object?>? propertiesSetOnce = null)
    {
        Identify(distinctId, properties, propertiesSetOnce);
        return FlushAsync();
    }

    /// <summary>Like <see cref="Alias"/>, then delivers at once; true when the queue was sent.</summary>
    public Task<bool> AliasAsync(string distinctId, string alias)
    {
        Alias(distinctId, alias);
        return FlushAsync();
    }

    /// <summary>Like <see cref="GroupIdentify"/>, then delivers at once; true when the queue was sent.</summary>
    public Task<bool> GroupIdentifyAsync(string groupType, string groupKey, IReadOnlyDictionary<string, object?>? properties = null, string? distinctId = null)
    {
        GroupIdentify(groupType, groupKey, properties, distinctId);
        return FlushAsync();
    }

    private void Enqueue(JsonObject e)
    {
        if (_o.Disabled) return;
        if (IsTestMode)
        {
            lock (_captured) _captured.Add(e);
            return;
        }
        if (_queue.Count >= _o.MaxQueueSize)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(e);
        if (_queue.Count >= _o.FlushAt) _ = FlushAsync();
    }

    /// <summary>Sends everything queued. Returns false if events were dropped after exhausting retries.</summary>
    public async Task<bool> FlushAsync()
    {
        if (_http is null) return true;
        await _flushLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var ok = true;
            while (!_queue.IsEmpty)
            {
                var batch = new List<JsonObject>();
                while (batch.Count < BatchSize && _queue.TryDequeue(out var e)) batch.Add(e);
                if (!await SendWithRetryAsync(batch).ConfigureAwait(false))
                {
                    Interlocked.Add(ref _dropped, batch.Count);
                    ok = false;
                }
            }
            return ok;
        }
        finally { _flushLock.Release(); }
    }

    private async Task<bool> SendWithRetryAsync(List<JsonObject> batch)
    {
        var payload = new JsonObject { ["batch"] = new JsonArray(batch.Select(e => (JsonNode?)e.DeepClone()).ToArray()), ["sent_at"] = Iso(null), ["api_key"] = _apiKey };
        for (var attempt = 0; ; attempt++)
        {
            var (status, retryAfter) = await PostAsync("/v1/batch", payload.ToJsonString(), null).ConfigureAwait(false);
            if (status is >= 200 and < 300) return true;
            var retryable = status is 0 or 408 or 429 or >= 500;
            if (!retryable || attempt >= _o.MaxRetries)
            {
                _log.Error($"dropping {batch.Count} events: status {status}");
                return false;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, _o.RetryBase.TotalMilliseconds * Math.Pow(2, attempt)) * (0.5 + 0.5 * Random.Shared.NextDouble()));
            await Task.Delay(retryAfter is { } ra && ra > delay ? ra : delay).ConfigureAwait(false);
        }
    }

    /// <summary>JSON body; gzip above 1 KB flagged with <c>?compression=gzip</c>.</summary>
    private async Task<(int Status, TimeSpan? RetryAfter)> PostAsync(string path, string json, string? bearer)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var url = _o.Host.TrimEnd('/') + path;
        if (bytes.Length >= MinGzipBytes)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(bytes);
            bytes = ms.ToArray();
            url += (url.Contains('?') ? "&" : "?") + "compression=gzip";
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var cts = new CancellationTokenSource(_o.RequestTimeout);
            using var res = await _http!.SendAsync(req, cts.Token).ConfigureAwait(false);
            var ra = res.Headers.RetryAfter?.Delta;
            return ((int)res.StatusCode, ra is { } d ? TimeSpan.FromSeconds(Math.Clamp(d.TotalSeconds, 0, 60)) : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            _log.Debug($"post {path} failed: {e.Message}");
            return (0, null);
        }
    }

    // helpers --------------------------------------------------------------------------------

    internal static string Iso(DateTimeOffset? t) => (t ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    internal static string Uuid7()
    {
        Span<byte> b = stackalloc byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 6; i++) b[i] = (byte)(ms >> (8 * (5 - i)));
        b[6] = (byte)(0x70 | (b[6] & 0x0F));
        b[8] = (byte)(0x80 | (b[8] & 0x3F));
        var h = Convert.ToHexString(b).ToLowerInvariant();
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..]}";
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _sync.Dispose();
        FlushAsync().GetAwaiter().GetResult();
        _http?.Dispose();
    }
}
