using System.Net;
using MediaServer.Api.Auth;

namespace MediaServer.Api.Endpoints;

public sealed record SetupBody(string? Username, string? Password, string? SetupCode, string? DisplayName = null);
public sealed record ProfileBody(string? DisplayName);
public sealed record LoginBody(string? Username, string? Password, bool? Remember);
public sealed record PasswordBody(string? Current, string? New);

public static class AuthEndpoints
{
    public const string UserItem = "ms.user";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        // Everything the page needs to decide between "set up", "sign in" and "show the app".
        app.MapGet("/api/auth/state", (AuthService auth, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            if (!auth.Enabled) return Results.Ok(new { authEnabled = false, setupRequired = false, authenticated = true, user = (string?)null, setupCodeRequired = false, serverName = auth.ServerName, name = (string?)null });
            var user = auth.Validate(ctx.Request.Cookies[AuthService.CookieName]);
            return Results.Ok(new { authEnabled = true, setupRequired = !auth.HasUsers(), authenticated = user is not null, user = user?.Username, name = user?.Name, displayName = user?.DisplayName, setupCodeRequired = auth.SetupCodeRequired, serverName = auth.ServerName });
        });

        app.MapPost("/api/auth/setup", (SetupBody body, AuthService auth, HttpContext ctx) =>
        {
            if (!auth.Enabled) return Results.NotFound();
            try
            {
                var user = auth.CreateFirstUser(body.Username, body.Password, body.SetupCode, body.DisplayName);
                SignIn(ctx, auth, user, persistent: true);
                return Results.Ok(new { user = user.Username, name = user.Name });
            }
            catch (AuthException ex) { return Fail(ex); }
        });

        app.MapPost("/api/auth/login", (LoginBody body, AuthService auth, HttpContext ctx) =>
        {
            if (!auth.Enabled) return Results.NotFound();
            try
            {
                var user = auth.Login(body.Username, body.Password, ClientKey(ctx));
                SignIn(ctx, auth, user, body.Remember ?? false);
                return Results.Ok(new { user = user.Username, name = user.Name });
            }
            catch (AuthException ex) { return Fail(ex, ctx); }
        });

        app.MapPost("/api/auth/logout", (AuthService auth, HttpContext ctx) =>
        {
            auth.Revoke(ctx.Request.Cookies[AuthService.CookieName]);
            ctx.Response.Cookies.Delete(AuthService.CookieName, CookieOptions(ctx, null));
            return Results.NoContent();
        });

        // The friendly name the interface uses ("Good evening, Alex"). The username is unchanged.
        app.MapPut("/api/auth/profile", (ProfileBody body, AuthService auth, HttpContext ctx) =>
        {
            if (ctx.Items[UserItem] is not AuthUser user) return Results.Unauthorized();
            try { var name = auth.SetDisplayName(user.Id, body.DisplayName); return Results.Ok(new { name = name ?? user.Username, displayName = name }); }
            catch (AuthException ex) { return Fail(ex); }
        });

        app.MapGet("/api/auth/sessions", (AuthService auth, HttpContext ctx) =>
            ctx.Items[UserItem] is AuthUser user ? Results.Ok(new { sessions = auth.ListSessions(user.Id, ctx.Request.Cookies[AuthService.CookieName]) }) : Results.Unauthorized());

        app.MapDelete("/api/auth/sessions/{id}", (string id, AuthService auth, HttpContext ctx) =>
            ctx.Items[UserItem] is not AuthUser user ? Results.Unauthorized() : auth.RevokeSession(user.Id, id) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/auth/sessions/revoke-others", (AuthService auth, HttpContext ctx) =>
            ctx.Items[UserItem] is AuthUser user ? Results.Ok(new { revoked = auth.RevokeOthers(user.Id, ctx.Request.Cookies[AuthService.CookieName]) }) : Results.Unauthorized());

        // What the page needs to know about this server in one small call.
        app.MapGet("/api/app", (AuthService auth, IConfiguration config) => Results.Ok(new
        {
            serverName = auth.ServerName,
            allowTranscoding = MediaServer.Api.Hls.HlsOptions.From(config).AllowTranscoding,
            authEnabled = auth.Enabled,
        }));

