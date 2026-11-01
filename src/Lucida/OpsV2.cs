using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lucida;

/// <summary>Spec v2 operators (testdata/flags/README.md, "Spec v2"); port of go/internal/flageval/ops_v2.go.</summary>
internal static class OpsV2
{
    private static readonly Regex Rel = new(@"^-?(\d{1,6})([hdwmy])$", RegexOptions.Compiled);
    private static readonly Regex Rfc3339 = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);
    private static readonly Regex DateOnlyRx = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    public static long? RelativeSeconds(string s)
    {
        var m = Rel.Match(s.Trim());
        if (!m.Success) return null;
        var unit = m.Groups[2].Value switch { "h" => 3600L, "d" => 86_400L, "w" => 7 * 86_400L, "m" => 30 * 86_400L, _ => 365 * 86_400L };
        return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * unit;
    }

    /// <summary>The evaluation clock: <c>Context.now</c> (RFC 3339) when it parses, else the wall clock.</summary>
    public static DateTimeOffset ParseNow(string? s) => s is not null && Rfc(s) is { } t ? t : DateTimeOffset.UtcNow;

    private static DateTimeOffset? Rfc(string s) =>
        Rfc3339.IsMatch(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    private static DateTimeOffset? Instant(JsonElement v, DateTimeOffset now, bool rel)
    {
        if (v.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.UnixEpoch.AddTicks((long)Math.Round(v.GetDouble() * TimeSpan.TicksPerSecond));
        if (v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()!.Trim();
        if (rel && RelativeSeconds(s) is { } secs) return now.AddSeconds(-secs);
        if (Rfc(s) is { } t) return t;
        if (DateOnlyRx.IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            return new DateTimeOffset(d, TimeSpan.Zero);
        return null;
    }

    private static int CmpParts(BigInteger[] a, BigInteger[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Half-open [lo, hi) of a tilde, caret or wildcard value; null when the value is unusable.</summary>
    public static (BigInteger[] Lo, BigInteger[] Hi)? SemverRange(string op, JsonElement want)
    {
        if (want.ValueKind != JsonValueKind.String) return null;
        var s = want.GetString()!.Trim();
        if (s.StartsWith('v')) s = s[1..];
        var parts = s.Split('.');
        if (op == "semver_wildcard")
        {
            if (parts[^1] is not ("*" or "x" or "X")) return null;
            if (parts.Length == 1) return ([0], [1 << 30]);
            if (Evaluator.Semver(JsonSerializer.SerializeToElement(string.Join('.', parts[..^1]))) is not { } lo) return null;
            var hi = (BigInteger[])lo.Clone();
            hi[^1] += 1;
            return (lo, hi);
        }
        if (Evaluator.Semver(want) is not { } p0) return null;
        var p = p0.Concat(Enumerable.Repeat(BigInteger.Zero, Math.Max(0, 3 - p0.Length))).ToArray();
        BigInteger[] top;
        if (op == "semver_tilde" && parts.Length == 1) top = [p[0] + 1, 0, 0];
        else if (op == "semver_tilde" || (p[0].IsZero && p[1] > 0)) top = [p[0], p[1] + 1, 0];
        else if (p[0] > 0) top = [p[0] + 1, 0, 0];
        else top = [0, 0, p[2] + 1];
        return (p, top);
    }

    /// <summary>Applies a v2 operator to a set property. Unknown operators never match.</summary>
    public static bool Match(string op, JsonElement prop, JsonElement want, DateTimeOffset now)
    {
        switch (op)
        {
            case "icontains" or "not_icontains" or "starts_with" or "not_starts_with" or "ends_with" or "not_ends_with":
                if (prop.ValueKind != JsonValueKind.String || want.ValueKind != JsonValueKind.String) return false;
                var (s, w) = (prop.GetString()!, want.GetString()!);
                if (op is "icontains" or "not_icontains") return s.ToLowerInvariant().Contains(w.ToLowerInvariant(), StringComparison.Ordinal) == (op == "icontains");
                if (op is "starts_with" or "not_starts_with") return s.StartsWith(w, StringComparison.Ordinal) == (op == "starts_with");
                return s.EndsWith(w, StringComparison.Ordinal) == (op == "ends_with");
            case "between" or "not_between":
                if (Evaluator.ToFloat(prop) is not { } x || want.ValueKind != JsonValueKind.Array || want.GetArrayLength() != 2) return false;
                var bounds = want.EnumerateArray().ToArray();
                if (bounds.Any(b => b.ValueKind != JsonValueKind.Number)) return false;
                return (x >= bounds[0].GetDouble() && x <= bounds[1].GetDouble()) == (op == "between");
            case "is_date_before" or "is_date_after":
                if (Instant(prop, now, false) is not { } pi || Instant(want, now, true) is not { } wi) return false;
                return op == "is_date_before" ? pi < wi : pi > wi;
            case "semver_eq" or "semver_neq" or "semver_gt" or "semver_gte" or "semver_lt" or "semver_lte":
                if (Evaluator.CmpSemver(prop, want) is not { } c) return false;
                return op switch { "semver_eq" => c == 0, "semver_neq" => c != 0, "semver_gt" => c > 0, "semver_gte" => c >= 0, "semver_lt" => c < 0, _ => c <= 0 };
            case "semver_tilde" or "semver_caret" or "semver_wildcard":
                return Evaluator.Semver(prop) is { } v && SemverRange(op, want) is { } r && CmpParts(v, r.Lo) >= 0 && CmpParts(v, r.Hi) < 0;
        }
        return false;
    }
}
