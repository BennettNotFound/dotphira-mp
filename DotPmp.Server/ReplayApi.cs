using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace DotPmp.Server;

file record ReplayAuthRequest([property: JsonPropertyName("token")] string Token);

file record ReplayDeleteRequest(
    [property: JsonPropertyName("sessionToken")] string SessionToken,
    [property: JsonPropertyName("chartId")] int ChartId,
    [property: JsonPropertyName("timestamp")] long Timestamp);

file record ReplayUploadRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("chartId")] int ChartId,
    [property: JsonPropertyName("timestamp")] long Timestamp);

public static class ReplayApi
{
    public static void MapReplayApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/replay");

        group.MapPost("/auth", async (ReplayAuthRequest body, ReplayService replayService) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { ok = false });

            var (ok, data) = await replayService.AuthenticateAsync(body.Token);

            return ok ? Results.Ok(data) : Results.Json(data, statusCode: 401);
        });

        group.MapGet("/download", async (
            [FromQuery] string sessionToken,
            [FromQuery] int chartId,
            [FromQuery] long timestamp,
            ReplayService replayService,
            HttpContext context) =>
        {
            var session = replayService.GetSession(sessionToken);
            if (session == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var filePath = replayService.GetReplayPath(session.UserId, chartId, timestamp);

            if (!File.Exists(filePath))
                return Results.NotFound(new { ok = false });

            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{timestamp}.phirarec\"";

            await context.Response.SendFileAsync(filePath);

            return Results.Empty;
        });

        group.MapPost("/upload", async (
            ReplayUploadRequest body,
            ReplayService replayService,
            ShareStationService shareStation) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { ok = false });

            var user = await replayService.AuthenticateUserAsync(body.Token);

            if (user == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var path = replayService.GetReplayPath(user.Id, body.ChartId, body.Timestamp);

            if (!File.Exists(path))
                return Results.NotFound(new { ok = false });

            if (!shareStation.IsConfigured)
                return Results.Json(new { ok = false, error = "share-station-not-configured" });

            var result = await shareStation.UploadReplayAsync(path, user.Id);

            return Results.Ok(new
            {
                ok = true,
                userId = user.Id,
                chartId = body.ChartId,
                recordId = result.RecordId,
                scoreId = result.ScoreId
            });
        });

        group.MapPost("/delete", async (
            ReplayDeleteRequest body,
            ReplayService replayService) =>
        {
            var session = replayService.GetSession(body.SessionToken);

            if (session == null)
                return Results.Json(new { ok = false }, statusCode: 401);

            var filePath = replayService.GetReplayPath(session.UserId, body.ChartId, body.Timestamp);

            if (!File.Exists(filePath))
                return Results.NotFound(new { ok = false });

            File.Delete(filePath);

            return Results.Ok(new { ok = true });
        });
    }
}