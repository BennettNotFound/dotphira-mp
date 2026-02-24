using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Internal;

namespace DotPmp.Server;

// 代表一个待验证的OTP请求
public record OtpRequest(string Otp, DateTime ExpiresAt);

// 代表一个已颁发的临时管理员Token
public record TempAdminToken(string Token, DateTime ExpiresAt, IPAddress BoundIp);

public class OtpService
{
    private readonly ISystemClock _clock;
    private readonly ConcurrentDictionary<string, OtpRequest> _otpRequests = new();
    private readonly ConcurrentDictionary<string, TempAdminToken> _activeTokens = new();

    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TempTokenLifetime = TimeSpan.FromHours(4);

    public OtpService(ISystemClock clock)
    {
        _clock = clock;
    }

    public (string Ssid, OtpRequest Request) CreateOtpRequest()
    {
        var ssid = Guid.NewGuid().ToString();
        var otp = Convert.ToBase64String(RandomNumberGenerator.GetBytes(6))
            .Replace("+", "").Replace("/", "").Substring(0, 6).ToLower();

        var request = new OtpRequest(otp, _clock.UtcNow.Add(OtpLifetime).DateTime);
        _otpRequests[ssid] = request;
        return (ssid, request);
    }
    
    public TempAdminToken? VerifyOtp(string ssid, string otp, IPAddress ipAddress)
    {
        if (!_otpRequests.TryRemove(ssid, out var request)) return null;
        if (request.ExpiresAt < _clock.UtcNow || !string.Equals(request.Otp, otp, StringComparison.OrdinalIgnoreCase)) return null;

        var tokenValue = Guid.NewGuid().ToString();
        var tempToken = new TempAdminToken(tokenValue, _clock.UtcNow.Add(TempTokenLifetime).DateTime, ipAddress);
        _activeTokens[tokenValue] = tempToken;
        return tempToken;
    }

    public bool ValidateTempToken(string token, IPAddress? ipAddress)
    {
        if (!_activeTokens.TryGetValue(token, out var tempToken)) return false;
        if (tempToken.ExpiresAt < _clock.UtcNow) {
            _activeTokens.TryRemove(token, out _);
            return false;
        }

        var isIpMatch = tempToken.BoundIp.Equals(ipAddress) || 
                        (IPAddress.IsLoopback(tempToken.BoundIp) && ipAddress != null && IPAddress.IsLoopback(ipAddress));

        if (!isIpMatch) {
            _activeTokens.TryRemove(token, out _);
            return false;
        }
        return true;
    }
}
