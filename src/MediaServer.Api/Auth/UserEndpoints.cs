using MediaServer.Api.Auth;

namespace MediaServer.Api.Endpoints;

public sealed record NewUserBody(string? Username, string? Password, string? DisplayName, string? Role);
public sealed record UserPatchBody(string? Role, string? DisplayName, bool? ClearDisplayName);
public sealed record ResetPasswordBody(string? Password);

/// <summary>People who can sign in. Administrators only (enforced in AuthMiddleware).</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        app.MapGet("/api/users", (UserService users) => Results.Ok(new { users = users.List() }));

        app.MapPost("/api/users", (NewUserBody b, UserService users) =>
        {
            try { return Results.Created("/api/users", users.Create(b.Username, b.Password, b.DisplayName, b.Role)); }
            catch (AuthException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapPatch("/api/users/{id:long}", (long id, UserPatchBody b, UserService users) =>
        {
            try { users.Update(id, b.Role, b.DisplayName, b.DisplayName is not null || b.ClearDisplayName == true); return Results.NoContent(); }
            catch (AuthException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapPost("/api/users/{id:long}/password", (long id, ResetPasswordBody b, UserService users) =>
        {
            try { users.ResetPassword(id, b.Password); return Results.NoContent(); }
            catch (AuthException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapPost("/api/users/{id:long}/two-factor/reset", (long id, UserService users) =>
        {
            try { users.ResetTwoFactor(id); return Results.NoContent(); }
            catch (AuthException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapDelete("/api/users/{id:long}", (long id, UserService users, HttpContext ctx) =>
        {
            if (ctx.Items[AuthEndpoints.UserItem] is not AuthUser me) return Results.Unauthorized();
            try { users.Delete(id, me.Id); return Results.NoContent(); }
            catch (AuthException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });
    }
}
