using System.Text.Json.Serialization;

namespace DotPmp.Server;

file record ReplayAuthRequest([property: JsonPropertyName("token")] string Token);
file record ReplayDeleteRequest(
    [property: JsonPropertyName("sessionToken")] string SessionToken, 
    [property: JsonPropertyName("chartId")] int ChartId, 
    [property: JsonPropertyName("timestamp")] long Timestamp);

public static class ReplayApi
{
    public static void MapReplayApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/replay");

        group.MapPost("/auth", async (ReplayAuthRequest body, ReplayService replayService) =>
        {
            if (string.IsNullOrWhiteSpace(body.Token)) return Results.BadRequest(new { ok = false, error = "bad-request" });
            var (ok, data) = await replayService.AuthenticateAsync(body.Token);
            return ok ? Results.Ok(data) : Results.Json(data, statusCode: 401);
        });

        group.MapGet("/download", async (string sessionToken, int chartId, long timestamp, ReplayService replayService, HttpContext context) =>
        {
            var session = replayService.GetSession(sessionToken);
            if (session == null) return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var filePath = Path.Combine(AppContext.BaseDirectory, "record", session.UserId.ToString(), chartId.ToString(), $"{timestamp}.phirarec");
            if (!File.Exists(filePath)) return Results.NotFound(new { ok = false, error = "not-found" });

            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.Add("Content-Disposition", $"attachment; filename={timestamp}.phirarec");

            // 实现限速 50KB/s
            const int bufferSize = 4096;
            const int bytesPerSecond = 50 * 1024;
            var buffer = new byte[bufferSize];
            
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            while (true)
            {
                var bytesRead = await fs.ReadAsync(buffer);
                if (bytesRead == 0) break;

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, bytesRead));
                
                // 计算延迟以达到限速效果
                var delayMs = (int)(bytesRead * 1000.0 / bytesPerSecond);
                if (delayMs > 0) await Task.Delay(delayMs);
                
                if (context.RequestAborted.IsCancellationRequested) break;
            }
            
            return Results.Empty;
        });

        group.MapPost("/delete", async (ReplayDeleteRequest body, ReplayService replayService) =>
        {
            var session = replayService.GetSession(body.SessionToken);
            if (session == null) return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

            var filePath = Path.Combine(AppContext.BaseDirectory, "record", session.UserId.ToString(), body.ChartId.ToString(), $"{body.Timestamp}.phirarec");
            if (!File.Exists(filePath)) return Results.NotFound(new { ok = false, error = "not-found" });

            try
            {
                File.Delete(filePath);
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { ok = false, error = ex.Message });
            }
        });
    }
}
