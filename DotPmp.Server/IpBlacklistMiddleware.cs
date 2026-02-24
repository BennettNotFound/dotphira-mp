using System.Net;

namespace DotPmp.Server;

public class IpBlacklistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IpBlacklistService _blacklistService;
    private readonly ILogger<IpBlacklistMiddleware> _logger;

    public IpBlacklistMiddleware(RequestDelegate next, IpBlacklistService blacklistService, ILogger<IpBlacklistMiddleware> logger)
    {
        _next = next;
        _blacklistService = blacklistService;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;

        if (remoteIp != null && _blacklistService.IsBlacklisted(remoteIp))
        {
            _logger.LogWarning("Blocked request from blacklisted IP: {IpAddress}", remoteIp);
            context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        await _next(context);
    }
}
