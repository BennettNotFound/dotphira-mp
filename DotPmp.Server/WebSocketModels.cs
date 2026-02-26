using System.Text.Json.Serialization;

namespace DotPmp.Server;

// --- WebSocket 消息类型 ---
public static class WsMessageType
{
    public const string Subscribe = "subscribe";
    public const string Unsubscribe = "unsubscribe";
    public const string Ping = "ping";
    public const string Subscribed = "subscribed";
    public const string Unsubscribed = "unsubscribed";
    public const string Pong = "pong";
    public const string RoomUpdate = "room_update";
    public const string RoomLog = "room_log";
    public const string Error = "error";
    public const string AdminSubscribe = "admin_subscribe";
    public const string AdminUnsubscribe = "admin_unsubscribe";
    public const string AdminSubscribed = "admin_subscribed";
    public const string AdminUnsubscribed = "admin_unsubscribed";
    public const string AdminUpdate = "admin_update";
}

// --- 基础消息接口 ---
public interface IWebSocketMessage
{
    [JsonPropertyName("type")]
    string Type { get; }
}

// --- 客户端请求消息 ---
public record WebSocketSubscribeRequest(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Subscribe,
    [property: JsonPropertyName("roomId")] string RoomId = "",
    [property: JsonPropertyName("userId")] int? UserId = null
) : IWebSocketMessage;

public record WebSocketUnsubscribeRequest(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Unsubscribe
) : IWebSocketMessage;

public record WebSocketPingRequest(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Ping
) : IWebSocketMessage;

public record WebSocketAdminSubscribeRequest(
    [property: JsonPropertyName("type")] string Type = WsMessageType.AdminSubscribe,
    [property: JsonPropertyName("token")] string Token = ""
) : IWebSocketMessage;

public record WebSocketAdminUnsubscribeRequest(
    [property: JsonPropertyName("type")] string Type = WsMessageType.AdminUnsubscribe
) : IWebSocketMessage;

// --- 服务器响应消息 ---
public record WebSocketSubscribedResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Subscribed,
    [property: JsonPropertyName("roomId")] string RoomId = ""
) : IWebSocketMessage;

public record WebSocketUnsubscribedResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Unsubscribed
) : IWebSocketMessage;

public record WebSocketPongResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Pong
) : IWebSocketMessage;

public record WebSocketErrorResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.Error,
    [property: JsonPropertyName("message")] string Message = ""
) : IWebSocketMessage;

public record WebSocketAdminSubscribedResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.AdminSubscribed
) : IWebSocketMessage;

public record WebSocketAdminUnsubscribedResponse(
    [property: JsonPropertyName("type")] string Type = WsMessageType.AdminUnsubscribed
) : IWebSocketMessage;

// --- 房间更新数据模型 ---
public record WebSocketRoomUserInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_ready")] bool IsReady
);

public record WebSocketRoomMonitorInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name
);

public record WebSocketRoomChartInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("id")] int Id
);

public record WebSocketRoomHostInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name
);

public record WebSocketRoomUpdateData(
    [property: JsonPropertyName("roomid")] string RoomId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("cycle")] bool Cycle,
    [property: JsonPropertyName("live")] bool Live,
    [property: JsonPropertyName("chart")] WebSocketRoomChartInfo? Chart,
    [property: JsonPropertyName("host")] WebSocketRoomHostInfo Host,
    [property: JsonPropertyName("users")] List<WebSocketRoomUserInfo> Users,
    [property: JsonPropertyName("monitors")] List<WebSocketRoomMonitorInfo> Monitors
);

public record WebSocketRoomUpdateMessage(
    [property: JsonPropertyName("type")] string Type = WsMessageType.RoomUpdate,
    [property: JsonPropertyName("data")] WebSocketRoomUpdateData? Data = null
) : IWebSocketMessage;

// --- 房间日志数据模型 ---
public record WebSocketRoomLogData(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("timestamp")] long Timestamp
);

public record WebSocketRoomLogMessage(
    [property: JsonPropertyName("type")] string Type = WsMessageType.RoomLog,
    [property: JsonPropertyName("data")] WebSocketRoomLogData? Data = null
) : IWebSocketMessage;

// --- 管理员更新数据模型 ---
public record WebSocketAdminRoomState(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("ready_users")] List<long> ReadyUsers,
    [property: JsonPropertyName("ready_count")] int ReadyCount,
    [property: JsonPropertyName("results_count")] int ResultsCount,
    [property: JsonPropertyName("aborted_count")] int AbortedCount,
    [property: JsonPropertyName("finished_users")] List<long> FinishedUsers,
    [property: JsonPropertyName("aborted_users")] List<long> AbortedUsers
);

public record WebSocketAdminContestInfo(
    [property: JsonPropertyName("whitelist_count")] int WhitelistCount,
    [property: JsonPropertyName("whitelist")] List<long> Whitelist,
    [property: JsonPropertyName("manual_start")] bool ManualStart,
    [property: JsonPropertyName("auto_disband")] bool AutoDisband
);

public record WebSocketAdminUserInfo(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("connected")] bool Connected,
    [property: JsonPropertyName("is_host")] bool IsHost,
    [property: JsonPropertyName("game_time")] float GameTime,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("finished")] bool Finished,
    [property: JsonPropertyName("aborted")] bool Aborted,
    [property: JsonPropertyName("record_id")] int? RecordId
);

public record WebSocketAdminRoomInfo(
    [property: JsonPropertyName("roomid")] string RoomId,
    [property: JsonPropertyName("max_users")] int MaxUsers,
    [property: JsonPropertyName("current_users")] int CurrentUsers,
    [property: JsonPropertyName("current_monitors")] int CurrentMonitors,
    [property: JsonPropertyName("replay_eligible")] bool ReplayEligible,
    [property: JsonPropertyName("live")] bool Live,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("cycle")] bool Cycle,
    [property: JsonPropertyName("host")] WebSocketRoomHostInfo Host,
    [property: JsonPropertyName("state")] WebSocketAdminRoomState State,
    [property: JsonPropertyName("chart")] WebSocketRoomChartInfo? Chart,
    [property: JsonPropertyName("contest")] WebSocketAdminContestInfo Contest,
    [property: JsonPropertyName("users")] List<WebSocketAdminUserInfo> Users,
    [property: JsonPropertyName("monitors")] List<WebSocketAdminUserInfo> Monitors
);

public record WebSocketAdminUpdateData(
    [property: JsonPropertyName("timestamp")] long Timestamp,
    [property: JsonPropertyName("changes")] WebSocketAdminChangesData Changes
);

public record WebSocketAdminChangesData(
    [property: JsonPropertyName("rooms")] List<WebSocketAdminRoomInfo> Rooms,
    [property: JsonPropertyName("total_rooms")] int TotalRooms
);

public record WebSocketAdminUpdateMessage(
    [property: JsonPropertyName("type")] string Type = WsMessageType.AdminUpdate,
    [property: JsonPropertyName("data")] WebSocketAdminUpdateData? Data = null
) : IWebSocketMessage;