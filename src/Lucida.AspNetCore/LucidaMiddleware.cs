using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;

namespace Lucida.AspNetCore;

/// <summary>
/// Opens a <see cref="LucidaContext"/> for each request. Distinct id: the authenticated user's <c>NameIdentifier</c>/<c>sub</c> claim,
/// else the browser's <c>X-Lucida-Distinct-Id</c> header (client-controlled, so an authenticated claim always wins and the header
/// never authenticates anything). Also <c>X-Lucida-Session-Id</c> and the request props <c>$current_url</c>, <c>$request_method</c>,
/// <c>$request_path</c>, <c>$user_agent</c>, <c>$ip</c>. Place it after <c>UseAuthentication()</c>.
/// </summary>
public sealed class LucidaMiddleware(RequestDelegate next, LucidaAspNetSettings settings)
{
    public async Task InvokeAsync(HttpContext http)
    {
        using (LucidaContext.With(Build(http, settings.TrustProxy)))
            await next(http).ConfigureAwait(false);
    }

    internal static LucidaContextData Build(HttpContext http, bool trustProxy)
    {
        var r = http.Request;
        var props = new Dictionary<string, object?>
        {
            ["$current_url"] = r.GetDisplayUrl(),
            ["$request_method"] = r.Method,
            ["$request_path"] = r.Path.Value,
            ["$ip"] = http.Connection.RemoteIpAddress?.ToString(),
        };
        var data = LucidaContext.FromHeaders(h => r.Headers.TryGetValue(h, out var v) ? v.FirstOrDefault() : null, trustProxy, props);
        var user = http.User;
        var claim = user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub") : null;
        return claim is null ? data : data with { DistinctId = LucidaContext.Sanitize(claim) ?? data.DistinctId };
    }
}
