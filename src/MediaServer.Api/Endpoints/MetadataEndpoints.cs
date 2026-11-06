using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Endpoints;

public sealed record SetMatchRequest(string TmdbType, int TmdbId, string? Scope);

public static class MetadataEndpoints
{
    public static void MapMetadataEndpoints(this WebApplication app)
    {
        app.MapPost("/api/library/enrich", async (MetadataService svc, CancellationToken ct) =>
        {
            try { return await svc.EnrichAsync(ct) is { } r ? Results.Ok(r) : Results.Conflict(new { error = "Enrichment is already running" }); }
            catch (TmdbNotConfiguredException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (TmdbException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapGet("/api/media/{id:long}/match", (long id, MetadataService svc) =>
            svc.GetMatch(id) is { } m ? Results.Ok(m) : Results.NotFound());

        // Manual override for a bad match. scope: "file" (default) or "title" (whole series / all files with this title).
        app.MapPut("/api/media/{id:long}/match", async (long id, SetMatchRequest body, MetadataService svc, CancellationToken ct) =>
        {
            try
            {
                return await svc.SetOverrideAsync(id, body.TmdbType, body.TmdbId, body.Scope ?? "file", ct) is { } n
                    ? Results.Ok(new { rematched = n }) : Results.NotFound();
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (TmdbNotConfiguredException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (TmdbException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapDelete("/api/media/{id:long}/match", (long id, string? scope, MetadataService svc) =>
        {
            try { return svc.ClearOverride(id, scope ?? "file") is { } n ? Results.Ok(new { reset = n }) : Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Helps pick the right TMDB id when overriding.
        app.MapGet("/api/tmdb/search", async (string type, string q, int? year, TmdbClient tmdb, CancellationToken ct) =>
        {
            if (type is not ("movie" or "tv") || string.IsNullOrWhiteSpace(q)) return Results.BadRequest(new { error = "type=movie|tv and q are required" });
            try { return Results.Ok(await tmdb.SearchAsync(type, q, year, ct)); }
            catch (TmdbNotConfiguredException ex) { return Results.Problem(ex.Message, statusCode: 503); }
            catch (TmdbException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        // Candidate artwork shown while picking the right match. Path and size are validated by ImageCache (never used as a file path).
        app.MapGet("/api/tmdb/image", async (string path, string? size, ImageCache images, HttpContext ctx, CancellationToken ct) =>
        {
            size ??= "w154";
            if (!ImageCache.IsValidSize(size)) return Results.BadRequest(new { error = "invalid size" });
            try
            {
                var file = await images.GetAsync(size, path, ct);
                if (file is null) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "public, max-age=604800";
                return Results.File(file, path.EndsWith(".png") ? "image/png" : path.EndsWith(".webp") ? "image/webp" : "image/jpeg");
            }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
        });

        app.MapGet("/api/media/{id:long}/{kind:regex(^(poster|backdrop)$)}", async (long id, string kind, string? size, MetadataService svc, ImageCache images, HttpContext ctx, CancellationToken ct) =>
        {
            var m = svc.GetMatch(id);
            var path = kind == "poster" ? m?.PosterPath : m?.BackdropPath;
            if (path is null) return Results.NotFound();
            size ??= kind == "poster" ? "w342" : "w780";
            if (!ImageCache.IsValidSize(size)) return Results.BadRequest(new { error = "invalid size" });
            try
            {
                var file = await images.GetAsync(size, path, ct);
                if (file is null) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "public, max-age=604800";
                return Results.File(file, path.EndsWith(".png") ? "image/png" : path.EndsWith(".webp") ? "image/webp" : "image/jpeg");
            }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
        });
    }
}
