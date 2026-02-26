using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DotPmp.Server;

public enum WebSocketClientType
{
    RoomSubscriber,
    AdminSubscriber
}

public class WebSocketClient
{
    public Guid Id { get; set; }
    public WebSocket WebSocket { get; set; } = null!;
    public WebSocketClientType ClientType { get; set; }
    public string? SubscribedRoomId { get; set; }
    public DateTime LastPingTime { get; set; } = DateTime.UtcNow;
    public CancellationTokenSource CancellationTokenSource { get; set; } = null!;
    public Task? ReceiveTask { get; set; }
    public Task? SendTask { get; set; }
}

public class WebSocketService
{
    private readonly ConcurrentDictionary<Guid, WebSocketClient> _clients = new();
    private readonly ServerState _serverState;
    private readonly string? _adminToken;
    private readonly string? _viewToken;
    private readonly OtpService _otpService;
    private readonly ILogger<WebSocketService> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public WebSocketService(ServerState serverState, ServerConfig config, OtpService otpService, ILogger<WebSocketService> logger)
    {
        _serverState = serverState;
        _adminToken = config.AdminToken;
        _viewToken = config.ViewToken;
        _otpService = otpService;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    public async Task HandleConnectionAsync(WebSocket webSocket)
    {
        var clientId = Guid.NewGuid();
        var client = new WebSocketClient
        {
            Id = clientId,
            WebSocket = webSocket,
            ClientType = WebSocketClientType.RoomSubscriber,
            CancellationTokenSource = new CancellationTokenSource()
        };

        _clients[clientId] = client;
        _logger.LogInformation($"WebSocket client connected: {clientId}");

        try
        {
            // 启动接收消息的任务
            var receiveTask = ReceiveMessagesAsync(client);
            client.ReceiveTask = receiveTask;

            // 启动心跳检查的任务
            var heartbeatTask = MonitorHeartbeatAsync(client);
            client.SendTask = heartbeatTask;

            // 等待任一任务完成
            await Task.WhenAny(receiveTask, heartbeatTask);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"WebSocket error for client {clientId}");
        }
        finally
        {
            await DisconnectClientAsync(clientId);
        }
    }

    private async Task ReceiveMessagesAsync(WebSocketClient client)
    {
        var buffer = new byte[4096];

        while (!client.CancellationTokenSource.Token.IsCancellationRequested &&
                       client.WebSocket.State == WebSocketState.Open)
                    {
                        try
                        {
                            var result = await client.WebSocket.ReceiveAsync(
                                new ArraySegment<byte>(buffer),
                                client.CancellationTokenSource.Token);
        
                            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                            {
                                await client.WebSocket.CloseAsync(
                                    WebSocketCloseStatus.NormalClosure,
                                    "Closing",
                                    client.CancellationTokenSource.Token);
                                break;
                            }
        
                            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                            {
                                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                await HandleMessageAsync(client, message);
                            }
                        }
                        catch (WebSocketException ex)
                        {
                            _logger.LogWarning(ex, $"WebSocket receive error for client {client.Id}");
                            break;
                        }
                    }    }

