using Xunit;

namespace Lucida.Tests;

/// <summary>Init-time fallbacks, the details read and the change / stale hooks (N-EXP-16).</summary>
public class FallbackTests
{
    private static readonly FlagContext U = new() { DistinctId = "u" };
    private static readonly Dictionary<string, object> Fb = new() { ["dark"] = false, ["theme"] = "blue" };

    private sealed class Dead : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => throw new HttpRequestException("down");
    }

    [Fact]
    public async Task NetworkFailureAndMissingFlagServeFallbackWithoutExposure()
    {
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = new Dead(), FlushInterval = TimeSpan.FromHours(1), Fallbacks = Fb });
        var r = await lc.GetFeatureFlagDetailsAsync("theme", U);
        Assert.Equal("blue", r!.Value);
        Assert.Equal("fallback", r.Reason);
        Assert.True(r.IsFallback);
        Assert.Equal(false, await lc.GetFeatureFlagAsync("dark", U));
        Assert.Null(await lc.GetFeatureFlagAsync("other", U)); // no fallback: old behaviour
    }

    [Fact]
    public async Task ServedFlagKeepsServerValueReasonAndRequestId()
    {
        var fake = new FakeHandler
        {
            RemoteReply = "{\"request_id\":\"r9\",\"flags\":{\"dark\":{\"value\":\"b\",\"variant\":\"b\",\"reason\":\"condition_match\",\"condition_index\":2}}}",
        };
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1), Fallbacks = Fb });
        var r = await lc.GetFeatureFlagDetailsAsync("dark", U);
        Assert.Equal("b", r!.Value);
        Assert.Equal("condition_match", r.Reason);
        Assert.Equal(2, r.ConditionIndex);
        Assert.Equal("r9", r.RequestId);
        Assert.False(r.EvaluatedLocally);
    }

    [Fact]
    public async Task ChangeAndStaleHooksFire()
    {
        var fake = new FakeHandler();
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, SecretKey = "lc_sk_x", FlushInterval = TimeSpan.FromHours(1) });
        int changed = 0, stale = 0;
        lc.FlagsChanged += () => changed++;
        lc.FlagsStale += () => stale++;
        Assert.True(await lc.ReloadFlagsAsync());
        Assert.Equal((1, 0), (changed, stale));
        using var dead = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = new Dead(), SecretKey = "lc_sk_x", FlushInterval = TimeSpan.FromHours(1) });
        dead.FlagsStale += () => stale++;
        Assert.False(await dead.ReloadFlagsAsync());
        Assert.Equal(1, stale);
    }
}
