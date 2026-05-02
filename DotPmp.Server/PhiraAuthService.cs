using System.Collections.Concurrent;
using Microsoft.Extensions.Internal;

namespace DotPmp.Server;

public record PhiraUserInfo(int Id, string Name, string? Language = null);
public readonly record struct PhiraAuthResult(PhiraUserInfo User, bool FromCache);

public class PhiraAuthService : IDisposable
{
    private const string PhiraHost = "https://phira.5wyxi.com";
    private static readonly HttpClient HttpClient = new();

    private readonly ISystemClock _clock;
    private readonly TimeSpan _cacheDuration;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private readonly Timer? _cleanupTimer;

    public PhiraAuthService(ISystemClock clock, ServerConfig config)
    {
        _clock = clock;
        _cacheDuration = TimeSpan.FromMinutes(Math.Max(0, config.AuthorizationCacheMinutes));
        if (_cacheDuration > TimeSpan.Zero)
        {
            var cleanupInterval = _cacheDuration < TimeSpan.FromMinutes(1)
                ? TimeSpan.FromMinutes(1)
                : _cacheDuration;
            _cleanupTimer = new Timer(_ => CleanupExpiredEntries(), null, cleanupInterval, cleanupInterval);
        }
    }

    public async Task<PhiraAuthResult?> AuthenticateUserAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        if (_cacheDuration > TimeSpan.Zero &&
            _cache.TryGetValue(token, out var cached) &&
            cached.ExpiresAt > _clock.UtcNow)
        {
            return new PhiraAuthResult(cached.User, true);
        }

        var user = await FetchUserAsync(token);

        if (user != null && _cacheDuration > TimeSpan.Zero)
        {
            _cache[token] = new CacheEntry(user, _clock.UtcNow.Add(_cacheDuration));
        }

        return user == null ? null : new PhiraAuthResult(user, false);
    }

    private async Task<PhiraUserInfo?> FetchUserAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{PhiraHost}/me");
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

    private void CleanupExpiredEntries()
    {
        var now = _clock.UtcNow;
        var expiredKeys = _cache
            .Where(x => x.Value.ExpiresAt <= now)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expiredKeys)
            _cache.TryRemove(key, out _);
    }

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
    }

    private sealed record CacheEntry(PhiraUserInfo User, DateTimeOffset ExpiresAt);
}
