using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Primitives;

namespace DotPmp.Server;

// --- 1. API 请求/响应模型 (全量统一定义) ---
public record MaxUsersRequest([property: JsonPropertyName("maxUsers")] int MaxUsers);
public record EnabledRequest([property: JsonPropertyName("enabled")] bool Enabled);
public record BanUserRequest([property: JsonPropertyName("userId")] long UserId, [property: JsonPropertyName("banned")] bool Banned, [property: JsonPropertyName("disconnect")] bool Disconnect);
public record OtpVerifyRequest([property: JsonPropertyName("ssid")] string Ssid, [property: JsonPropertyName("otp")] string Otp);
public record IpRequest([property: JsonPropertyName("ip")] string Ip);
public record MessageRequest([property: JsonPropertyName("message")] string Message);
public record RoomBanRequest([property: JsonPropertyName("userId")] long UserId, [property: JsonPropertyName("roomId")] string RoomId, [property: JsonPropertyName("banned")] bool Banned);
public record ContestConfigRequest([property: JsonPropertyName("enabled")] bool Enabled, [property: JsonPropertyName("whitelist")] List<long>? Whitelist);
public record WhitelistRequest([property: JsonPropertyName("userIds")] List<long> UserIds);
public record ContestStartRequest([property: JsonPropertyName("force")] bool Force = false);
public record MoveUserRequest([property: JsonPropertyName("roomId")] string RoomId, [property: JsonPropertyName("monitor")] bool Monitor);

public record AdminUserDetails(long Id, string Name, bool Monitor, bool Connected, string? Room, bool Banned);

// --- 2. 认证中间件 ---
public class AdminApiMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _adminToken;
    private readonly string? _viewToken;
    private readonly OtpService _otpService;

    public AdminApiMiddleware(RequestDelegate next, ServerConfig config, OtpService otpService)
    {
        _next = next;
        _adminToken = string.IsNullOrWhiteSpace(config.AdminToken) ? null : config.AdminToken;
        _viewToken = string.IsNullOrWhiteSpace(config.ViewToken) ? null : config.ViewToken;
        _otpService = otpService;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/admin")) { await _next(context); return; }
        if (context.Request.Path.StartsWithSegments("/admin/otp")) { await _next(context); return; }

        var token = ExtractToken(context.Request);

        // 检查管理员 Token（完全权限）
        if (!string.IsNullOrEmpty(token) && token == _adminToken)
        {
            await _next(context);
            return;
        }

        // 检查临时 Token
        if (!string.IsNullOrEmpty(token) && _otpService.ValidateTempToken(token, context.Connection.RemoteIpAddress))
        {
            await _next(context);
            return;
        }

        // 检查查询 Token（只读权限）
        if (!string.IsNullOrEmpty(token) && token == _viewToken)
        {
            // ViewToken 只允许 GET 请求
            if (context.Request.Method == "GET")
            {
                await _next(context);
                return;
            }
            else
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { ok = false, error = "view-token-readonly" });
                return;
            }
        }

        if (_adminToken is null)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { ok = false, error = "admin-disabled" });
            return;
        }

        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { ok = false, error = "unauthorized" });
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Query.TryGetValue("token", out var v)) return v.FirstOrDefault();
        if (request.Headers.TryGetValue("X-Admin-Token", out v)) return v.FirstOrDefault();
        if (request.Headers.TryGetValue("Authorization", out v)) {
            var b = v.FirstOrDefault();
            if (b?.StartsWith("Bearer ") == true) return b.Substring(7);
        }
        return null;
    }
}

