using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lucida.Tests;

/// <summary>Polling, remote fallback, payload accessors, delivery hooks, import, personless events and request context.</summary>
public class ParityTests
{
    /// <summary>"on" is local; "server" needs a cohort (undecidable locally); "cfg" is a json flag with a payload.</summary>
    private const string Defs = "{\"flags\":["
        + "{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]},"
        + "{\"key\":\"cfg\",\"type\":\"json\",\"active\":true,\"salt\":\"s\",\"payload\":{\"a\":1},\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]},"
        + "{\"key\":\"server\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"conditions\":[{\"filters\":[{\"type\":\"cohort\",\"id\":\"c1\"}],\"rollout_pct\":100}]}"
        + "]}";
    private const string Remote = "{\"request_id\":\"r1\",\"flags\":{\"server\":{\"value\":\"b\",\"payload\":{\"x\":2}},\"extra\":{\"enabled\":true}}}";
    private static readonly FlagContext U1 = new() { DistinctId = "u1" };

    private static LucidaClient Client(FakeHandler fake, Func<LucidaOptions, LucidaOptions>? tweak = null)
    {
        var o = new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1), RetryBase = TimeSpan.FromMilliseconds(1) };
        var lc = new LucidaClient("lc_pk_x", tweak?.Invoke(o) ?? o);
        lc.SetFlagDefinitions(Defs);
        return lc;
    }

    private static LucidaClient TestMode(LucidaOptions? o = null) => new("k", new LucidaOptions
    {
        TestModeFixtureJson = Defs,
        BeforeSend = o?.BeforeSend,
        OnError = o?.OnError,
    });

    [Fact]
    public async Task PollingFetchesAtOnceFloorsTheIntervalAndSendsIfNoneMatch()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), FlagSync.ClampInterval(TimeSpan.FromMilliseconds(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), FlagSync.ClampInterval(TimeSpan.FromMinutes(1)));
        var fake = new FakeHandler();
        int calls;
        using (var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, SecretKey = "lc_sk_x", FlagPollInterval = TimeSpan.FromMilliseconds(1), FlushInterval = TimeSpan.FromHours(1) }))
        {
            for (var i = 0; i < 100 && fake.IfNoneMatch.Count == 0; i++) await Task.Delay(20);
            Assert.True(lc.IsEnabled("on", U1));
            Assert.True(await lc.ReloadFlagsAsync()); // 304 still counts as current
            calls = fake.IfNoneMatch.Count;
        }
        Assert.Null(fake.IfNoneMatch[0]);
        Assert.Contains("\"v1\"", fake.IfNoneMatch);
        await Task.Delay(150);
        Assert.Equal(calls, fake.IfNoneMatch.Count); // stopped on dispose
    }

    [Fact]
    public async Task UndecidableFlagsFallBackToTheServerUnlessOnlyLocal()
    {
        var fake = new FakeHandler { RemoteReply = Remote };
        using (var lc = Client(fake))
        {
            var r = await lc.GetFlagResultAsync("server", U1);
            Assert.Equal("b", r!.Value);
            Assert.Equal("remote", r.Reason);
            Assert.Equal(2, r.Payload!.Value.GetProperty("x").GetInt32());
            Assert.Equal("b", await lc.GetFeatureFlagAsync("server", U1));
            var body = fake.Bodies[0];
            Assert.Equal("lc_pk_x", body["api_key"]!.GetValue<string>());
            Assert.Equal("u1", body["distinct_id"]!.GetValue<string>());
            Assert.Equal("server", body["flag_keys"]![0]!.GetValue<string>());
            var before = fake.Bodies.Count;
            Assert.Equal(true, await lc.GetFeatureFlagAsync("on", U1)); // local, no call
            Assert.Equal(before, fake.Bodies.Count);
            Assert.Null(await lc.GetFlagResultAsync("server", U1, onlyEvaluateLocally: true));
        }
        var n = fake.Bodies.Count;
        using var local = Client(fake, o => new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1), OnlyEvaluateLocally = true });
        Assert.Null(await local.GetFeatureFlagAsync("server", U1));
        Assert.Equal(n, fake.Bodies.Count);
    }

    [Fact]
    public async Task GetAllFlagsMergesLocalAndRemoteAndPayloads()
    {
        var fake = new FakeHandler { RemoteReply = Remote };
        using var lc = Client(fake);
        var all = await lc.GetAllFlagsAsync(U1);
        Assert.Equal(true, all["on"]);
        Assert.Equal("b", all["server"]);
        Assert.Equal(true, all["extra"]);
        var fp = await lc.GetAllFlagsAndPayloadsAsync(U1);
        Assert.Equal(1, fp.Payloads["cfg"].GetProperty("a").GetInt32());
        Assert.Equal(2, fp.Payloads["server"].GetProperty("x").GetInt32());
        Assert.False(fp.Payloads.ContainsKey("on"));
        Assert.Null(await lc.GetFeatureFlagPayloadAsync("on", U1));
    }

    [Fact]
    public async Task TestModeNeverCallsTheServerForFlags()
    {
        using var lc = TestMode();
        Assert.Null(await lc.GetFeatureFlagAsync("server", U1));
        Assert.Equal(true, (await lc.GetAllFlagsAsync(U1))["on"]);
    }

    [Fact]
    public void BeforeSendEditsDropsAndKeepsTheOriginalWhenItThrows()
    {
        var errors = new List<Exception>();
        using var lc = TestMode(new LucidaOptions
        {
            OnError = errors.Add,
            BeforeSend = e => e["event"]!.GetValue<string>() switch
            {
                "drop" => null,
                "boom" => throw new InvalidOperationException("x"),
                _ => Scrub(e),
            },
        });
        lc.Capture("u", "edit");
        lc.Capture("u", "drop");
        lc.Capture("u", "boom");
        Assert.Equal(["edit", "boom"], lc.Captured.Select(e => e["event"]!.GetValue<string>()));
        Assert.True(lc.Captured[0]["properties"]!["scrubbed"]!.GetValue<bool>());
        Assert.Null(lc.Captured[1]["properties"]!["scrubbed"]);
        Assert.Single(errors);
    }

    private static JsonObject Scrub(JsonObject e)
    {
        e["properties"]!["scrubbed"] = true;
        return e;
    }

    [Fact]
    public async Task MaxBatchSizeIsIndependentOfFlushAtAndFailuresReachOnError()
    {
        var fake = new FakeHandler();
        using (var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, FlushAt = 100, MaxBatchSize = 2, FlushInterval = TimeSpan.FromHours(1) }))
        {
            for (var i = 0; i < 5; i++) lc.Capture("u", "e" + i);
            Assert.True(await lc.FlushAsync());
        }
        Assert.Equal([2, 2, 1], fake.Bodies.Select(b => b["batch"]!.AsArray().Count));
        fake.Statuses.Enqueue(HttpStatusCode.BadRequest);
        var errors = new List<Exception>();
        using var bad = new LucidaClient("k", new LucidaOptions { Handler = fake, OnError = errors.Add, FlushInterval = TimeSpan.FromHours(1) });
        bad.Capture("u", "e");
        Assert.False(await bad.FlushAsync());
        Assert.Single(errors);
    }

    [Fact]
    public async Task ImportEventsUsesTheSecretKeyKeepsTimestampsAndChunks()
    {
        var fake = new FakeHandler();
        using (var noKey = new LucidaClient("k", new LucidaOptions { Handler = fake }))
            await Assert.ThrowsAsync<LucidaException>(() => noKey.ImportEventsAsync([]));
        var t = DateTimeOffset.Parse("2020-01-02T03:04:05.678Z");
        var evs = Enumerable.Range(0, 3).Select(i => new ImportEvent($"u{i}", "old", new Dictionary<string, object?> { ["n"] = i }, t)).ToList();
        using var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, SecretKey = "lc_sk_x", MaxBatchSize = 2, FlushInterval = TimeSpan.FromHours(1) });
        Assert.Equal(3, await lc.ImportEventsAsync(evs));
        Assert.Equal(2, fake.Bodies.Count);
        Assert.All(fake.Requests, r => Assert.Equal("/v1/s2s/batch", r.RequestUri!.AbsolutePath));
        Assert.All(fake.Requests, r => Assert.Equal("Bearer lc_sk_x", r.Headers.Authorization!.ToString()));
        Assert.Null(fake.Bodies[0]["api_key"]);
        Assert.Equal("2020-01-02T03:04:05.678Z", fake.Bodies[0]["batch"]![0]!["timestamp"]!.GetValue<string>());
        fake.Statuses.Enqueue(HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<LucidaException>(() => lc.ImportEventsAsync(evs));
    }

    [Fact]
    public async Task AsyncIdentifyAliasAndGroupIdentifyDeliverAtOnce()
    {
        var fake = new FakeHandler();
        using var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1), FlushAt = 100 });
        Assert.True(await lc.IdentifyAsync("u", new Dictionary<string, object?> { ["plan"] = "pro" }));
        Assert.True(await lc.AliasAsync("u", "alt"));
        Assert.True(await lc.GroupIdentifyAsync("org", "acme"));
        Assert.Equal(["$identify", "$create_alias", "$groupidentify"], fake.Bodies.Select(b => b["batch"]![0]!["event"]!.GetValue<string>()));
    }

    [Fact]
    public void PersonlessEventsFlagThePropertyAndGenerateAUuidId()
    {
        using var lc = TestMode();
        lc.CapturePersonless("anon");
        lc.Capture("u1", "named", personless: true);
        lc.Capture("u1", "normal");
        var a = lc.Captured[0];
        Assert.Equal('4', Guid.Parse(a["distinct_id"]!.GetValue<string>()).ToString()[14]);
        Assert.False(a["properties"]!["$process_person_profile"]!.GetValue<bool>());
        Assert.Equal("u1", lc.Captured[1]["distinct_id"]!.GetValue<string>());
        Assert.NotNull(lc.Captured[1]["properties"]!["$process_person_profile"]);
        Assert.Null(lc.Captured[2]["properties"]!["$process_person_profile"]);
        Assert.Throws<ArgumentException>(() => lc.Capture(null, "x"));
    }

    [Fact]
    public void ReadHeaderIsCaseInsensitiveStripsControlCharsTrimsAndCaps()
    {
        Assert.Equal("abc", LucidaContext.ReadHeader([new("x-lucida-distinct-id", "  a\u0000b\nc ")], "X-Lucida-Distinct-Id"));
        Assert.Null(LucidaContext.ReadHeader([new("K", " \t ")], "k"));
        Assert.Null(LucidaContext.ReadHeader(null, "k"));
        Assert.Equal(1000, LucidaContext.ReadHeader([new("K", new string('x', 2000))], "k")!.Length);
    }

    [Fact]
    public void FromHeadersReadsIdsAndHonoursTrustProxy()
    {
        KeyValuePair<string, string?>[] headers =
        [
            new("X-Lucida-Distinct-Id", "u1"), new("X-Lucida-Session-Id", "s1"), new("User-Agent", "ua"), new("X-Forwarded-For", "1.1.1.1, 2.2.2.2"),
        ];
        var props = new Dictionary<string, object?> { ["$request_path"] = "/p", ["$ip"] = "9.9.9.9" };
        var trusted = LucidaContext.FromHeaders(headers, true, props);
        Assert.Equal("u1", trusted.DistinctId);
        Assert.Equal("s1", trusted.SessionId);
        Assert.Equal("1.1.1.1", trusted.Props["$ip"]);
        Assert.Equal("ua", trusted.Props["$user_agent"]);
        Assert.Equal("9.9.9.9", LucidaContext.FromHeaders(headers, false, props).Props["$ip"]);
    }

    [Fact]
    public async Task ScopesNestRestoreFlowThroughAwaitAndFeedCapture()
    {
        using var lc = TestMode();
        using (LucidaContext.With("u1", "s1", new Dictionary<string, object?> { ["$request_path"] = "/a", ["k"] = "ctx" }))
        {
            using (LucidaContext.With("u2")) Assert.Equal("u2", LucidaContext.Current!.DistinctId);
            Assert.Equal("u1", LucidaContext.Current!.DistinctId);
            await Task.Delay(1);
            lc.Capture(null, "ev", new Dictionary<string, object?> { ["k"] = "explicit" });
            lc.Capture("other", "ev2");
        }
        Assert.Null(LucidaContext.Current);
        var e = lc.Captured[0];
        Assert.Equal("u1", e["distinct_id"]!.GetValue<string>());
        Assert.Equal("s1", e["properties"]!["$session_id"]!.GetValue<string>());
        Assert.Equal("/a", e["properties"]!["$request_path"]!.GetValue<string>());
        Assert.Equal("explicit", e["properties"]!["k"]!.GetValue<string>());
        Assert.Equal("other", lc.Captured[1]["distinct_id"]!.GetValue<string>());
    }
}
