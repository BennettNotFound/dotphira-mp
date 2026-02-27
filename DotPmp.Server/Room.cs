using System.Text.Json;
using DotPmp.Common;

namespace DotPmp.Server;

public class Room
{
        public int MaxPlayerCount { get; set; } = 32678;
    private readonly List<User> _users = new();
    private readonly List<User> _monitors = new();
    private readonly HashSet<int> _readyUsers = new();
    private readonly Dictionary<int, int> _playResults = new();
    private readonly Dictionary<int, int> _playRecordIds = new();
    private readonly HashSet<int> _abortedUsers = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ServerState _serverState;
    private readonly WebSocketService? _webSocketService;

    public string Id { get; }
    public User Host { get; private set; }
    public RoomState State { get; private set; } = RoomState.SelectChart;
    public bool IsLive { get; private set; }
    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            _isLocked = value;
            if (value) IsRecruiting = false;
        }
    }
    public bool IsCycle { get; set; }
    public bool IsRecruiting { get; set; } = true;
    public int? SelectedChartId { get; set; }

    // --- Contest Mode Properties ---
    public bool IsContestMode { get; set; }
    public HashSet<long> ContestWhitelist { get; } = new();
    // --- End Contest Mode Properties ---

    public Room(string id, User host, ServerState serverState, WebSocketService? webSocketService = null)
    {
        Id = id;
        Host = host;
        _serverState = serverState;
        _webSocketService = webSocketService;
        _users.Add(host);
    }

    public bool IsHost(User user) => Host.Id == user.Id;

    public async Task<bool> AddUserAsync(User user, bool isMonitor)
    {
        await _lock.WaitAsync();
        try
        {
            if (isMonitor)
            {
                _monitors.Add(user);
                IsLive = true;
                return true;
            }
            else
            {
                // 比赛模式下的白名单检查
                if (IsContestMode && !ContestWhitelist.Contains(user.Id))
                {
                    return false;
                }

                if (_users.Count >= MaxPlayerCount)
                    return false;

                _users.Add(user);
                return true;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnUserLeaveAsync(User user)
    {
        await _lock.WaitAsync();
        try
        {
            await SendMessageAsync(new Message.LeaveRoom(user.Id, user.Name));

            _users.Remove(user);
            _monitors.Remove(user);
            user.Room = null;

            if (IsHost(user))
            {
                if (_users.Count == 0)
                {
                    // 房间应该被删除
                    _serverState.RemoveRoom(Id);
                    return;
                }

                // 选择新的房主
                Host = _users[0];
                await SendMessageAsync(new Message.NewHost(Host.Id));
                await Host.SendAsync(new ServerCommand.ChangeHost(true));
            }

            await CheckAllReadyAsync();

            // WebSocket 通知
            _ = Task.Run(async () => {
                if (_webSocketService != null)
                {
                    await _webSocketService.SendRoomLogAsync(Id, $"{user.Name} 离开了房间");
                    await _webSocketService.SendRoomUpdateAsync(Id);
                }
            });
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public void RemoveUserSilently(User user)
    {
        _users.Remove(user);
        _monitors.Remove(user);
        user.Room = null;
        
        if (IsHost(user) && _users.Count > 0)
        {
            Host = _users[0];
        }
    }


    public List<User> GetAllUsers()
    {
        return _users.Concat(_monitors).ToList();
    }

    // HTTP API 方法
    public int GetPlayerCount() => _users.Count;
    public int GetMonitorCount() => _monitors.Count;
    public List<User> GetPlayers() => _users.ToList();
    
    // --- Getters for Admin API ---
    public bool IsPlayerFinished(int userId) => _playResults.ContainsKey(userId);
    public bool IsPlayerAborted(int userId) => _abortedUsers.Contains(userId);
    public int? GetPlayerRecordId(int userId) => _playRecordIds.TryGetValue(userId, out var id) ? id : null;
    public int GetFinishedCount() => _playResults.Count;
    public int GetAbortedCount() => _abortedUsers.Count;
    public List<long> GetFinishedUserIds() => _playResults.Keys.Select(k => (long)k).ToList();
    public List<long> GetAbortedUserIds() => _abortedUsers.Select(k => (long)k).ToList();
    
    public bool IsUserReady(User user) => _readyUsers.Contains(user.Id);
    public List<User> GetMonitors() => _monitors.ToList();
    // --- End Getters for Admin API ---

    public ClientRoomState GetClientState(User user)
    {
        var users = GetAllUsers().ToDictionary(u => u.Id, u => u.ToInfo());

        return new ClientRoomState(
            Id,
            State,
            IsLive,
            IsLocked,
            IsCycle,
            IsHost(user),
            _readyUsers.Contains(user.Id),
            users,
            SelectedChartId
        );
    }

    public async Task SendMessageAsync(Message message)
    {
        await BroadcastAsync(new ServerCommand.Message(message));
    }

    public async Task BroadcastAsync(ServerCommand command)
    {
        var tasks = GetAllUsers().Select(u => u.SendAsync(command));
        await Task.WhenAll(tasks);
    }

    public async Task BroadcastToMonitorsAsync(ServerCommand command)
    {
        var tasks = _monitors.Select(u => u.SendAsync(command));
        await Task.WhenAll(tasks);
    }

    public async Task OnStateChangeAsync()
    {
        await BroadcastAsync(new ServerCommand.ChangeState(State, SelectedChartId));

        // WebSocket 通知
        _ = Task.Run(async () => {
            if (_webSocketService != null)
            {
                await _webSocketService.SendRoomUpdateAsync(Id);
            }
        });
    }

    public async Task StartGameAsync(User user)
    {
        await _lock.WaitAsync();
        try
        {
            State = RoomState.WaitingForReady;
            _readyUsers.Clear();
            _readyUsers.Add(user.Id);

            await SendMessageAsync(new Message.GameStart(user.Id));
            await OnStateChangeAsync();
            await CheckAllReadyAsync();

            // WebSocket 通知
            _ = Task.Run(async () => {
                if (_webSocketService != null)
                {
                    await _webSocketService.SendRoomLogAsync(Id, $"{user.Name} 开始了游戏");
                    await _webSocketService.SendRoomUpdateAsync(Id);
                }
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnUserReadyAsync(User user)
    {
        await _lock.WaitAsync();
        try
        {
            if (State != RoomState.WaitingForReady)
                return;

            _readyUsers.Add(user.Id);
            await SendMessageAsync(new Message.Ready(user.Id));
            await CheckAllReadyAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnUserCancelReadyAsync(User user)
    {
        await _lock.WaitAsync();
        try
        {
            if (State != RoomState.WaitingForReady)
                return;

            _readyUsers.Remove(user.Id);

            if (IsHost(user))
            {
                // 房主取消，游戏取消
                State = RoomState.SelectChart;
                _readyUsers.Clear();
                await SendMessageAsync(new Message.CancelGame(user.Id));
                await OnStateChangeAsync();
            }
            else
            {
                await SendMessageAsync(new Message.CancelReady(user.Id));
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnUserPlayedAsync(User user, int recordId, int score, float accuracy, bool fullCombo)
    {
        await _lock.WaitAsync();
        try
        {
            if (State != RoomState.Playing)
                return;

            _playRecordIds[user.Id] = recordId;
            _playResults[user.Id] = score;

            if (user.CurrentReplay != null)
            {
                await user.CurrentReplay.UpdateRecordIdAsync(recordId);
            }

            await SendMessageAsync(new Message.Played(user.Id, score, accuracy, fullCombo));
            await CheckAllReadyAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OnUserAbortAsync(User user)
    {
        await _lock.WaitAsync();
        try
        {
            if (State != RoomState.Playing)
                return;

            _abortedUsers.Add(user.Id);
            await SendMessageAsync(new Message.Abort(user.Id));
            await CheckAllReadyAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StartGameManuallyAsync(bool force)
    {
        await _lock.WaitAsync();
        try
        {
            if (State != RoomState.WaitingForReady)
                throw new InvalidOperationException("Room is not in waiting state");

            if (!force && !GetAllUsers().All(u => _readyUsers.Contains(u.Id)))
                throw new InvalidOperationException("Not all users are ready");

            State = RoomState.Playing;
            _playResults.Clear();
            _playRecordIds.Clear();
            _abortedUsers.Clear();
            
            await StartReplayRecordingAsync();

            await SendMessageAsync(new Message.StartPlaying());
            await OnStateChangeAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StartReplayRecordingAsync()
    {
        if (!_serverState.ReplayRecordingEnabled || SelectedChartId == null) return;

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var user in _users)
        {
            var fileName = $"{timestamp}.phirarec";
            var filePath = Path.Combine(AppContext.BaseDirectory, "record", user.Id.ToString(), SelectedChartId.Value.ToString(), fileName);
            user.CurrentReplay = new ReplayWriter(filePath, SelectedChartId.Value, user.Id);
        }
    }

    private async Task StopReplayRecordingAsync()
    {
        foreach (var user in _users)
        {
            if (user.CurrentReplay != null)
            {
                await user.CurrentReplay.DisposeAsync();
                user.CurrentReplay = null;
            }
        }
    }

    private async Task CheckAllReadyAsync()
    {
        var allUsers = GetAllUsers();

        if (State == RoomState.WaitingForReady)
        {
            // 在比赛模式下，我们不自动开始游戏
            if (IsContestMode)
                return;

            if (allUsers.All(u => _readyUsers.Contains(u.Id)))
            {
                // 所有人准备好，开始游戏
                State = RoomState.Playing;
                _playResults.Clear();
                _playRecordIds.Clear();
                _abortedUsers.Clear();

                await StartReplayRecordingAsync();

                await SendMessageAsync(new Message.StartPlaying());
                await OnStateChangeAsync();
            }
        }
        else if (State == RoomState.Playing)
        {
            var players = _users; // 只有玩家需要完成游戏
            if (players.All(u => _playResults.ContainsKey(u.Id) || _abortedUsers.Contains(u.Id)))
            {
                // 所有人完成游戏
                await StopReplayRecordingAsync();
                await SendMessageAsync(new Message.GameEnd());

                // 比赛模式逻辑：在结算后输出日志并解散房间
                if (IsContestMode)
                {
                    Console.WriteLine($"[CONTEST] Room {Id} finished. Chart: {SelectedChartId}. Results: {JsonSerializer.Serialize(_playResults)}");
                    
                    // 必须先解除锁定，因为 DisbandRoomAsync 也会尝试获取锁(通过 OnConnectionLostAsync)
                    // 这里的实现假设 DisbandRoomAsync 会优雅处理。
                    // 为了简单起见，我们直接调用 ServerState 里的 Disband
                    _ = Task.Run(() => _serverState.DisbandRoomAsync(Id,"比赛已结束"));
                    return;
                }

                State = RoomState.SelectChart;
                _readyUsers.Clear();

                // 如果开启循环模式，切换房主
                if (IsCycle && _users.Count > 1)
                {
                    var currentIndex = _users.IndexOf(Host);
                    var nextIndex = (currentIndex + 1) % _users.Count;
                    var oldHost = Host;
                    Host = _users[nextIndex];

                    await SendMessageAsync(new Message.NewHost(Host.Id));
                    await oldHost.SendAsync(new ServerCommand.ChangeHost(false));
                    await Host.SendAsync(new ServerCommand.ChangeHost(true));
                }

                await OnStateChangeAsync();
            }
        }
    }
}
