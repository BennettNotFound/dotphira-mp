using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Internal;

namespace DotPmp.Server;

public record BlacklistedIpInfo(string Ip, long ExpiresIn);

public class IpBlacklistService : IDisposable
{
    private readonly ISystemClock _clock;
    private readonly Timer _cleanupTimer;
    private readonly ConcurrentDictionary<IPAddress, DateTime> _blacklist = new();

    public IpBlacklistService(ISystemClock clock)
    {
        _clock = clock;
        // 每分钟检查一次过期的IP
        _cleanupTimer = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool IsBlacklisted(IPAddress ip)
    {
        if (_blacklist.TryGetValue(ip, out var expiresAt))
        {
            if (expiresAt > _clock.UtcNow)
            {
                return true;
            }
            // 如果已过期，则顺便移除
            _blacklist.TryRemove(ip, out _);
        }
        return false;
    }

    public void BlacklistIp(IPAddress ip, TimeSpan duration)
    {
        _blacklist[ip] = _clock.UtcNow.Add(duration).DateTime;
    }

    public bool Remove(IPAddress ip)
    {
        return _blacklist.TryRemove(ip, out _);
    }

    public void Clear()
    {
        _blacklist.Clear();
    }

    public List<BlacklistedIpInfo> GetBlacklist()
    {
        var now = _clock.UtcNow;
        return _blacklist
            .Where(kvp => kvp.Value > now)
            .Select(kvp => new BlacklistedIpInfo(
                kvp.Key.ToString(),
                (long)(kvp.Value - now.DateTime).TotalMilliseconds
            ))
            .ToList();
    }

    private void Cleanup()
    {
        var now = _clock.UtcNow;
        var expiredKeys = _blacklist.Where(kvp => kvp.Value <= now).Select(kvp => kvp.Key).ToList();
        foreach (var key in expiredKeys)
        {
            _blacklist.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