// --- 3. 路由注册 ---
public static class AdminApiEndpoints
{
    public static WebApplication MapAdminApi(this WebApplication app)
    {
        var adminGroup = app.MapGroup("/admin");

        // --- OTP 认证 ---
        adminGroup.MapPost("/otp/request", (OtpService otp, ServerConfig cfg, ILogger<Program> log) => {
            if (!string.IsNullOrEmpty(cfg.AdminToken)) return Results.Json(new { ok = false, error = "otp-disabled-when-token-configured" }, statusCode: 403);
            var (ssid, req) = otp.CreateOtpRequest();
            log.LogInformation("[OTP Request] SSID: {Ssid}, OTP: {Otp}", ssid, req.Otp);
            return Results.Ok(new { ok = true, ssid, expiresIn = 300000 });
        });

        adminGroup.MapPost("/otp/verify", (OtpVerifyRequest body, OtpService otp, ServerConfig cfg, HttpContext http) => {
            if (!string.IsNullOrEmpty(cfg.AdminToken)) return Results.Json(new { ok = false, error = "otp-disabled-when-token-configured" }, statusCode: 403);
            var token = otp.VerifyOtp(body.Ssid, body.Otp, http.Connection.RemoteIpAddress!);
            if (token == null) return Results.Json(new { ok = false, error = "invalid-or-expired-otp" }, statusCode: 401);
            return Results.Ok(new { ok = true, token = token.Token, expiresAt = new DateTimeOffset(token.ExpiresAt).ToUnixTimeMilliseconds(), expiresIn = 14400000 });
        });

        // --- 房间管理 ---
        adminGroup.MapGet("/rooms", (ServerState server) => Results.Ok(new { ok = true, rooms = server.GetAllRooms().Select(r => new AdminRoom(r)) }));

        adminGroup.MapPost("/rooms/{roomId}/max_users", async (string roomId, MaxUsersRequest body, ServerState server) => {
            var room = await server.GetRoomAsync(roomId);
            if (room == null) return Results.NotFound(new { ok = false, error = "room-not-found" });
            room.MaxPlayerCount = body.MaxUsers;
            return Results.Ok(new { ok = true, roomid = roomId, max_users = room.MaxPlayerCount });
        });

        adminGroup.MapPost("/rooms/{roomId}/disband", async (string roomId, ServerState server) => {
            await server.DisbandRoomAsync(roomId,"管理员已强制解散该房间");
            return Results.Ok(new { ok = true, roomid = roomId });
        });

        adminGroup.MapPost("/rooms/{roomId}/chat", async (string roomId, MessageRequest body, ServerState server) => {
            if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 200) return Results.BadRequest(new { ok = false, error = "bad-message" });
            var room = await server.GetRoomAsync(roomId);
            if (room == null) return Results.NotFound(new { ok = false, error = "room-not-found" });
            await room.SendMessageAsync(new Common.Message.Chat(0, body.Message));
            return Results.Ok(new { ok = true });
        });

        // --- 全局广播 ---
        adminGroup.MapPost("/broadcast", async (MessageRequest body, ServerState server) => {
            if (string.IsNullOrWhiteSpace(body.Message) || body.Message.Length > 200) return Results.BadRequest(new { ok = false, error = "bad-message" });
            var rooms = server.GetAllRooms().ToList();
            var msg = new Common.Message.Chat(0, body.Message);
            foreach (var r in rooms) await r.SendMessageAsync(msg);
            return Results.Ok(new { ok = true, rooms = rooms.Count });
        });

        // --- 功能开关 ---
        adminGroup.MapGet("/replay/config", (ServerState server) => Results.Ok(new { ok = true, enabled = server.ReplayRecordingEnabled }));
        adminGroup.MapPost("/replay/config", (EnabledRequest body, ServerState server) => {
            server.ReplayRecordingEnabled = body.Enabled;
            return Results.Ok(new { ok = true, enabled = server.ReplayRecordingEnabled });
        });

        adminGroup.MapGet("/room-creation/config", (ServerState server) => Results.Ok(new { ok = true, enabled = server.RoomCreationEnabled }));
        adminGroup.MapPost("/room-creation/config", (EnabledRequest body, ServerState server) => {
            server.RoomCreationEnabled = body.Enabled;
            return Results.Ok(new { ok = true, enabled = server.RoomCreationEnabled });
        });

        // --- IP 黑名单管理 ---
        adminGroup.MapGet("/ip-blacklist", (IpBlacklistService blacklist) => Results.Ok(new { ok = true, blacklist = blacklist.GetBlacklist() }));
        adminGroup.MapPost("/ip-blacklist/remove", (IpRequest body, IpBlacklistService blacklist) => {
            if (!System.Net.IPAddress.TryParse(body.Ip, out var ipAddress)) return Results.BadRequest(new { ok = false, error = "bad-ip-address" });
            blacklist.Remove(ipAddress);
            return Results.Ok(new { ok = true });
        });
        adminGroup.MapPost("/ip-blacklist/clear", (IpBlacklistService blacklist) => {
            blacklist.Clear();
            return Results.Ok(new { ok = true });
        });
        adminGroup.MapGet("/log-rate", () => Results.Ok(new { ok = true, rate = new { logsPerSecond = 0, threshold = 100 } }));

        // --- 用户管理 ---
        adminGroup.MapGet("/users/{id:long}", (long id, ServerState server) => {
            var user = server.GetUser(id);
            if (user == null) return Results.NotFound(new { ok = false, error = "user-not-found" });
            return Results.Ok(new { ok = true, user = new AdminUserDetails(user.Id, user.Name, user.IsMonitor, user.IsConnected, user.Room?.Id, server.IsUserBanned(id)) });
        });

        adminGroup.MapPost("/ban/user", async (BanUserRequest body, ServerState server) => {
            if (body.Banned) await server.BanUserAsync(body.UserId); else await server.UnbanUserAsync(body.UserId);
            if (body.Banned && body.Disconnect) {
                var u = server.GetUser(body.UserId);
                if (u?.Session != null) await u.Session.CloseAsync();
            }
            return Results.Ok(new { ok = true });
        });

        adminGroup.MapPost("/ban/room", async (RoomBanRequest body, ServerState server) => {
            if (body.Banned) await server.BanFromRoomAsync(body.RoomId, body.UserId); else await server.UnbanFromRoomAsync(body.RoomId, body.UserId);
            return Results.Ok(new { ok = true });
        });

