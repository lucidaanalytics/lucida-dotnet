using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lucida;

/// <summary>Flag definitions state: ETag-aware loading, optional background polling, and the remote <c>/v1/flags</c> call.</summary>
internal sealed class FlagSync(HttpClient? http, string host, string apiKey, string? secretKey, Reporter log, TimeSpan timeout) : IDisposable
{
    /// <summary>Polling faster than this only burns the definitions endpoint; shorter intervals are raised to it.</summary>
    public static readonly TimeSpan MinPoll = TimeSpan.FromSeconds(5);

    private readonly string _host = host.TrimEnd('/');
    private Timer? _poller;
    private int _reloading;
    private string? _etag;
    private readonly TaskCompletionSource _firstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Raised after definitions are (re)loaded with a change (first load included).</summary>
    public event Action? Changed;

    /// <summary>Raised when a definitions reload fails.</summary>
    public event Action? Stale;

    private static void Fire(Action? hooks)
    {
        if (hooks is null) return;
        foreach (var h in hooks.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch (Exception) { /* a listener never breaks the reload loop */ }
        }
    }

    /// <summary>Waits for the first fetch attempt; true when definitions are loaded (or nothing is polled).</summary>
    public async Task<bool> AwaitAsync(TimeSpan max)
    {
        if (http is null || secretKey is null || _poller is null) return true;
        await Task.WhenAny(_firstAttempt.Task, Task.Delay(max)).ConfigureAwait(false);
        return Loaded;
    }

    /// <summary>Shares one definitions fetch between workers; see <see cref="IFlagDefinitionCacheProvider"/>.</summary>
    public IFlagDefinitionCacheProvider? Cache { get; set; }

    public IReadOnlyList<JsonElement> Definitions { get; private set; } = [];
    public bool Loaded { get; private set; }
    public bool CanFetchRemote => http is not null;

    public static TimeSpan ClampInterval(TimeSpan d) => d < MinPoll ? MinPoll : d;

    public void Set(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("flags", out var f) ? f : root;
        if (arr.ValueKind != JsonValueKind.Array) throw new ArgumentException("flag definitions must be {\"flags\": [...]} or an array");
        Definitions = arr.EnumerateArray().Select(e => e.Clone()).ToList();
        Loaded = true;
    }

    /// <summary>One conditional fetch; true when the definitions are current (new body or 304). Keeps the old ones on failure.</summary>
    public async Task<bool> ReloadAsync()
    {
        if (http is null || secretKey is null) return false;
        var ok = await FetchAsync().ConfigureAwait(false);
        _firstAttempt.TrySetResult();
        if (!ok) Fire(Stale);
        return ok;
    }

