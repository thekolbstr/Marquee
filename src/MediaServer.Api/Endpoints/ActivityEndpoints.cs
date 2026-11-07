using MediaServer.Api.Library;

namespace MediaServer.Api.Endpoints;

public static class ActivityEndpoints
{
    /// <summary>Under /api/system, so only administrators can read who watched what (enforced in AuthMiddleware).</summary>
    public static void MapActivityEndpoints(this WebApplication app) =>
        app.MapGet("/api/system/activity", (ActivityService activity, int? days) => Results.Ok(activity.Report(days ?? 30)));
}
