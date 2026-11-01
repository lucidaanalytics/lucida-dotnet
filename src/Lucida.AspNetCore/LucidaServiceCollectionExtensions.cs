using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Lucida.AspNetCore;

/// <summary>Middleware settings. <paramref name="TrustProxy"/>: take <c>$ip</c> from the first <c>X-Forwarded-For</c> entry (only behind a proxy you control).</summary>
public sealed record LucidaAspNetSettings(bool TrustProxy = false);

/// <summary>Flushes queued events when the host stops, so a graceful shutdown loses nothing.</summary>
internal sealed class LucidaFlushOnStop(LucidaClient client) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => client.FlushAsync();
}

public static class LucidaServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="LucidaClient"/> from configuration. Section <c>Lucida</c> (or <paramref name="sectionName"/>):
    /// <c>ApiKey</c> (required), <c>TrustProxy</c>, plus any <see cref="LucidaOptions"/> property (<c>Host</c>, <c>SecretKey</c>,
    /// <c>FlushAt</c>, <c>FlagPollInterval</c> as <c>00:01:00</c>, ...). Code-only options (handler, hooks) need the other overload.
    /// </summary>
    public static IServiceCollection AddLucida(this IServiceCollection services, IConfiguration configuration, string sectionName = "Lucida")
    {
        var section = configuration.GetSection(sectionName);
        var apiKey = section["ApiKey"] ?? throw new InvalidOperationException($"{sectionName}:ApiKey is not configured");
        return services.AddLucida(apiKey, section.Get<LucidaOptions>() ?? new LucidaOptions(), section.GetValue<bool>("TrustProxy"));
    }

    public static IServiceCollection AddLucida(this IServiceCollection services, string apiKey, LucidaOptions? options = null, bool trustProxy = false)
    {
        services.AddSingleton(new LucidaAspNetSettings(trustProxy));
        services.AddSingleton(new LucidaClient(apiKey, options));
        services.AddHostedService<LucidaFlushOnStop>();
        return services;
    }

    /// <summary>Adds <see cref="LucidaMiddleware"/>; call after <c>UseAuthentication()</c> so the user claim is available.</summary>
    public static IApplicationBuilder UseLucida(this IApplicationBuilder app) => app.UseMiddleware<LucidaMiddleware>();
}