    private async Task<bool> FetchAsync()
    {
        if (http is not { } client) return false; // ReloadAsync checks too, but the compiler cannot see across methods
        if (Adopt() is { } cached)
        {
            Definitions = cached;
            Loaded = true;
            Fire(Changed);
            return true;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, _host + "/v1/flags/definitions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        if (_etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var res = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotModified) return true;
            if (!res.IsSuccessStatusCode)
            {
                log.Error($"flag definitions fetch failed: status {(int)res.StatusCode}");
                return false;
            }
            Set(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            _etag = res.Headers.ETag?.ToString();
            Publish();
            Fire(Changed);
            log.Debug($"flag definitions loaded ({Definitions.Count})");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ArgumentException)
        {
            log.Error("flag definitions fetch failed", e);
            return false;
        }
    }

    /// <summary>Definitions another worker already fetched, or null when this process must fetch. A throwing provider means "fetch".</summary>
    private IReadOnlyList<JsonElement>? Adopt()
    {
        try { return Cache is { } c && !c.ShouldFetchFlagDefinitions() ? c.FlagDefinitions() : null; }
        catch (Exception e) { log.Error("flag definition cache failed", e); return null; }
    }

    private void Publish()
    {
        try { Cache?.OnFlagDefinitionsReceived(Definitions); }
        catch (Exception e) { log.Error("flag definition cache failed", e); }
    }

    /// <summary>Polls at <paramref name="interval"/> (floored at <see cref="MinPoll"/>); the first fetch runs at once. Stops on dispose.</summary>
    public void StartPolling(TimeSpan interval)
    {
        if (http is null || secretKey is null || _poller is not null) return;
        _poller = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _reloading, 1) == 1) return; // never overlap two fetches
            _ = ReloadAsync().ContinueWith(_ => Volatile.Write(ref _reloading, 0), TaskScheduler.Default);
        }, null, TimeSpan.Zero, ClampInterval(interval));
    }

    /// <summary>Asks the server for flags; <paramref name="keys"/> null means all. Empty on any failure.</summary>
    public async Task<Dictionary<string, FlagResult>> RemoteAsync(FlagContext ctx, IReadOnlyList<string>? keys)
    {
        var out_ = new Dictionary<string, FlagResult>();
        if (http is null) return out_;
        return await PostAsync(RemoteBody(ctx, keys)).ConfigureAwait(false);
    }

    internal JsonObject RemoteBody(FlagContext ctx, IReadOnlyList<string>? keys)
    {
        var body = new JsonObject
        {
            ["api_key"] = apiKey,
            ["distinct_id"] = ctx.DistinctId,
            ["groups"] = JsonSerializer.SerializeToNode(ctx.Groups ?? []),
            ["person_properties"] = JsonSerializer.SerializeToNode(ctx.PersonProperties ?? []),
            ["group_properties"] = JsonSerializer.SerializeToNode(ctx.GroupProperties ?? []),
        };
        if (keys is { Count: > 0 }) body["flag_keys"] = JsonSerializer.SerializeToNode(keys);
        if (ctx.AppContexts is { Count: > 0 }) body["app_contexts"] = JsonSerializer.SerializeToNode(ctx.AppContexts);
        if (!string.IsNullOrEmpty(ctx.AnonDistinctId)) body["anon_distinct_id"] = ctx.AnonDistinctId;
        return body;
    }

    /// <summary>Payload of a remote-config flag (the server decrypts encrypted ones for secret keys); null when unknown or on failure.</summary>
    public async Task<JsonElement?> RemoteConfigAsync(string key)
    {
        if (http is null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, _host + "/v1/flags/" + Uri.EscapeDataString(key) + "/remote-config");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secretKey);
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // unknown key or not a remote-config flag
            if (!res.IsSuccessStatusCode)
            {
                log.Error($"remote config failed: status {(int)res.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null ? p.Clone() : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Error("remote config failed", e);
            return null;
        }
    }

    private async Task<Dictionary<string, FlagResult>> PostAsync(JsonObject body)
    {
        var out_ = new Dictionary<string, FlagResult>();
        if (http is null) return out_;
        using var req = new HttpRequestMessage(HttpMethod.Post, _host + "/v1/flags") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var res = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                log.Error($"remote flags failed: status {(int)res.StatusCode}");
                return out_;
            }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync().ConfigureAwait(false));
            var rid = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Object)
                foreach (var f in flags.EnumerateObject()) out_[f.Name] = ParseRemote(f.Value, rid);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.Error("remote flags failed", e);
        }
        return out_;
    }

    internal static FlagResult ParseRemote(JsonElement f, string? requestId = null)
    {
        string? variant = f.TryGetProperty("variant", out var vt) && vt.ValueKind == JsonValueKind.String ? vt.GetString() : null;
        object value;
        if (f.TryGetProperty("value", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) value = v.GetBoolean();
        else if (v.ValueKind == JsonValueKind.String) value = v.GetString()!;
        else if (variant is not null) value = variant;
        else value = f.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True;
        JsonElement? payload = f.TryGetProperty("payload", out var p) && p.ValueKind != JsonValueKind.Null ? p.Clone() : null;
        var reason = f.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String && rs.GetString() is { Length: > 0 } rr ? rr : "remote";
        var idx = f.TryGetProperty("condition_index", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : -1;
        return new FlagResult(value, reason, idx, value as string ?? variant, payload, requestId, false);
    }

    public void Dispose()
    {
        _poller?.Dispose();
        try { Cache?.Shutdown(); }
        catch (Exception e) { log.Error("flag definition cache shutdown failed", e); }
    }
}
