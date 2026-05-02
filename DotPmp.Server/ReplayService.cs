using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Internal;
using ZstdSharp;

namespace DotPmp.Server;

public record ReplaySession(long UserId, DateTimeOffset ExpiresAt);

public class ReplayService : IDisposable
{
    private static readonly TimeSpan ReplayRetention = TimeSpan.FromDays(4);

    private readonly ISystemClock _clock;
    private readonly ServerConfig _config;
    private readonly PhiraAuthService _phiraAuthService;

    private readonly ConcurrentDictionary<string, ReplaySession> _sessions = new();

    private readonly Timer _cleanupTimer;
    private readonly Timer _replayCleanupTimer;

    public ReplayService(ISystemClock clock, ServerConfig config, PhiraAuthService phiraAuthService)
    {
        _clock = clock;
        _config = config;
        _phiraAuthService = phiraAuthService;

        _cleanupTimer = new Timer(_ =>
        {
            CleanupSessions();
        }, null, TimeSpan.Zero, TimeSpan.FromHours(1));

        var now = _clock.UtcNow;
        var nextMidnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset).AddDays(1);
        _replayCleanupTimer = new Timer(_ =>
        {
            CleanupReplayFiles();
        }, null, nextMidnight - now, TimeSpan.FromDays(1));
    }

    public async Task<(bool Ok, object? Data)> AuthenticateAsync(string token)
    {
        var user = await AuthenticateUserAsync(token);

        if (user == null)
            return (false, "unauthorized");

        var sessionToken = Guid.NewGuid().ToString();

        var expiresAt = _clock.UtcNow.AddMinutes(30);

        _sessions[sessionToken] = new ReplaySession(user.Id, expiresAt);

        var charts = GetUserReplayList(user.Id);

        return (true, new
        {
            ok = true,
            userId = user.Id,
            charts,
            sessionToken,
            expiresAt = expiresAt.ToUnixTimeMilliseconds()
        });
    }

    public async Task<PhiraUserInfo?> AuthenticateUserAsync(string token)
    {
        var authResult = await _phiraAuthService.AuthenticateUserAsync(token);
        return authResult?.User;
    }

    public ReplaySession? GetSession(string token)
    {   
        Console.WriteLine($"session count: {_sessions.Count}");
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
            GetReplayRootPath(),
            userId.ToString(),
            chartId.ToString(),
            $"{timestamp}.phirarec"
        );
    }

    public string GetReplayRootPath()
    {
        return Path.IsPathRooted(_config.ReplayDataPath)
            ? _config.ReplayDataPath
            : Path.Combine(AppContext.BaseDirectory, _config.ReplayDataPath);
    }

    public bool IsReplayPathAllowed(string path)
    {
        var root = Path.GetFullPath(GetReplayRootPath());
        var target = Path.GetFullPath(path);
        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || target == root;
    }

    public bool TryReadReplayHeader(string path, out ReplayFileHeader? header)
    {
        header = null;

        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 14)
                return false;

            if (data.Length >= 8 && Encoding.ASCII.GetString(data, 0, 8) == "PHIRAREC")
            {
                var reader = new DotPmp.Common.BinaryReader(data[8..]);
                var version = reader.ReadInt32();
                if (version == 0)
                {
                    var recordId = reader.ReadInt32();
                    var chartId = reader.ReadInt32();
                    _ = reader.ReadString();
                    var userId = reader.ReadInt32();
                    _ = reader.ReadString();
                    header = new ReplayFileHeader(chartId, userId, recordId);
                    return true;
                }

                if (version == 1)
                {
                    var compressionType = reader.ReadByte();
                    var payloadBytes = data.AsSpan(8 + 4 + 1).ToArray();
                    byte[] decompressedPayload = compressionType switch
                    {
                        0x00 => payloadBytes,
                        0x01 => new Decompressor().Unwrap(payloadBytes).ToArray(),
                        _ => Array.Empty<byte>()
                    };

                    if (decompressedPayload.Length == 0)
                        return false;

                    var payloadReader = new DotPmp.Common.BinaryReader(decompressedPayload);
                    var recordId = payloadReader.ReadInt32();
                    _ = payloadReader.ReadInt64();
                    var chartId = payloadReader.ReadInt32();
                    _ = payloadReader.ReadString();
                    var userId = payloadReader.ReadInt32();
                    _ = payloadReader.ReadString();
                    header = new ReplayFileHeader(chartId, userId, recordId);
                    return true;
                }

                return false;
            }

            var magic = BitConverter.ToUInt16(data, 0);
            if (magic != 0x504D)
                return false;

            header = new ReplayFileHeader(
                (int)BitConverter.ToUInt32(data, 2),
                (int)BitConverter.ToUInt32(data, 6),
                (int)BitConverter.ToUInt32(data, 10));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private object GetUserReplayList(long userId)
    {
        var userPath = Path.Combine(GetReplayRootPath(), userId.ToString());

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
                    var recordId = TryReadReplayHeader(file, out var header) ? header!.RecordId : 0;
                    var deleteAt = DateTimeOffset.FromUnixTimeMilliseconds(ts).Add(ReplayRetention).ToUnixTimeMilliseconds();
                    replays.Add(new
                    {
                        timestamp = ts,
                        recordId,
                        expiresAt = deleteAt,
                        deleteAt
                    });
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

    private void CleanupReplayFiles()
    {
        var root = GetReplayRootPath();
        if (!Directory.Exists(root))
            return;

        var cutoff = _clock.UtcNow - ReplayRetention;
        foreach (var file in Directory.EnumerateFiles(root, "*.phirarec", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!long.TryParse(name, out var timestamp))
                continue;

            if (DateTimeOffset.FromUnixTimeMilliseconds(timestamp) > cutoff)
                continue;

            try
            {
                File.Delete(file);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _replayCleanupTimer.Dispose();
    }
}

public record ReplayFileHeader(int ChartId, int UserId, int RecordId);
