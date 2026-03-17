using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.Extensions.Internal;

namespace DotPmp.Server;

public record ReplaySession(long UserId, DateTime ExpiresAt);

public record PhiraUserInfo(int Id, string Name);

public class ReplayService : IDisposable
{
    private const string PhiraHost = "https://phira.5wyxi.com";

    private static readonly HttpClient HttpClient = new();

    private readonly ISystemClock _clock;

    private readonly ConcurrentDictionary<string, ReplaySession> _sessions = new();

    private readonly Timer _cleanupTimer;

    public ReplayService(ISystemClock clock)
    {
        _clock = clock;

        _cleanupTimer = new Timer(_ =>
        {
            CleanupSessions();
        }, null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    public async Task<(bool Ok, object? Data)> AuthenticateAsync(string token)
    {
        var user = await AuthenticateUserAsync(token);

        if (user == null)
            return (false, "unauthorized");

        var sessionToken = Guid.NewGuid().ToString();

        var expiresAt = _clock.UtcNow.AddMinutes(30).DateTime;

        _sessions[sessionToken] = new ReplaySession(user.Id, expiresAt);

        var charts = GetUserReplayList(user.Id);

        return (true, new
        {
            ok = true,
            userId = user.Id,
            charts,
            sessionToken
        });
    }

    public async Task<PhiraUserInfo?> AuthenticateUserAsync(string token)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{PhiraHost}/me");

            request.Headers.Add("Authorization", $"Bearer {token}");

            var response = await HttpClient.SendAsync(request);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<PhiraUserInfo>();
        }
        catch
        {
            return null;
        }
    }

    public ReplaySession? GetSession(string token)
    {
        if (!_sessions.TryGetValue(token, out var session))
            return null;

        if (session.ExpiresAt < _clock.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        return session;
    }

    public string GetReplayPath(long userId, int chartId, long timestamp)
    {
        return Path.Combine(
            AppContext.BaseDirectory,
            "record",
            userId.ToString(),
            chartId.ToString(),
            $"{timestamp}.phirarec"
        );
    }

    private object GetUserReplayList(long userId)
    {
        var userPath = Path.Combine(AppContext.BaseDirectory, "record", userId.ToString());

        if (!Directory.Exists(userPath))
            return new List<object>();

        var result = new List<object>();

        foreach (var chartDir in Directory.GetDirectories(userPath))
        {
            var chartId = int.Parse(Path.GetFileName(chartDir));

            var replays = new List<object>();

            foreach (var file in Directory.GetFiles(chartDir, "*.phirarec"))
            {
                var name = Path.GetFileNameWithoutExtension(file);

                if (long.TryParse(name, out var ts))
                {
                    replays.Add(new { timestamp = ts, recordId = 0 });
                }
            }

            result.Add(new { chartId, replays });
        }

        return result;
    }

    private void CleanupSessions()
    {
        var now = _clock.UtcNow;

        var expired = _sessions
            .Where(x => x.Value.ExpiresAt <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expired)
            _sessions.TryRemove(key, out _);
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}