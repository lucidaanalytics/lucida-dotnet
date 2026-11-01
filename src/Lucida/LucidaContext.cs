namespace Lucida;

/// <summary>What a request carries. <see cref="Props"/> are event properties (<c>$current_url</c>, <c>$request_path</c>, ...).</summary>
public sealed record LucidaContextData(string? DistinctId, string? SessionId, IReadOnlyDictionary<string, object?> Props);

/// <summary>
/// Per-request ambient identity on an <see cref="AsyncLocal{T}"/> (flows through awaits). <see cref="LucidaClient.Capture"/> fills a
/// missing distinct id, adds <c>$session_id</c> and the request properties from it; explicit arguments win.
/// </summary>
public static class LucidaContext
{
    public const string DistinctIdHeader = "X-Lucida-Distinct-Id";
    public const string SessionIdHeader = "X-Lucida-Session-Id";
    private const int MaxHeader = 1000;
    private static readonly AsyncLocal<LucidaContextData?> Slot = new();

    /// <summary>The open context of this async flow, or null.</summary>
    public static LucidaContextData? Current => Slot.Value;

    /// <summary>Opens a context; dispose to restore the previous one.</summary>
    public static IDisposable With(LucidaContextData data)
    {
        var previous = Slot.Value;
        Slot.Value = data;
        return new Scope(previous);
    }

    public static IDisposable With(string? distinctId, string? sessionId = null, IReadOnlyDictionary<string, object?>? props = null) =>
        With(new LucidaContextData(distinctId, sessionId, props ?? new Dictionary<string, object?>()));

    private sealed class Scope(LucidaContextData? previous) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Slot.Value = previous;
        }
    }

    /// <summary>
    /// Strips ASCII control characters, trims and caps at 1000 chars; empty gives null. These headers are
    /// client-controlled: never use them for authentication or authorisation.
    /// </summary>
    public static string? Sanitize(string? raw)
    {
        if (raw is null) return null;
        var s = new string(raw.Where(c => c >= 0x20 && c != 0x7F).ToArray()).Trim();
        if (s.Length > MaxHeader) s = s[..MaxHeader];
        return s.Length == 0 ? null : s;
    }

    /// <summary>Case-insensitive header read over any header collection, sanitized by <see cref="Sanitize"/>.</summary>
    public static string? ReadHeader(IEnumerable<KeyValuePair<string, string?>>? headers, string name) =>
        Sanitize(headers?.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value);

    /// <summary>
    /// Builds the context. <paramref name="header"/> reads one header (null when absent; case-insensitive for ASP.NET Core).
    /// <paramref name="props"/> are the request facts the framework knows (<c>$current_url</c>, <c>$request_method</c>,
    /// <c>$request_path</c>, <c>$ip</c> = remote address); the user agent comes from the headers when absent. With
    /// <paramref name="trustProxy"/>, <c>$ip</c> becomes the first <c>X-Forwarded-For</c> entry.
    /// </summary>
    public static LucidaContextData FromHeaders(Func<string, string?> header, bool trustProxy, IReadOnlyDictionary<string, object?>? props = null)
    {
        var p = new Dictionary<string, object?>(props ?? new Dictionary<string, object?>());
        if (Sanitize(header("User-Agent")) is { } ua) p.TryAdd("$user_agent", ua);
        if (trustProxy && Sanitize(header("X-Forwarded-For")) is { } xff && xff.Split(',')[0].Trim() is { Length: > 0 } first) p["$ip"] = first;
        return new LucidaContextData(Sanitize(header(DistinctIdHeader)), Sanitize(header(SessionIdHeader)), p);
    }

    public static LucidaContextData FromHeaders(IEnumerable<KeyValuePair<string, string?>> headers, bool trustProxy, IReadOnlyDictionary<string, object?>? props = null)
    {
        var list = headers.ToList();
        return FromHeaders(n => list.FirstOrDefault(h => string.Equals(h.Key, n, StringComparison.OrdinalIgnoreCase)).Value, trustProxy, props);
    }
}
