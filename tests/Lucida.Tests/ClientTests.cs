using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Lucida.Tests;

/// <summary>Records requests (decompressing gzip bodies); <see cref="Statuses"/> scripts replies, then 200.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<JsonObject> Bodies { get; } = new();
    public Queue<HttpStatusCode> Statuses { get; } = new();
    public List<string?> IfNoneMatch { get; } = new();
    /// <summary>Reply body for POST /v1/flags.</summary>
    public string RemoteReply { get; set; } = "{\"flags\":{}}";
    /// <summary>Reply body for GET /v1/flags/{key}/remote-config; null = 404.</summary>
    public string? RemoteConfigReply { get; set; }
    public string Defs { get; set; } = "{\"flags\":[{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]}]}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests.Add(req);
        if (req.Method == HttpMethod.Post && req.Content is not null)
        {
            var raw = await req.Content.ReadAsByteArrayAsync(ct);
            if (req.RequestUri!.Query.Contains("compression=gzip"))
            {
                using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var o = new MemoryStream();
                gz.CopyTo(o);
                raw = o.ToArray();
            }
            Bodies.Add((JsonObject)JsonNode.Parse(raw)!);
        }
        if (req.RequestUri!.AbsolutePath.EndsWith("/remote-config", StringComparison.Ordinal))
            return RemoteConfigReply is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RemoteConfigReply) };
        if (req.RequestUri!.AbsolutePath == "/v1/flags" && req.Method == HttpMethod.Post)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RemoteReply) };
        if (req.RequestUri!.AbsolutePath == "/v1/flags/definitions")
        {
            var inm = req.Headers.TryGetValues("If-None-Match", out var v) ? v.Single() : null;
            IfNoneMatch.Add(inm);
            if (inm == "\"v1\"") return new HttpResponseMessage(HttpStatusCode.NotModified);
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Defs) };
            ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return ok;
        }
        return new HttpResponseMessage(Statuses.Count > 0 ? Statuses.Dequeue() : HttpStatusCode.OK);
    }
}

public class ClientTests
{
    private const string Fixture = "{\"flags\":[{\"key\":\"on\",\"type\":\"boolean\",\"active\":true,\"salt\":\"s\",\"conditions\":[{\"filters\":[],\"rollout_pct\":100}]}]}";

    [Fact]
    public void TestModeKeepsEventsAndNeverTouchesTheNetwork()
    {
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { TestModeFixtureJson = Fixture });
        lc.Capture("u1", "purchase");
        lc.Identify("u1", new Dictionary<string, object?> { ["plan"] = "pro" });
        Assert.Equal(["purchase", "$identify"], lc.Captured.Select(e => e["event"]!.GetValue<string>()));
        Assert.True(lc.IsTestMode);
        Assert.True(lc.IsEnabled("on", new FlagContext { DistinctId = "u1" }));
        Assert.Null(lc.Evaluate("missing", new FlagContext { DistinctId = "u1" }));
    }

    [Fact]
    public async Task BatchRetriesRetryableStatusesThenDelivers()
    {
        var fake = new FakeHandler();
        fake.Statuses.Enqueue(HttpStatusCode.ServiceUnavailable);
        fake.Statuses.Enqueue(HttpStatusCode.TooManyRequests);
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, RetryBase = TimeSpan.FromMilliseconds(1), FlushInterval = TimeSpan.FromHours(1) });
        lc.Capture("u1", "a", uuid: "01890a5d-ac96-7ab2-80e2-4536629c90de");
        Assert.True(await lc.FlushAsync());
        Assert.Equal(3, fake.Bodies.Count);
        Assert.All(fake.Bodies, b => Assert.Equal("01890a5d-ac96-7ab2-80e2-4536629c90de", b["batch"]![0]!["uuid"]!.GetValue<string>())); // same uuid: server dedupes
    }

    [Fact]
    public async Task ClientErrorsAreNotRetriedAndCountAsDropped()
    {
        var fake = new FakeHandler();
        fake.Statuses.Enqueue(HttpStatusCode.BadRequest);
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1) });
        lc.Capture("u1", "a");
        Assert.False(await lc.FlushAsync());
        Assert.Single(fake.Bodies);
        Assert.Equal(1, lc.DroppedEvents);
    }

    [Fact]
    public async Task LargeBatchesAreGzipped()
    {
        var fake = new FakeHandler();
        using var lc = new LucidaClient("lc_pk_x", new LucidaOptions { Handler = fake, FlushInterval = TimeSpan.FromHours(1) });
        lc.Capture("u1", "big", new Dictionary<string, object?> { ["blob"] = new string('x', 3000) });
        await lc.FlushAsync();
        Assert.Contains("compression=gzip", fake.Requests[0].RequestUri!.Query);
        Assert.Equal(3000, fake.Bodies[0]["batch"]![0]!["properties"]!["blob"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task FullQueueDropsAndDisabledSendsNothing()
    {
        var fake = new FakeHandler();
        using (var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, MaxQueueSize = 2, FlushAt = 100, FlushInterval = TimeSpan.FromHours(1) }))
        {
            for (var i = 0; i < 3; i++) lc.Capture("u", "e");
            Assert.Equal(1, lc.DroppedEvents);
        }
        var off = new FakeHandler();
        using var d = new LucidaClient("k", new LucidaOptions { Handler = off, Disabled = true });
        d.Capture("u", "e");
        Assert.True(await d.FlushAsync());
        Assert.Empty(off.Requests);
    }

    [Fact]
    public async Task ReloadFlagsNeedsSecretKeyAndLoadsDefinitions()
    {
        var fake = new FakeHandler();
        using var noKey = new LucidaClient("k", new LucidaOptions { Handler = fake });
        Assert.False(await noKey.ReloadFlagsAsync());
        using var lc = new LucidaClient("k", new LucidaOptions { Handler = fake, SecretKey = "lc_sk_x" });
        Assert.True(await lc.ReloadFlagsAsync());
        Assert.Equal("Bearer lc_sk_x", fake.Requests.Single(r => r.Method == HttpMethod.Get).Headers.Authorization!.ToString());
        Assert.True(lc.IsEnabled("on", new FlagContext { DistinctId = "u1" }));
    }
}
