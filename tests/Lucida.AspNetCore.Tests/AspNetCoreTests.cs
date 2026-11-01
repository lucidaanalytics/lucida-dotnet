using System.Net;
using System.Net.Http;
using System.Security.Claims;
using Lucida.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Lucida.AspNetCore.Tests;

public class AspNetCoreTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Paths { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Paths.Add(req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static DefaultHttpContext Request(Action<DefaultHttpContext>? tweak = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("shop.example.com");
        http.Request.Path = "/cart";
        http.Request.QueryString = new QueryString("?a=1");
        http.Request.Headers["X-Lucida-Distinct-Id"] = "browser-id";
        http.Request.Headers["X-Lucida-Session-Id"] = "sess-1";
        http.Request.Headers["User-Agent"] = "test-agent";
        http.Request.Headers["X-Forwarded-For"] = "7.7.7.7, 8.8.8.8";
        http.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
        tweak?.Invoke(http);
        return http;
    }

    private static async Task<LucidaContextData?> Run(DefaultHttpContext http, bool trustProxy)
    {
        LucidaContextData? seen = null;
        var mw = new LucidaMiddleware(_ => { seen = LucidaContext.Current; return Task.CompletedTask; }, new LucidaAspNetSettings(trustProxy));
        await mw.InvokeAsync(http);
        Assert.Null(LucidaContext.Current); // scope closed after the request
        return seen;
    }

    [Fact]
    public async Task MiddlewareSetsContextFromHeadersAndRequest()
    {
        var d = await Run(Request(), trustProxy: false);
        Assert.Equal("browser-id", d!.DistinctId);
        Assert.Equal("sess-1", d.SessionId);
        Assert.Equal("https://shop.example.com/cart?a=1", d.Props["$current_url"]);
        Assert.Equal("POST", d.Props["$request_method"]);
        Assert.Equal("/cart", d.Props["$request_path"]);
        Assert.Equal("test-agent", d.Props["$user_agent"]);
        Assert.Equal("10.0.0.1", d.Props["$ip"]);
    }

    [Fact]
    public async Task TrustProxyTakesTheForwardedIp()
    {
        Assert.Equal("7.7.7.7", (await Run(Request(), trustProxy: true))!.Props["$ip"]);
    }

    [Fact]
    public async Task AuthenticatedClaimBeatsTheClientHeader()
    {
        var authed = Request(h => h.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user-42")], "test")));
        Assert.Equal("user-42", (await Run(authed, false))!.DistinctId);
        var anonymous = Request(h => h.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ignored")])));
        Assert.Equal("browser-id", (await Run(anonymous, false))!.DistinctId); // unauthenticated identity: claim not trusted
    }

    [Fact]
    public async Task AddLucidaBindsAppsettingsAndFlushesOnStop()
    {
        var rec = new Recorder();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lucida:ApiKey"] = "lc_pk_x",
            ["Lucida:Host"] = "https://ingest.example.com",
            ["Lucida:FlushAt"] = "50",
            ["Lucida:FlushInterval"] = "01:00:00",
            ["Lucida:TrustProxy"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLucida(config);
        // swap in a client with the same config but a recording handler, keeping the hosted service wiring under test
        services.AddSingleton(new LucidaClient("lc_pk_x", new LucidaOptions { Handler = rec, FlushInterval = TimeSpan.FromHours(1), FlushAt = 100 }));
        await using var sp = services.BuildServiceProvider();
        Assert.True(sp.GetRequiredService<LucidaAspNetSettings>().TrustProxy);

        var client = sp.GetRequiredService<LucidaClient>();
        client.Capture("u", "bye");
        await sp.GetServices<IHostedService>().Single().StopAsync(CancellationToken.None);
        Assert.Equal(["/v1/batch"], rec.Paths);
    }

    [Fact]
    public void BoundOptionsReachTheClientAndMissingApiKeyFails()
    {
        var bound = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lucida:Host"] = "https://ingest.example.com",
            ["Lucida:FlushAt"] = "50",
            ["Lucida:FlagPollInterval"] = "00:01:00",
            ["Lucida:Debug"] = "true",
        }).Build().GetSection("Lucida").Get<LucidaOptions>()!;
        Assert.Equal("https://ingest.example.com", bound.Host);
        Assert.Equal(50, bound.FlushAt);
        Assert.Equal(TimeSpan.FromMinutes(1), bound.FlagPollInterval);
        Assert.True(bound.Debug);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddLucida(new ConfigurationBuilder().Build()));
    }
}
