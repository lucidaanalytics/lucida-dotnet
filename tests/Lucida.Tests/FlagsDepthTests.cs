using System.Text.Json;
using Xunit;

namespace Lucida.Tests;

/// <summary>Snapshot, event enrichment, definition cache and remote-config payload (LC-FLG-28, 29, 45, 46).</summary>
public class FlagsDepthTests
{
    private static string Flag(string key, string ctxs, int pct) =>
        $$"""{"key":"{{key}}","type":"boolean","active":true,"salt":"s","aggregation":{"kind":"person"},"app_contexts":{{ctxs}},"conditions":[{"rollout_pct":{{pct}}}]}""";

    private static readonly string Defs = "{\"flags\":[" + string.Join(",", Flag("a", "[]", 100), Flag("b", "[]", 100), Flag("off", "[]", 0), Flag("web", "[\"web\"]", 100)) + "]}";

    private static LucidaClient Offline() => new("lc_pk_x", new LucidaOptions { TestModeFixtureJson = Defs, AppContexts = ["api"] });

    [Fact]
    public async Task SnapshotAndEnrichment()
    {
        using var lc = Offline();
        lc.Capture("u", "buy", new Dictionary<string, object?> { ["$feature/a"] = "mine" }, sendFeatureFlags: true);
        var snap = await lc.EvaluateFlagsAsync(new FlagContext { DistinctId = "u" });
        Assert.True(snap.IsEnabled("a"));
        Assert.Equal(["a", "b", "off"], snap.Keys);
        lc.Capture("u", "e1", flags: snap.OnlyAccessed());
        var p0 = lc.Captured[0]["properties"]!.AsObject();
        var p1 = lc.Captured[1]["properties"]!.AsObject();
        Assert.Equal("mine", p0["$feature/a"]!.GetValue<string>());
        Assert.False(p0["$feature/off"]!.GetValue<bool>());
        Assert.False(p0.ContainsKey("$feature/web"));
        Assert.Equal("[\"a\",\"b\"]", p0["$active_feature_flags"]!.ToJsonString());
        Assert.True(p1["$feature/a"]!.GetValue<bool>());
        Assert.False(p1.ContainsKey("$feature/b"));
        Assert.DoesNotContain(lc.Captured, e => e["event"]!.GetValue<string>() == "$feature_flag_called");
    }

    [Fact]
    public void EventPropsCallerWins()
    {
        Assert.Equal("x", Assert.Single(FlagSnapshot.EventProps(new Dictionary<string, object?> { ["x"] = 1 }, new Dictionary<string, object>())).Key);
        var o = FlagSnapshot.EventProps(new Dictionary<string, object?> { ["$active_feature_flags"] = new List<string>() }, new Dictionary<string, object> { ["a"] = "v" });
        Assert.Empty((List<string>)o["$active_feature_flags"]!);
    }

    [Fact]
    public async Task DefinitionCacheIsAdoptedAndPublished()
    {
        var fake = new FakeHandler();
        var cache = new InMemoryFlagDefinitionCache(TimeSpan.FromMinutes(1));
        cache.OnFlagDefinitionsReceived(JsonDocument.Parse(Defs).RootElement.GetProperty("flags").EnumerateArray().Select(e => e.Clone()).ToList());
        using (var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, Host = "http://lucida.test", SecretKey = "lc_sk_x", FlagDefinitionCache = cache }))
        {
            Assert.True(await lc.ReloadFlagsAsync());
            Assert.Empty(fake.Requests); // fresh cache: no fetch
            Assert.True(lc.IsEnabled("a", new FlagContext { DistinctId = "u" }));
        }
        var empty = new InMemoryFlagDefinitionCache(TimeSpan.FromMinutes(1));
        using (var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, Host = "http://lucida.test", SecretKey = "lc_sk_x", FlagDefinitionCache = empty }))
        {
            Assert.True(await lc.ReloadFlagsAsync());
            Assert.Single(empty.FlagDefinitions()!);
        }
    }

    private sealed class Broken : IFlagDefinitionCacheProvider
    {
        public IReadOnlyList<JsonElement>? FlagDefinitions() => throw new InvalidOperationException("down");
        public bool ShouldFetchFlagDefinitions() => throw new InvalidOperationException("down");
        public void OnFlagDefinitionsReceived(IReadOnlyList<JsonElement> d) => throw new InvalidOperationException("down");
        public void Shutdown() { }
    }

    [Fact]
    public async Task BrokenCacheMeansFetch()
    {
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = new FakeHandler(), Host = "http://lucida.test", SecretKey = "lc_sk_x", FlagDefinitionCache = new Broken() });
        Assert.True(await lc.ReloadFlagsAsync());
        Assert.True(lc.IsEnabled("on", new FlagContext { DistinctId = "u" }));
    }

    [Fact]
    public async Task RemoteConfigPayloadUsesTheSecretKey()
    {
        var fake = new FakeHandler { RemoteConfigReply = "{\"key\":\"a/b\",\"enabled\":true,\"payload\":{\"x\":1}}" };
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, Host = "http://lucida.test", SecretKey = "lc_sk_x" });
        var p = await lc.GetRemoteConfigPayloadAsync("a/b");
        Assert.Equal(1, p!.Value.GetProperty("x").GetInt32());
        var req = fake.Requests.Last(r => r.RequestUri!.AbsolutePath.EndsWith("/remote-config", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("/v1/flags/a%2Fb/remote-config", req.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer lc_sk_x", req.Headers.Authorization!.ToString());
        fake.RemoteConfigReply = null;
        Assert.Null(await lc.GetRemoteConfigPayloadAsync("missing"));
        using var noSecret = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, Host = "http://lucida.test" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => noSecret.GetRemoteConfigPayloadAsync("cfg"));
    }
}
