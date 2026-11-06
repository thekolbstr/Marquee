namespace MediaServer.Api.Hosting;

/// <summary>
/// Headers that tell the browser what the page may and may not do. The page is plain scripts served from this origin, so the policy is strict:
/// no inline scripts, no framing, no plugins. (Google's cast library is allowed because Chromecast needs it.)
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp =
        "default-src 'self'; " +
        "script-src 'self' https://www.gstatic.com; " +
        "style-src 'self' 'unsafe-inline'; " +                          // the app sets small inline style attributes (positions, colours)
        "img-src 'self' data: blob: https://www.gstatic.com; " +
        "media-src 'self' blob:; " +
        "connect-src 'self' https://www.gstatic.com; " +
        "worker-src 'self' blob:; " +
        "frame-src https://www.gstatic.com; " +
        "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = Csp;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "DENY";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        return next(ctx);
    }
}

/// <summary>Anything that escapes an endpoint becomes a short JSON answer with a reference id; details go to the log, never to the client.</summary>
public static class ErrorHandling
{
    public static void UseApiErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
        {
            var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            var id = ctx.TraceIdentifier;
            if (ex is BadHttpRequestException bad)       // malformed or oversized input is the caller's mistake, not ours
            {
                if (!ctx.Response.HasStarted) { ctx.Response.StatusCode = bad.StatusCode; ctx.Response.ContentType = "application/json"; await ctx.Response.WriteAsJsonAsync(new { error = "That request couldn't be understood." }); }
                return;
            }
            if (ex is not null && ex is not OperationCanceledException)
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Unhandled").LogError(ex, "Unhandled error {Id} on {Method} {Path}", id, ctx.Request.Method, ctx.Request.Path);
            if (ctx.Response.HasStarted) return;
            ctx.Response.StatusCode = ex is OperationCanceledException ? 499 : StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong on the server.", reference = id });
        }));
    }
}
