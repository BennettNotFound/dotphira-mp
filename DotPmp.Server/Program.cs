using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotPmp.Server;

// --- 1. 配置加载 ---
var builder = WebApplication.CreateBuilder(args);
var config = new ServerConfig();
builder.Configuration.AddJsonFile("config.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// 处理环境变量映射
var envHttpService = Environment.GetEnvironmentVariable("HTTP_SERVICE");
if (!string.IsNullOrEmpty(envHttpService)) config.HttpService = bool.TryParse(envHttpService, out var b) ? b : config.HttpService;
var envHttpPort = Environment.GetEnvironmentVariable("HTTP_PORT");
if (!string.IsNullOrEmpty(envHttpPort)) config.HttpPort = int.TryParse(envHttpPort, out var p) ? p : config.HttpPort;
var envAdminToken = Environment.GetEnvironmentVariable("ADMIN_TOKEN");
if (!string.IsNullOrEmpty(envAdminToken)) config.AdminToken = envAdminToken;
var envAdminDataPath = Environment.GetEnvironmentVariable("ADMIN_DATA_PATH") ?? Environment.GetEnvironmentVariable("PHIRA_MP_HOME");
if (!string.IsNullOrEmpty(envAdminDataPath)) config.AdminDataPath = (Directory.Exists(envAdminDataPath) || !envAdminDataPath.EndsWith(".json")) ? Path.Combine(envAdminDataPath, "admin_data.json") : envAdminDataPath;

builder.Configuration.Bind(config);

// --- 2. 注册服务 ---
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<ServerState>();
builder.Services.AddSingleton<AdminDataService>();
builder.Services.AddSingleton<Microsoft.Extensions.Internal.ISystemClock, Microsoft.Extensions.Internal.SystemClock>();
builder.Services.AddSingleton<OtpService>();
builder.Services.AddSingleton<IpBlacklistService>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddCors();

var app = builder.Build();
var adminDataService = app.Services.GetRequiredService<AdminDataService>();
adminDataService.Load();
app.MapReplayApi();

// --- 3. 后台 TCP 游戏服务器逻辑 (核心修复点) ---
app.Lifetime.ApplicationStarted.Register(() =>
{
    var serverState = app.Services.GetRequiredService<ServerState>();
    var serverConfig = app.Services.GetRequiredService<ServerConfig>();
    var cancellationToken = app.Lifetime.ApplicationStopping;
    Task.Run(async () => {
        var listener = new TcpListener(IPAddress.IPv6Any, serverConfig.GamePort);
        listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
        listener.Start();
        Console.WriteLine($"Game server listening on 0.0.0.0:{serverConfig.GamePort}");

        try {
            while (!cancellationToken.IsCancellationRequested) {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(async () => {
                    var sessionId = Guid.NewGuid();
                    try {
                        // 创建 Session
                        var session = new Session(sessionId, client, serverState, serverConfig.WelcomeMessage);
                        serverState.AddSession(sessionId, session);
                        
                        // 【核心修复】：必须等待连接关闭，否则任务结束 session 会被销毁
                        // Session 内部会通过 HeartbeatMonitor 和 NetworkStream 进行通信
                        // 我们在这里等待直到底层 Socket 断开
                        while (client.Connected && !cancellationToken.IsCancellationRequested) {
                            await Task.Delay(1000, cancellationToken);
                        }
                    } catch {
                        // 异常处理
                    } finally {
                        await serverState.OnConnectionLostAsync(sessionId);
                        client.Dispose();
                    }
                }, cancellationToken);
            }
        } catch (OperationCanceledException) { }
        finally { listener.Stop(); }
    }, cancellationToken);
});

// --- 4. HTTP API 管道配置 ---
if (config.HttpService)
{
    app.UseMiddleware<IpBlacklistMiddleware>();
    app.UseCors(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    app.UseMiddleware<AdminApiMiddleware>();
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapGet("/", () => Results.Redirect("/index.html"));

    // 旧路径兼容 (复数 rooms)
    app.MapGet("/rooms", (ServerState server) => Results.Ok(new {
        count = server.GetRoomCount(),
        rooms = server.GetAllRooms().Select(r => new {
            id = r.Id,
            state = r.State.ToString(),
            hostId = r.Host.Id,
            hostName = r.Host.Name,
            playerCount = r.GetPlayerCount(),
            monitorCount = r.GetMonitorCount(),
            isLocked = r.IsLocked,
            isCycle = r.IsCycle,
            isLive = r.IsLive,
            isRecruiting = r.IsRecruiting,
            selectedChartId = r.SelectedChartId,
            players = r.GetPlayers().Select(u => new { id = u.Id, name = u.Name, isMonitor = u.IsMonitor }).ToList()
        })
    }));

    // 新规范路径 (单数 room)
    app.MapGet("/room", (ServerState server) => {
        var roomList = server.GetAllRooms().Select(r => new {
            roomid = r.Id,
            cycle = r.IsCycle,
            @lock = r.IsLocked,
            host = new { name = r.Host.Name, id = r.Host.Id.ToString() },
            state = r.State.ToString().ToLower(),
            chart = r.SelectedChartId.HasValue ? new { name = $"Chart-{r.SelectedChartId}", id = r.SelectedChartId.Value.ToString() } : null,
            players = r.GetPlayers().Select(p => new { p.Name, id = p.Id }).ToList()
        }).ToList();
        return Results.Ok(new { rooms = roomList, total = roomList.Count });
    });

    app.MapGet("/status", (ServerState server, ServerConfig sc) => Results.Ok(new {
        serverName = sc.ServerName,
        version = "1.0.0",
        uptime = DateTime.UtcNow.ToString("o"),
        roomCount = server.GetRoomCount(),
        sessionCount = server.GetSessionCount(),
        userCount = server.GetUserCount()
    }));

    app.MapAdminApi();
}

var listenUrl = $"http://0.0.0.0:{config.HttpPort}";
app.Urls.Add(listenUrl);
app.Run();

namespace DotPmp.Server {
    public class ServerConfig {
        public int GamePort { get; set; } = 12346;
        public int HttpPort { get; set; } = 12347;
        public string ServerName { get; set; } = "DotPmp Server";
        public string WelcomeMessage { get; set; } = "[L]欢迎来到L的联机服务器!!!";
        public bool HttpService { get; set; } = true;
        public string? AdminToken { get; set; }
        public string AdminDataPath { get; set; } = "admin_data.json";
    }
}
