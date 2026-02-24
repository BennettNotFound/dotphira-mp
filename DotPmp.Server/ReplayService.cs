using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Internal;

namespace DotPmp.Server;

public record ReplaySession(long UserId, DateTime ExpiresAt);

public class ReplayService : IDisposable
{
    private const string PhiraHost = "https://phira.5wyxi.com";
    private static readonly HttpClient HttpClient = new();
    private readonly ISystemClock _clock;
    private readonly Timer _cleanupTimer;
    private readonly ConcurrentDictionary<string, ReplaySession> _sessions = new();

    public ReplayService(ISystemClock clock)
    {
        _clock = clock;
        // 每天执行一次清理，同时也清理过期的 session
        _cleanupTimer = new Timer(_ => 
        {
            CleanupSessions();
            CleanupOldReplays();
        }, null, TimeSpan.Zero, TimeSpan.FromDays(1));
    }

    public async Task<(bool Ok, object? Data)> AuthenticateAsync(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{PhiraHost}/me");
            request.Headers.Add("Authorization", $"Bearer {token}");

            var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return (false, "unauthorized");

            var phiraUser = await response.Content.ReadFromJsonAsync<PhiraUserInfo>();
            if (phiraUser == null) return (false, "failed-to-get-user-info");

            var sessionToken = Guid.NewGuid().ToString();
            var expiresAt = _clock.UtcNow.AddMinutes(30).DateTime;
            _sessions[sessionToken] = new ReplaySession(phiraUser.Id, expiresAt);

            // 扫描用户的所有回放文件并返回列表
            var charts = GetUserReplayList(phiraUser.Id);

            return (true, new
            {
                ok = true,
                userId = phiraUser.Id,
                charts,
                sessionToken,
                expiresAt = new DateTimeOffset(expiresAt).ToUnixTimeMilliseconds()
            });
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public ReplaySession? GetSession(string token)
    {
        if (_sessions.TryGetValue(token, out var session))
        {
            if (session.ExpiresAt > _clock.UtcNow) return session;
            _sessions.TryRemove(token, out _);
        }
        return null;
    }

    private record PhiraUserInfo(int Id, string Name);

    private object GetUserReplayList(long userId)
    {
        var userPath = Path.Combine(AppContext.BaseDirectory, "record", userId.ToString());
        if (!Directory.Exists(userPath)) return new List<object>();

        var chartList = new List<object>();
        foreach (var chartDir in Directory.GetDirectories(userPath))
        {
            var chartIdStr = Path.GetFileName(chartDir);
            if (!int.TryParse(chartIdStr, out var chartId)) continue;

            var replays = new List<object>();
            foreach (var file in Directory.GetFiles(chartDir, "*.phirarec"))
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (long.TryParse(fileName, out var timestamp))
                {
                    // 这里 recordId 暂时填 0，因为在文件列表中获取 recordId 比较慢
                    replays.Add(new { timestamp, recordId = 0 });
                }
            }
            
            if (replays.Count > 0)
            {
                chartList.Add(new { chartId, replays });
            }
        }
        return chartList;
    }

    private void CleanupSessions()
    {
        var now = _clock.UtcNow;
        var expired = _sessions.Where(kvp => kvp.Value.ExpiresAt <= now).Select(kvp => kvp.Key).ToList();
        foreach (var key in expired) _sessions.TryRemove(key, out _);
    }

    private void CleanupOldReplays()
    {
        var recordRoot = Path.Combine(AppContext.BaseDirectory, "record");
        if (!Directory.Exists(recordRoot)) return;

        var now = DateTimeOffset.UtcNow;
        var threshold = now.AddDays(-4).ToUnixTimeMilliseconds();

        try
        {
            foreach (var userDir in Directory.GetDirectories(recordRoot))
            {
                foreach (var chartDir in Directory.GetDirectories(userDir))
                {
                    foreach (var file in Directory.GetFiles(chartDir, "*.phirarec"))
                    {
                        var fileName = Path.GetFileNameWithoutExtension(file);
                        if (long.TryParse(fileName, out var timestamp) && timestamp < threshold)
                        {
                            File.Delete(file);
                        }
                    }
                    // 清理空文件夹
                    if (!Directory.EnumerateFileSystemEntries(chartDir).Any()) Directory.Delete(chartDir);
                }
                if (!Directory.EnumerateFileSystemEntries(userDir).Any()) Directory.Delete(userDir);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to cleanup old replays: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
