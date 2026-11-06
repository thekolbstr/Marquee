using MediaServer.Api.Auth;
using MediaServer.Api.Library;

namespace MediaServer.Api.Endpoints;

public sealed record FolderBody(string? Path, string? Name);
public sealed record FolderPatch(bool? Enabled, string? Name);

/// <summary>Adding library folders from the web UI. Only a signed-in user gets here (see AuthMiddleware), and every path is validated.</summary>
public static class FolderEndpoints
{
    public static void MapFolderEndpoints(this WebApplication app)
    {
        // Browsing and editing the server's folders is only offered when sign-in protects it (or the deployer opts in explicitly).
        static bool Allowed(IConfiguration c, AuthService a) => a.Enabled || c.GetValue("Library:AllowFolderEditing", false);

        app.MapGet("/api/library/folders", (LibraryFolders f, IConfiguration c, AuthService a) =>
            Results.Ok(new { folders = f.List(), editable = Allowed(c, a), hasLibrary = f.HasAny }));

        app.MapGet("/api/fs/browse", (string? path, LibraryFolders f, IConfiguration c, AuthService a) =>
        {
            if (!Allowed(c, a)) return Results.Json(new { error = "Turn on sign-in to manage folders from the web." }, statusCode: 403);
            try { return Results.Ok(f.Browse(path)); }
            catch (FolderException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/api/library/folders", (FolderBody body, LibraryFolders f, ScanCoordinator scans, IConfiguration c, AuthService a) =>
        {
            if (!Allowed(c, a)) return Results.Json(new { error = "Turn on sign-in to manage folders from the web." }, statusCode: 403);
            try
            {
                var added = f.Add(body.Path, body.Name);
                scans.TryStart();
                return Results.Created($"/api/library/folders/{added.Id}", added);
            }
            catch (FolderException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Results.BadRequest(new { error = "That folder is already in the library." }); }
        });

        app.MapPatch("/api/library/folders/{id:long}", (long id, FolderPatch body, LibraryFolders f, ScanCoordinator scans, IConfiguration c, AuthService a) =>
        {
            if (!Allowed(c, a)) return Results.Json(new { error = "Turn on sign-in to manage folders from the web." }, statusCode: 403);
            try
            {
                if (!f.Update(id, body.Enabled, body.Name)) return Results.NotFound();
                if (body.Enabled is not null) scans.TryStart();   // hide or restore its files
                return Results.NoContent();
            }
            catch (FolderException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/library/folders/{id:long}", (long id, LibraryFolders f, ScanCoordinator scans, IConfiguration c, AuthService a) =>
        {
            if (!Allowed(c, a)) return Results.Json(new { error = "Turn on sign-in to manage folders from the web." }, statusCode: 403);
            if (!f.Remove(id)) return Results.NotFound();
            scans.TryStart();                                  // its files leave the library (the files themselves are never touched)
            return Results.NoContent();
        });
    }
}
