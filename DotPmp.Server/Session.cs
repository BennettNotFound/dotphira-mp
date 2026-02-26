using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using DotPmp.Common;

namespace DotPmp.Server;

public class Session
{
    private const string PhiraHost = "https://phira.5wyxi.com";
    private static readonly HttpClient HttpClient = new();

    private readonly Guid _id;
    private readonly NetworkStream<ServerCommand, ClientCommand> _stream;
    private readonly ServerState _server;
    private readonly CancellationTokenSource _heartbeatCts;
    private readonly string _welcomeMessage;
    private User? _user;
    private string? _token;

    public Guid Id => _id;
    public User? User => _user;
    public byte Version => _stream.Version;

    public Session(Guid id, TcpClient client, ServerState server, string welcomeMessage = "[L]欢迎来到L的联机服务器!!!")
    {
        _id = id;
        _server = server;
        _welcomeMessage = welcomeMessage;
        _heartbeatCts = new CancellationTokenSource();

        _stream = new NetworkStream<ServerCommand, ClientCommand>(
            client,
            null,
            MessageSerializer.SerializeServerCommand,
            MessageSerializer.DeserializeClientCommand,
            HandleMessageAsync
        );

        _ = Task.Run(HeartbeatMonitor);
    }

    private async Task HeartbeatMonitor()
    {
        try
        {
            while (!_heartbeatCts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _heartbeatCts.Token);

                var lastReceive = _stream.GetLastReceiveTime();
                if (DateTime.UtcNow - lastReceive > TimeSpan.FromSeconds(10))
                {
                    Console.WriteLine($"Session {_id} timeout");
                    await _server.OnConnectionLostAsync(_id);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleMessageAsync(ClientCommand command)
    {
        try
        {
            ServerCommand? response = command switch
            {
                ClientCommand.Ping => new ServerCommand.Pong(),
                ClientCommand.Authenticate auth => await HandleAuthenticateAsync(auth),
                _ when _user == null => null,
                ClientCommand.Chat chat => await HandleChatAsync(chat),
                ClientCommand.Touches touches => await HandleTouchesAsync(touches),
                ClientCommand.Judges judges => await HandleJudgesAsync(judges),
                ClientCommand.CreateRoom create => await HandleCreateRoomAsync(create),
                ClientCommand.JoinRoom join => await HandleJoinRoomAsync(join),
                ClientCommand.LeaveRoom => await HandleLeaveRoomAsync(),
                ClientCommand.LockRoom lockRoom => await HandleLockRoomAsync(lockRoom),
                ClientCommand.CycleRoom cycleRoom => await HandleCycleRoomAsync(cycleRoom),
                ClientCommand.SelectChart select => await HandleSelectChartAsync(select),
                ClientCommand.RequestStart => await HandleRequestStartAsync(),
                ClientCommand.Ready => await HandleReadyAsync(),
                ClientCommand.CancelReady => await HandleCancelReadyAsync(),
                ClientCommand.Played played => await HandlePlayedAsync(played),
                ClientCommand.Abort => await HandleAbortAsync(),
                _ => null
            };

            if (response != null)
            {
                await SendAsync(response);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling message: {ex.Message}");
        }
    }

    // Phira API 响应类型
    private record PhiraUserInfo(int Id, string Name, string Language);
    private record PhiraRecord(int Id, int Player, int Score, float Accuracy, bool FullCombo);
    private record PhiraChart(int Id, string Name);

    private async Task<ServerCommand> HandleAuthenticateAsync(ClientCommand.Authenticate auth)
    {
        try
        {
            _token = auth.Token;

            // 调用 phira API 获取用户信息
            var request = new HttpRequestMessage(HttpMethod.Get, $"{PhiraHost}/me");
            request.Headers.Add("Authorization", $"Bearer {auth.Token}");

            var response = await HttpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var phiraUser = await response.Content.ReadFromJsonAsync<PhiraUserInfo>();
            if (phiraUser == null)
                throw new Exception("Failed to get user info");

            Console.WriteLine($"User authenticated: {phiraUser.Id} - {phiraUser.Name}");

            var user = await _server.GetOrCreateUserAsync(phiraUser.Id, phiraUser.Name);
            _user = user;
            user.Session = this;

            var roomState = user.Room?.GetClientState(user);

            // 2. 开一个后台任务去发欢迎消息，不阻塞当前方法的返回
            if (user.Id != 1739989)
            {
                _ = Task.Run(async () => 
                {
                    try
                    {
                        // 稍微延迟一下（比如 200~500 毫秒），确保外层框架已经把 Auth 消息发给了客户端，
                        // 且客户端已经处理完毕切入了“已登录”状态。
                        await Task.Delay(300); 

                        await user.SendAsync(new ServerCommand.Message(new Message.Chat(0, _welcomeMessage)));
                        await user.SendAsync(new ServerCommand.Message(new Message.Chat(0, "服务器当前为开发阶段,可能有很多小毛病")));
                        await user.SendAsync(new ServerCommand.Message(new Message.Chat(0, "欢迎反馈到腐竹: 2165217440")));
                        await user.SendAsync(new ServerCommand.Message(new Message.Chat(0, "交流群: ?")));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to send welcome message: {ex.Message}");
                    }
                });
            }

            
            return new ServerCommand.Authenticate(
                Result<(UserInfo, ClientRoomState?)>.Success((user.ToInfo(), roomState))
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Authentication failed: {ex.Message}");
            return new ServerCommand.Authenticate(
                Result<(UserInfo, ClientRoomState?)>.Failure(ex.Message)
            );
        }
    }

    private async Task<ServerCommand> HandleChatAsync(ClientCommand.Chat chat)
    {
        if (_user?.Room == null)
            return new ServerCommand.Chat(Result<object?>.Failure("Not in a room"));

        await _user.Room.SendMessageAsync(new Message.Chat(_user.Id, chat.Message));
        return new ServerCommand.Chat(Result<object?>.Success(null));
    }

    private async Task<ServerCommand?> HandleTouchesAsync(ClientCommand.Touches touches)
    {
        if (_user?.Room == null || !_user.Room.IsLive)
            return null;

        // 更新用户游戏时间
        if (touches.Frames.Count > 0)
            _user.GameTime = touches.Frames[^1].Time;

        // 写入回放
        if (_user.CurrentReplay != null)
        {
            await _user.CurrentReplay.WriteTouchesAsync(touches.Frames);
        }

        // 广播给所有观察者
        await _user.Room.BroadcastToMonitorsAsync(new ServerCommand.Touches(_user.Id, touches.Frames));
        return null;
    }

    private async Task<ServerCommand?> HandleJudgesAsync(ClientCommand.Judges judges)
    {
        if (_user?.Room == null || !_user.Room.IsLive)
            return null;

        // 写入回放
        if (_user.CurrentReplay != null)
        {
            await _user.CurrentReplay.WriteJudgesAsync(judges.JudgeEvents);
        }

        // 广播给所有观察者
        await _user.Room.BroadcastToMonitorsAsync(new ServerCommand.Judges(_user.Id, judges.JudgeEvents));
        return null;
    }

    public string RoomId = "";
    public bool IsUseRandomRoomId = false;
    private async Task<ServerCommand> HandleCreateRoomAsync(ClientCommand.CreateRoom create)
    {
        try
        {
            if (!_server.RoomCreationEnabled)
            {
                return new ServerCommand.CreateRoom(Result<object?>.Failure("Room creation is currently disabled by the administrator."));
            }

            if (_user == null)
                return new ServerCommand.CreateRoom(Result<object?>.Failure("Not authenticated"));

            if (_user.Room != null)
                return new ServerCommand.CreateRoom(Result<object?>.Failure("Already in a room"));

            if (create.RoomId == "0")
            {
                IsUseRandomRoomId  = true;
                int code = RandomNumberGenerator.GetInt32(100000, 1000000);
                RoomId =  code.ToString();
            }
            else
            {
                RoomId = create.RoomId;
            }

            var room = await _server.CreateRoomAsync(RoomId, _user);
            _user.Room = room;

            await room.SendMessageAsync(new Message.CreateRoom(_user.Id));
            // 发送欢迎消息
            //await room.SendMessageAsync(new Message.Chat(0, _welcomeMessage));
            await room.SendMessageAsync(new Message.Chat(0,$"[L]当前房间ID: {RoomId}"));

            // WebSocket 通知
            _ = Task.Run(async () => {
                var wsService = _server.GetWebSocketService();
                if (wsService != null)
                {
                    await wsService.SendRoomLogAsync(RoomId, $"{_user.Name} 创建了房间");
                }
            });

            return new ServerCommand.CreateRoom(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.CreateRoom(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleJoinRoomAsync(ClientCommand.JoinRoom join)
    {
        try
        {
            if (_user == null)
                return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("Not authenticated"));

            if (_user.Room != null)
                return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("Already in a room"));

            Room? room;
            string joinedRoomId;

            // 房间ID为0时随机加入招募中的房间
            if (join.RoomId == "0")
            {
                room = _server.GetRandomRecruitingRoom();
                if (room == null)
                    return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("没有可加入的房间，请创建新房间"));
            }
            else
            {
                room = await _server.GetRoomAsync(join.RoomId);
                if (room == null)
                    return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("Room not found"));
            }

            joinedRoomId = room.Id;

            if (room.IsLocked)
                return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("Room is locked"));

            if (_server.IsBannedFromRoom(room.Id, _user.Id))
                return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("You are banned from this room."));

            if (!await room.AddUserAsync(_user, join.Monitor))
                return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure("Room is full"));

            _user.Room = room;
            _user.IsMonitor = join.Monitor;

            await room.BroadcastAsync(new ServerCommand.OnJoinRoom(_user.ToInfo()));
            await room.SendMessageAsync(new Message.JoinRoom(_user.Id, _user.Name));
            if (join.RoomId == "0")
            {
                await room.SendMessageAsync(new Message.Chat(0,$"[L]当前房间ID: {room.Id}"));
            }

            var response = new JoinRoomResponse(
                room.State,
                room.GetAllUsers().Select(u => u.ToInfo()).ToList(),
                room.IsLive
            );

            // WebSocket 通知
            _ = Task.Run(async () => {
                var wsService = _server.GetWebSocketService();
                if (wsService != null)
                {
                    await wsService.SendRoomLogAsync(joinedRoomId, $"{_user.Name} 加入了房间");
                    await wsService.SendRoomUpdateAsync(joinedRoomId);
                }
            });

            return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Success(response));
        }
        catch (Exception ex)
        {
            return new ServerCommand.JoinRoom(Result<JoinRoomResponse>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleLeaveRoomAsync()
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.LeaveRoom(Result<object?>.Failure("Not in a room"));

            var room = _user.Room;
            await room.OnUserLeaveAsync(_user);

            return new ServerCommand.LeaveRoom(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.LeaveRoom(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleLockRoomAsync(ClientCommand.LockRoom lockRoom)
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.LockRoom(Result<object?>.Failure("Not in a room"));

            if (!_user.Room.IsHost(_user))
                return new ServerCommand.LockRoom(Result<object?>.Failure("Only host can lock room"));

            _user.Room.IsLocked = lockRoom.Lock;
            await _user.Room.SendMessageAsync(new Message.LockRoom(lockRoom.Lock));

            return new ServerCommand.LockRoom(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.LockRoom(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleCycleRoomAsync(ClientCommand.CycleRoom cycleRoom)
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.CycleRoom(Result<object?>.Failure("Not in a room"));

            if (!_user.Room.IsHost(_user))
                return new ServerCommand.CycleRoom(Result<object?>.Failure("Only host can set cycle"));

            _user.Room.IsCycle = cycleRoom.Cycle;
            await _user.Room.SendMessageAsync(new Message.CycleRoom(cycleRoom.Cycle));

            return new ServerCommand.CycleRoom(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.CycleRoom(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleSelectChartAsync(ClientCommand.SelectChart select)
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.SelectChart(Result<object?>.Failure("Not in a room"));

            if (!_user.Room.IsHost(_user))
                return new ServerCommand.SelectChart(Result<object?>.Failure("Only host can select chart"));

            if (_user.Room.State != RoomState.SelectChart)
                return new ServerCommand.SelectChart(Result<object?>.Failure("Invalid state"));

            // 从 phira API 获取谱面信息
            string chartName = $"Chart{select.ChartId}";
            try
            {
                var chart = await HttpClient.GetFromJsonAsync<PhiraChart>($"{PhiraHost}/chart/{select.ChartId}");
                if (chart != null)
                    chartName = chart.Name;
            }
            catch
            {
                // 获取失败时使用默认名称
            }

            _user.Room.SelectedChartId = select.ChartId;
            await _user.Room.SendMessageAsync(new Message.SelectChart(_user.Id, chartName, select.ChartId));
            await _user.Room.OnStateChangeAsync();

            return new ServerCommand.SelectChart(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.SelectChart(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleRequestStartAsync()
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.RequestStart(Result<object?>.Failure("Not in a room"));

            if (!_user.Room.IsHost(_user))
                return new ServerCommand.RequestStart(Result<object?>.Failure("Only host can start game"));

            if (_user.Room.SelectedChartId == null)
                return new ServerCommand.RequestStart(Result<object?>.Failure("No chart selected"));

            await _user.Room.StartGameAsync(_user);

            return new ServerCommand.RequestStart(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.RequestStart(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleReadyAsync()
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.Ready(Result<object?>.Failure("Not in a room"));

            await _user.Room.OnUserReadyAsync(_user);

            return new ServerCommand.Ready(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.Ready(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleCancelReadyAsync()
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.CancelReady(Result<object?>.Failure("Not in a room"));

            await _user.Room.OnUserCancelReadyAsync(_user);

            return new ServerCommand.CancelReady(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.CancelReady(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandlePlayedAsync(ClientCommand.Played played)
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.Played(Result<object?>.Failure("Not in a room"));

            // 从 phira API 获取成绩记录
            var record = await HttpClient.GetFromJsonAsync<PhiraRecord>($"{PhiraHost}/record/{played.RecordId}");
            if (record == null)
                return new ServerCommand.Played(Result<object?>.Failure("Record not found"));

            if (record.Player != _user.Id)
                return new ServerCommand.Played(Result<object?>.Failure("Invalid record"));

            Console.WriteLine($"User {_user.Id} played: score={record.Score}, acc={record.Accuracy}, fc={record.FullCombo}");

            await _user.Room.OnUserPlayedAsync(_user, record.Id, record.Score, record.Accuracy, record.FullCombo);

            return new ServerCommand.Played(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HandlePlayedAsync error: {ex.Message}");
            return new ServerCommand.Played(Result<object?>.Failure(ex.Message));
        }
    }

    private async Task<ServerCommand> HandleAbortAsync()
    {
        try
        {
            if (_user?.Room == null)
                return new ServerCommand.Abort(Result<object?>.Failure("Not in a room"));

            await _user.Room.OnUserAbortAsync(_user);

            return new ServerCommand.Abort(Result<object?>.Success(null));
        }
        catch (Exception ex)
        {
            return new ServerCommand.Abort(Result<object?>.Failure(ex.Message));
        }
    }

    public async Task SendAsync(ServerCommand command)
    {
        await _stream.SendAsync(command);
    }

    public async Task CloseAsync()
    {
        _heartbeatCts.Cancel();
        await _stream.CloseAsync();
    }
}
