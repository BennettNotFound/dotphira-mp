using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace DotPmp.Server;

file record ReplayAuthRequest([property: JsonPropertyName("token")] string Token);
file record ReplayAutoUploadConfigRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("show")] bool? Show);

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
    private const int DownloadBytesPerSecond = 50 * 1024;

    public static void MapReplayApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/replay");

        group.MapPost("/auth", async (ReplayAuthRequest body, ReplayService replayService) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { ok = false, error = "bad-request" });

            var (ok, data) = await replayService.AuthenticateAsync(body.Token);

            return ok
                ? Results.Ok(data)
                : Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);
        });

        group.MapGet("/download", async (
            [FromQuery] string sessionToken,
            [FromQuery] int chartId,
            [FromQuery] long timestamp,
            ReplayService replayService,
            HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(sessionToken) || chartId <= 0 || timestamp <= 0)
                return Results.BadRequest(new { ok = false, error = "bad-request" });

            var session = replayService.GetSession(sessionToken);
            if (session == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var filePath = replayService.GetReplayPath(session.UserId, chartId, timestamp);
            if (!replayService.IsReplayPathAllowed(filePath))
                return Results.Json(new { ok = false, error = "path-not-allowed" }, statusCode: 403);

            if (!File.Exists(filePath))
                return Results.NotFound(new { ok = false, error = "not-found" });

            await using var fileStream = File.OpenRead(filePath);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = fileStream.Length;
            context.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{timestamp}.phirarec\"";

            await CopyWithRateLimitAsync(fileStream, context.Response.Body, context.RequestAborted);

            return Results.Empty;
        });

        group.MapPost("/upload", async (
            ReplayUploadRequest body,
            ReplayService replayService,
            ShareStationService shareStation) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token) || body.ChartId <= 0 || body.Timestamp <= 0)
                return Results.BadRequest(new { ok = false, error = "bad-request" });

            var user = await replayService.AuthenticateUserAsync(body.Token);

            if (user == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var path = replayService.GetReplayPath(user.Id, body.ChartId, body.Timestamp);
            if (!replayService.IsReplayPathAllowed(path))
                return Results.Json(new { ok = false, error = "path-not-allowed" }, statusCode: 403);

            if (!File.Exists(path))
                return Results.NotFound(new { ok = false, error = "not-found" });

            if (!replayService.TryReadReplayHeader(path, out var header) || header == null || header.UserId != user.Id || header.ChartId != body.ChartId)
                return Results.NotFound(new { ok = false, error = "not-found" });

            if (!shareStation.IsConfigured)
                return Results.Json(new { ok = false, error = "share-station-not-configured" }, statusCode: 503);

            ShareStationService.UploadResult result;
            try
            {
                result = await shareStation.UploadReplayAsync(path, user.Id, show: true);
            }
            catch
            {
                return Results.Json(new { ok = false, error = "upload-failed" }, statusCode: 500);
            }

            return Results.Ok(new
            {
                ok = true,
                userId = user.Id,
                chartId = body.ChartId,
                recordId = result.ReplayId,
                scoreId = result.ReplayId,
                replayId = result.ReplayId,
                message = "upload-success"
            });
        });

        group.MapPost("/delete", async (
            ReplayDeleteRequest body,
            ReplayService replayService) =>
        {
            if (string.IsNullOrWhiteSpace(body.SessionToken) || body.ChartId <= 0 || body.Timestamp <= 0)
                return Results.BadRequest(new { ok = false, error = "bad-request" });

            var session = replayService.GetSession(body.SessionToken);

            if (session == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var filePath = replayService.GetReplayPath(session.UserId, body.ChartId, body.Timestamp);
            if (!replayService.IsReplayPathAllowed(filePath))
                return Results.Json(new { ok = false, error = "path-not-allowed" }, statusCode: 403);

            if (!File.Exists(filePath))
                return Results.NotFound(new { ok = false, error = "not-found" });

            File.Delete(filePath);

            return Results.Ok(new { ok = true });
        });

        group.MapGet("/auto-upload/config", async (
            [FromQuery] string token,
            ReplayService replayService,
            ReplayAutoUploadConfigService configService,
            ShareStationService shareStation) =>
        {
            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { ok = false, error = "bad-token" });

            var user = await replayService.AuthenticateUserAsync(token);
            if (user == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var config = configService.Get(user.Id);
            return Results.Ok(new
            {
                ok = true,
                userId = user.Id,
                enabled = config.Enabled,
                show = config.Show,
                shareStationConfigured = shareStation.IsConfigured
            });
        });

        group.MapPost("/auto-upload/config", async (
            ReplayAutoUploadConfigRequest body,
            ReplayService replayService,
            ReplayAutoUploadConfigService configService) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { ok = false, error = "bad-token" });

            if (body.Enabled == null && body.Show == null)
                return Results.BadRequest(new { ok = false, error = "bad-request" });

            var user = await replayService.AuthenticateUserAsync(body.Token);
            if (user == null)
                return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var config = configService.Update(user.Id, body.Enabled, body.Show);
            return Results.Ok(new
            {
                ok = true,
                userId = user.Id,
                enabled = config.Enabled,
                show = config.Show
            });
        });
    }

    private static async Task CopyWithRateLimitAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        long totalBytes = 0;
        var startedAt = DateTime.UtcNow;

        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await destination.FlushAsync(cancellationToken);

            totalBytes += read;
            var expectedElapsed = TimeSpan.FromSeconds((double)totalBytes / DownloadBytesPerSecond);
            var actualElapsed = DateTime.UtcNow - startedAt;
            var delay = expectedElapsed - actualElapsed;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
    }
}
