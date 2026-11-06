using MediaServer.Api.Auth;
using MediaServer.Api.Endpoints;

namespace MediaServer.Api.Recording;

public sealed record RecordBody(long ChannelId, string? Mode = null, long? ProgrammeStart = null, long? Start = null, long? Stop = null, string? Title = null);
public sealed record ListBody(string? Name);

public static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recordings", (RecordingService rec, HttpContext ctx) =>
            Results.Ok(new { recordings = rec.List(all: ctx.Items[AuthEndpoints.UserItem] is AuthUser { IsAdmin: true } && ctx.Request.Query["all"] == "true"), active = rec.ActiveCount }));

        // mode "now": record this channel from now until the current programme ends.  ProgrammeStart: a guide entry.  Start+Stop: a time slot.
        app.MapPost("/api/recordings", (RecordBody b, RecordingService rec) =>
        {
            try
            {
                var r = b.Mode == "now" ? rec.RecordNow(b.ChannelId)
                    : b.ProgrammeStart is { } ps ? rec.ScheduleProgramme(b.ChannelId, ps)
                    : b is { Start: { } s, Stop: { } e } ? rec.ScheduleManual(b.ChannelId, s, e, b.Title)
                    : throw new RecordingException("Say what to record: now, a programme, or a start and end time.");
                return Results.Created($"/api/recordings/{r.Id}", r);
            }
            catch (RecordingException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        // Stop (keeping what was captured) or cancel a scheduled one.
        app.MapPost("/api/recordings/{id:long}/stop", async (long id, RecordingService rec, HttpContext ctx) =>
        {
            try { await rec.StopAsync(id, ctx.Items[AuthEndpoints.UserItem] is AuthUser { IsAdmin: true }); return Results.NoContent(); }
            catch (RecordingException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        app.MapDelete("/api/recordings/{id:long}", async (long id, RecordingService rec, HttpContext ctx) =>
        {
            try { await rec.DeleteAsync(id, ctx.Items[AuthEndpoints.UserItem] is AuthUser { IsAdmin: true }); return Results.NoContent(); }
            catch (RecordingException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        // ---- your own channel lists
        app.MapGet("/api/live/lists", (LiveListService lists) => Results.Ok(new { lists = lists.Lists() }));
        app.MapPost("/api/live/lists", (ListBody b, LiveListService lists) =>
        {
            try { return Results.Created("/api/live/lists", lists.Create(b.Name)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapPatch("/api/live/lists/{id:long}", (long id, ListBody b, LiveListService lists) =>
        {
            try { return lists.Rename(id, b.Name) ? Results.NoContent() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        app.MapDelete("/api/live/lists/{id:long}", (long id, LiveListService lists) => lists.Delete(id) ? Results.NoContent() : Results.NotFound());
        app.MapPut("/api/live/lists/{id:long}/channels/{channelId:long}", (long id, long channelId, LiveListService lists) => lists.SetMember(id, channelId, true) ? Results.NoContent() : Results.NotFound());
        app.MapDelete("/api/live/lists/{id:long}/channels/{channelId:long}", (long id, long channelId, LiveListService lists) => lists.SetMember(id, channelId, false) ? Results.NoContent() : Results.NotFound());
    }
}