    private async Task HandleMessageAsync(WebSocketClient client, string messageJson)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(messageJson);
            var root = jsonDoc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
            {
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "invalid-message"));
                return;
            }

            var typeValue = typeProp.GetString();
            if (string.IsNullOrEmpty(typeValue))
            {
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "invalid-message"));
                return;
            }

            switch (typeValue)
            {
                case "subscribe":
                    await HandleSubscribeAsync(client, messageJson);
                    break;
                case "unsubscribe":
                    await HandleUnsubscribeAsync(client);
                    break;
                case "ping":
                    client.LastPingTime = DateTime.UtcNow;
                    await SendMessageAsync(client, new WebSocketPongResponse());
                    break;
                case "admin_subscribe":
                    await HandleAdminSubscribeAsync(client, messageJson);
                    break;
                case "admin_unsubscribe":
                    await HandleAdminUnsubscribeAsync(client);
                    break;
                default:
                    await SendMessageAsync(client, new WebSocketErrorResponse(Message: "invalid-message"));
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, $"Failed to parse WebSocket message");
            await SendMessageAsync(client, new WebSocketErrorResponse(Message: "invalid-message"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebSocket message");
            await SendMessageAsync(client, new WebSocketErrorResponse(Message: "internal-error"));
        }
    }

    private async Task HandleSubscribeAsync(WebSocketClient client, string messageJson)
    {
        try
        {
            var request = JsonSerializer.Deserialize<WebSocketSubscribeRequest>(messageJson, _jsonOptions);
            if (request == null || string.IsNullOrEmpty(request.RoomId))
            {
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "invalid-room-id"));
                return;
            }

            // 验证房间是否存在
            var room = await _serverState.GetRoomAsync(request.RoomId);
            if (room == null)
            {
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "room-not-found"));
                return;
            }

            // 如果之前有订阅，先取消
            if (!string.IsNullOrEmpty(client.SubscribedRoomId))
            {
                client.SubscribedRoomId = null;
            }

            client.ClientType = WebSocketClientType.RoomSubscriber;
            client.SubscribedRoomId = request.RoomId;

            await SendMessageAsync(client, new WebSocketSubscribedResponse(RoomId: request.RoomId));

            // 立即发送当前房间状态
            await SendRoomUpdateAsync(request.RoomId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling subscribe");
            await SendMessageAsync(client, new WebSocketErrorResponse(Message: "internal-error"));
        }
    }

    private async Task HandleUnsubscribeAsync(WebSocketClient client)
    {
        client.SubscribedRoomId = null;
        await SendMessageAsync(client, new WebSocketUnsubscribedResponse());
    }

    private async Task HandleAdminSubscribeAsync(WebSocketClient client, string messageJson)
    {
        try
        {
            _logger.LogInformation($"收到管理员订阅请求: {messageJson}");

            var request = JsonSerializer.Deserialize<WebSocketAdminSubscribeRequest>(messageJson, _jsonOptions);
            if (request == null || string.IsNullOrEmpty(request.Token))
            {
                _logger.LogWarning("管理员订阅失败: Token 为空");
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "unauthorized"));
                return;
            }

            // 验证管理员权限
            bool isValid = false;
            if (!string.IsNullOrEmpty(_adminToken) && request.Token == _adminToken)
            {
                isValid = true;
                _logger.LogInformation($"管理员验证成功 (永久Token)");
            }
            else if (!string.IsNullOrEmpty(_viewToken) && request.Token == _viewToken)
            {
                isValid = true;
                _logger.LogInformation($"管理员验证成功 (查询Token)");
            }
            else if (_otpService.ValidateTempToken(request.Token, null))
            {
                isValid = true;
                _logger.LogInformation($"管理员验证成功 (临时Token)");
            }
            else
            {
                _logger.LogWarning($"管理员验证失败: 提供的Token长度={request.Token?.Length}, 管理员Token长度={_adminToken?.Length}, 查询Token长度={_viewToken?.Length}");
            }

            if (!isValid)
            {
                _logger.LogWarning("管理员验证失败: 返回 unauthorized");
                await SendMessageAsync(client, new WebSocketErrorResponse(Message: "unauthorized"));
                return;
            }

            client.ClientType = WebSocketClientType.AdminSubscriber;
            client.SubscribedRoomId = null;

            _logger.LogInformation($"客户端 {client.Id} 订阅为管理员 (Token: {request.Token})");

            await SendMessageAsync(client, new WebSocketAdminSubscribedResponse());

            // 立即发送所有房间状态
            await SendAdminUpdateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling admin subscribe");
            await SendMessageAsync(client, new WebSocketErrorResponse(Message: "internal-error"));
        }
    }

    private async Task HandleAdminUnsubscribeAsync(WebSocketClient client)
    {
        client.ClientType = WebSocketClientType.RoomSubscriber;
        client.SubscribedRoomId = null;
        await SendMessageAsync(client, new WebSocketAdminUnsubscribedResponse());
    }

    private async Task MonitorHeartbeatAsync(WebSocketClient client)
    {
        try
        {
            while (!client.CancellationTokenSource.Token.IsCancellationRequested &&
                   client.WebSocket.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), client.CancellationTokenSource.Token);

                // 检查客户端是否超时（超过30秒没有活动）
                if (DateTime.UtcNow - client.LastPingTime > TimeSpan.FromSeconds(30))
                {
                    _logger.LogWarning($"WebSocket client {client.Id} timeout, disconnecting");
                    await client.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Timeout",
                        client.CancellationTokenSource.Token);
                    break;
                }

                // 发送 ping
                try
                {
                    await client.WebSocket.SendAsync(
                        Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
                        System.Net.WebSockets.WebSocketMessageType.Text,
                        true,
                        client.CancellationTokenSource.Token);
                }
                catch (WebSocketException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DisconnectClientAsync(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var client))
        {
            _logger.LogInformation($"WebSocket client disconnected: {clientId}");
            
            try
            {
                if (client.WebSocket.State == WebSocketState.Open)
                {
                    await client.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Disconnecting",
                        client.CancellationTokenSource.Token);
                }
            }
            catch { }

            client.CancellationTokenSource.Cancel();
            client.CancellationTokenSource.Dispose();
            client.WebSocket.Dispose();
        }
    }

    private async Task SendMessageAsync(WebSocketClient client, IWebSocketMessage message)
    {
        try
        {
            if (client.WebSocket.State != WebSocketState.Open)
            {
                _logger.LogWarning($"无法发送消息到客户端 {client.Id}: WebSocket 状态为 {client.WebSocket.State}");
                return;
            }

            var json = JsonSerializer.Serialize(message, _jsonOptions);
            _logger.LogInformation($"发送消息到客户端 {client.Id}: {json.Substring(0, Math.Min(200, json.Length))}...");

            var buffer = Encoding.UTF8.GetBytes(json);

            await client.WebSocket.SendAsync(
                new ArraySegment<byte>(buffer),
                System.Net.WebSockets.WebSocketMessageType.Text,
                true,
                client.CancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error sending WebSocket message to client {client.Id}");
        }
    }

    // --- 公共方法：供外部调用以推送更新 ---

    public async Task SendRoomUpdateAsync(string roomId)
    {
        var room = await _serverState.GetRoomAsync(roomId);
        if (room == null) return;

        var roomData = new WebSocketRoomUpdateData(
            RoomId: room.Id,
            State: room.State.ToString().ToLower(),
            Locked: room.IsLocked,
            Cycle: room.IsCycle,
            Live: room.IsLive,
            Chart: room.SelectedChartId.HasValue ? new WebSocketRoomChartInfo(
                Name: $"Chart-{room.SelectedChartId}",
                Id: room.SelectedChartId.Value
            ) : null,
            Host: new WebSocketRoomHostInfo(
                Id: room.Host.Id,
                Name: room.Host.Name
            ),
            Users: room.GetPlayers().Select(u => new WebSocketRoomUserInfo(
                Id: u.Id,
                Name: u.Name,
                IsReady: false // TODO: 获取实际准备状态
            )).ToList(),
            Monitors: new List<WebSocketRoomMonitorInfo>() // TODO: 获取观察者列表
        );

        var message = new WebSocketRoomUpdateMessage(Data: roomData);

        _logger.LogInformation($"发送房间更新: {roomId}, 订阅客户端数: {_clients.Values.Count(c => c.ClientType == WebSocketClientType.RoomSubscriber && c.SubscribedRoomId == roomId)}");

        foreach (var client in _clients.Values)
        {
            if (client.ClientType == WebSocketClientType.RoomSubscriber &&
                client.SubscribedRoomId == roomId)
            {
                await SendMessageAsync(client, message);
            }
        }
    }

    public async Task SendRoomLogAsync(string roomId, string message)
    {
        var logData = new WebSocketRoomLogData(
            Message: message,
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        );

        var logMessage = new WebSocketRoomLogMessage(Data: logData);

        foreach (var client in _clients.Values)
        {
            if (client.ClientType == WebSocketClientType.RoomSubscriber &&
                client.SubscribedRoomId == roomId)
            {
                await SendMessageAsync(client, logMessage);
            }
        }
    }

    public async Task SendAdminUpdateAsync()
    {
        var allRooms = _serverState.GetAllRooms().ToList();
        _logger.LogInformation($"开始发送管理员更新，总房间数={allRooms.Count}");

        var rooms = allRooms.Select(r => new WebSocketAdminRoomInfo(
            RoomId: r.Id,
            MaxUsers: r.MaxPlayerCount,
            CurrentUsers: r.GetPlayerCount(),
            CurrentMonitors: r.GetMonitorCount(),
            ReplayEligible: _serverState.ReplayRecordingEnabled,
            Live: r.IsLive,
            Locked: r.IsLocked,
            Cycle: r.IsCycle,
            Host: new WebSocketRoomHostInfo(
                Id: r.Host.Id,
                Name: r.Host.Name
            ),
            State: r.State == Common.RoomState.Playing ? new WebSocketAdminRoomState(
                Type: "playing",
                ReadyUsers: new List<long>(),
                ReadyCount: 0,
                ResultsCount: r.GetFinishedCount(),
                AbortedCount: r.GetAbortedCount(),
                FinishedUsers: r.GetFinishedUserIds(),
                AbortedUsers: r.GetAbortedUserIds()
            ) : new WebSocketAdminRoomState(
                Type: r.State.ToString().ToLower(),
                ReadyUsers: new List<long>(),
                ReadyCount: 0,
                ResultsCount: 0,
                AbortedCount: 0,
                FinishedUsers: new List<long>(),
                AbortedUsers: new List<long>()
            ),
            Chart: r.SelectedChartId.HasValue ? new WebSocketRoomChartInfo(
                Name: $"Chart-{r.SelectedChartId}",
                Id: r.SelectedChartId.Value
            ) : null,
            Contest: new WebSocketAdminContestInfo(
                WhitelistCount: r.IsContestMode ? r.ContestWhitelist.Count : 0,
                Whitelist: r.ContestWhitelist.ToList(),
                ManualStart: true,
                AutoDisband: true
            ),
            Users: r.GetPlayers().Select(u => new WebSocketAdminUserInfo(
                Id: u.Id,
                Name: u.Name,
                Connected: u.IsConnected,
                IsHost: r.IsHost(u),
                GameTime: u.GameTime,
                Language: null,
                Finished: r.IsPlayerFinished(u.Id),
                Aborted: r.IsPlayerAborted(u.Id),
                RecordId: r.GetPlayerRecordId(u.Id)
            )).ToList(),
            Monitors: new List<WebSocketAdminUserInfo>() // TODO: 获取观察者详细信息
        )).ToList();

        var changes = new WebSocketAdminChangesData(
            Rooms: rooms,
            TotalRooms: rooms.Count
        );

        var updateData = new WebSocketAdminUpdateData(
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Changes: changes
        );

        var message = new WebSocketAdminUpdateMessage(Data: updateData);

        var adminClients = _clients.Values.Where(c => c.ClientType == WebSocketClientType.AdminSubscriber).ToList();
        _logger.LogInformation($"发送管理员更新: 房间数={rooms.Count}, 管理员订阅客户端数={adminClients.Count}");

        foreach (var client in adminClients)
        {
            _logger.LogInformation($"发送消息到客户端 {client.Id}, 状态={client.WebSocket.State}");
            await SendMessageAsync(client, message);
            _logger.LogInformation($"消息已发送到客户端 {client.Id}");
        }
    }

    public int GetClientCount() => _clients.Count;
    public int GetRoomSubscriberCount() => _clients.Values.Count(c => c.ClientType == WebSocketClientType.RoomSubscriber);
    public int GetAdminSubscriberCount() => _clients.Values.Count(c => c.ClientType == WebSocketClientType.AdminSubscriber);
}