        adminGroup.MapPost("/users/{id:long}/disconnect", async (long id, ServerState server) => {
            var u = server.GetUser(id);
            if (u?.Session == null) return Results.NotFound(new { ok = false, error = "user-not-connected" });
            await u.Session.CloseAsync();
            return Results.Ok(new { ok = true });
        });

        adminGroup.MapPost("/users/{id:long}/move", async (long id, MoveUserRequest body, ServerState server) => {
            var user = server.GetUser(id);
            if (user == null) return Results.NotFound(new { ok = false, error = "user-not-found" });
            if (user.IsConnected) return Results.BadRequest(new { ok = false, error = "user-must-be-disconnected" });
            var targetRoom = await server.GetRoomAsync(body.RoomId);
            if (targetRoom == null) return Results.NotFound(new { ok = false, error = "target-room-not-found" });
            if (targetRoom.State != Common.RoomState.SelectChart) return Results.BadRequest(new { ok = false, error = "target-room-must-be-in-selectchart" });
            
            user.Room?.RemoveUserSilently(user);
            if (!await targetRoom.AddUserAsync(user, body.Monitor)) return Results.BadRequest(new { ok = false, error = "target-room-full" });
            user.Room = targetRoom;
            user.IsMonitor = body.Monitor;
            return Results.Ok(new { ok = true });
        });

        // --- 比赛模式 ---
        adminGroup.MapPost("/contest/rooms/{roomId}/config", async (string roomId, ContestConfigRequest body, ServerState server) => {
            var r = await server.GetRoomAsync(roomId);
            if (r == null) return Results.NotFound(new { ok = false, error = "room-not-found" });
            r.IsContestMode = body.Enabled;
            r.ContestWhitelist.Clear();
            if (body.Whitelist != null) foreach (var id in body.Whitelist) r.ContestWhitelist.Add(id);
            else foreach (var u in r.GetAllUsers()) r.ContestWhitelist.Add(u.Id);
            return Results.Ok(new { ok = true });
        });

        adminGroup.MapPost("/contest/rooms/{roomId}/whitelist", async (string roomId, WhitelistRequest body, ServerState server) => {
            var r = await server.GetRoomAsync(roomId);
            if (r == null) return Results.NotFound(new { ok = false, error = "room-not-found" });
            foreach (var id in body.UserIds) r.ContestWhitelist.Add(id);
            foreach (var u in r.GetAllUsers()) r.ContestWhitelist.Add(u.Id);
            return Results.Ok(new { ok = true });
        });

        adminGroup.MapPost("/contest/rooms/{roomId}/start", async (string roomId, ContestStartRequest body, ServerState server) => {
            var r = await server.GetRoomAsync(roomId);
            if (r == null) return Results.NotFound(new { ok = false, error = "room-not-found" });
            try { await r.StartGameManuallyAsync(body.Force); return Results.Ok(new { ok = true }); }
            catch (Exception ex) { return Results.BadRequest(new { ok = false, error = ex.Message }); }
        });

        return app;
    }
}

// --- 4. 内部视图模型 ---
public record AdminUserBase(long Id, string Name);

public record AdminUser : AdminUserBase
{
    [JsonPropertyName("is_host")] public bool IsHost { get; init; }
    public bool Connected { get; init; }
    public float GameTime { get; init; }
    public bool Finished { get; init; }
    public bool Aborted { get; init; }
    [JsonPropertyName("record_id")] public int? RecordId { get; init; }

    public AdminUser(User user, Room room) : base(user.Id, user.Name)
    {
        Connected = user.IsConnected;
        IsHost = room.IsHost(user);
        GameTime = user.GameTime;
        if (room.State == Common.RoomState.Playing) {
            Finished = room.IsPlayerFinished((int)user.Id);
            Aborted = room.IsPlayerAborted((int)user.Id);
            RecordId = room.GetPlayerRecordId((int)user.Id);
        }
    }
}

public record AdminRoom
{
    [JsonPropertyName("roomid")] public string Roomid { get; }
    [JsonPropertyName("max_users")] public int MaxUsers { get; }
    public bool Live { get; }
    public bool Locked { get; }
    public bool Cycle { get; }
    public AdminUserBase Host { get; }
    public object State { get; }
    public List<AdminUser> Users { get; }

    public AdminRoom(Room room)
    {
        Roomid = room.Id;
        MaxUsers = room.MaxPlayerCount;
        Live = room.IsLive;
        Locked = room.IsLocked;
        Cycle = room.IsCycle;
        Host = new AdminUserBase(room.Host.Id, room.Host.Name);
        Users = room.GetPlayers().Select(u => new AdminUser(u, room)).ToList();
        
        if (room.State == Common.RoomState.Playing) {
            State = new { 
                type = "playing", 
                results_count = room.GetFinishedCount(), 
                aborted_count = room.GetAbortedCount(),
                finished_users = room.GetFinishedUserIds(),
                aborted_users = room.GetAbortedUserIds()
            };
        } else {
            State = new { type = room.State.ToString().ToLower() };
        }
    }
}