        app.MapPost("/api/auth/password", (PasswordBody body, AuthService auth, HttpContext ctx) =>
        {
            if (ctx.Items[UserItem] is not AuthUser user) return Results.Unauthorized();
            try { auth.ChangePassword(user.Id, body.Current, body.New, ctx.Request.Cookies[AuthService.CookieName]); return Results.NoContent(); }
            catch (AuthException ex) { return Fail(ex); }
        });
    }

    private static IResult Fail(AuthException ex, HttpContext? ctx = null)
    {
        if (ex.RetryAfterSeconds is { } s && ctx is not null) ctx.Response.Headers.RetryAfter = s.ToString();
        return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
    }

    private static void SignIn(HttpContext ctx, AuthService auth, AuthUser user, bool persistent)
    {
        var token = auth.CreateSession(user.Id, ctx.Request.Headers.UserAgent.ToString(), persistent);
        ctx.Response.Cookies.Append(AuthService.CookieName, token, CookieOptions(ctx, persistent ? DateTimeOffset.UtcNow + auth.SessionLength : null));
        ctx.Response.Headers.CacheControl = "no-store";
    }

    internal static CookieOptions CookieOptions(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", Expires = expires,
        Secure = ctx.Request.IsHttps || string.Equals(ctx.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>Who is trying: the connection's address (a reverse proxy's X-Forwarded-For is only honoured when told to).</summary>
    internal static string ClientKey(HttpContext ctx)
    {
        if (ctx.RequestServices.GetRequiredService<AuthService>().TrustProxy && ctx.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim() is { Length: > 0 and <= 45 } fwd) return fwd;
        var ip = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString();
    }
}

/// <summary>Everything under /api and /stream needs a signed-in user, except the sign-in endpoints themselves. Also blocks cross-site writes.</summary>
public sealed class AuthMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AuthService auth)
    {
        var path = ctx.Request.Path;
        var guarded = path.StartsWithSegments("/api") || path.StartsWithSegments("/stream");
        if (!guarded) { await next(ctx); return; }

        // A write from another site's page is never legitimate, whatever cookie the browser would attach.
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !HttpMethods.IsOptions(ctx.Request.Method)
            && ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !SameHost(origin, ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "Cross-site request refused" });
            return;
        }

        if (!auth.Enabled || path.StartsWithSegments("/api/auth", out var rest) && (rest.Value is "/state" or "/setup" or "/login" or "/logout" or "/password"))
        {
            if (auth.Enabled) ctx.Items[AuthEndpoints.UserItem] = auth.Validate(ctx.Request.Cookies[AuthService.CookieName]);
            await next(ctx);
            return;
        }

        // Capability links: an HLS session id (created by a signed-in user, 128 random bits) or a signed direct-play token for that one file.
        if (System.Text.RegularExpressions.Regex.IsMatch(path.Value ?? "", "^/api/hls/[0-9a-f]{32}/")) { await next(ctx); return; }
        if (path.StartsWithSegments("/stream", out var sid) && long.TryParse(sid.Value?.TrimStart('/'), out var mediaId) && auth.CheckStreamToken(mediaId, ctx.Request.Query["k"])) { await next(ctx); return; }

        var user = auth.Validate(ctx.Request.Cookies[AuthService.CookieName]);
        if (user is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.CacheControl = "no-store";
            if (ctx.Request.Headers.Accept.ToString().Contains("text/html") && !path.StartsWithSegments("/api")) { await ctx.Response.WriteAsync("Sign in required"); return; }
            await ctx.Response.WriteAsJsonAsync(new { error = "auth_required", setupRequired = !auth.HasUsers() });
            return;
        }
        ctx.Items[AuthEndpoints.UserItem] = user;
        await next(ctx);
    }

    private static bool SameHost(string origin, HttpRequest req)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var o)) return false;
        var host = req.Headers["X-Forwarded-Host"].ToString() is { Length: > 0 } fh ? fh.Split(',')[0].Trim() : req.Host.Value;
        return string.Equals(o.Authority, host, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Host, host, StringComparison.OrdinalIgnoreCase);
    }
}
