using System.Text.RegularExpressions;
using MediaServer.Api.Auth;
using MediaServer.Api.Cast;
using MediaServer.Api.Hls;

namespace MediaServer.Api.Endpoints;

public sealed record CastControlRequest(string Action, double? Value = null, string? Text = null);

public static partial class CastEndpoints
{
    [GeneratedRegex("^[a-z0-9]{1,64}$")] private static partial Regex IdRx();

    public static void MapCastEndpoints(this WebApplication app)
    {
        // The TVs the server can see. ?refresh=1 looks again (about three seconds) instead of using what it found in the last minute.
        app.MapGet("/api/cast/devices", async (CastService cast, HttpContext ctx, CancellationToken ct) =>
        {
            var devices = await cast.DevicesAsync(ctx.Request.Query["refresh"].ToString() is "1" or "true", ct);
            return Results.Ok(new { devices = devices.Select(d => new { d.Id, d.Name, d.Model, busy = cast.IsBusy(d.Id), d.Manual }) });
        });

        app.MapGet("/api/cast/sessions", (CastService cast) => Results.Ok(new { sessions = cast.Sessions() }));

        app.MapPost("/api/cast/play", async (CastPlayRequest body, CastService cast, IUserContext user, HttpContext ctx, CancellationToken ct) =>
        {
            if (!IdRx().IsMatch(body.DeviceId ?? "")) return Results.BadRequest(new { error = "Choose a TV." });
            try
            {
                var origin = ctx.Request.Scheme == "http" ? $"http://{ctx.Request.Host}" : null;
                return Results.Ok(await cast.PlayAsync(body, user.UserId, origin, ct));
            }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 422); }
            catch (HlsStartException ex) { return Results.Problem(ex.Message, statusCode: 502); }
            catch (Probing.ProbeFailedException ex) { return Results.Problem(ex.Message, statusCode: 422); }
            catch (CastException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapPost("/api/cast/{deviceId}/control", async (string deviceId, CastControlRequest body, CastService cast, CancellationToken ct) =>
        {
            if (!IdRx().IsMatch(deviceId)) return Results.NotFound();
            try { await cast.ControlAsync(deviceId, body.Action ?? "", body.Value, body.Text, ct); return Results.NoContent(); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (CastException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapPost("/api/cast/{deviceId}/stop", async (string deviceId, CastService cast) =>
        {
            if (IdRx().IsMatch(deviceId)) await cast.StopAsync(deviceId);
            return Results.NoContent();
        });
    }
}
