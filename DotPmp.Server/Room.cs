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
    public bool IsLive { get; private set; } = true;
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
    public string? SelectedChartName { get; set; }
    public bool ReplayRecordingAllowed { get; private set; }

    // --- Contest Mode Properties ---
    public bool IsContestMode { get; set; }
    public HashSet<long> ContestWhitelist { get; } = new();
    // --- End Contest Mode Properties ---
    
    public Room(string id, User host, ServerState serverState, bool replayRecordingAllowed, WebSocketService? webSocketService = null)
    {
        Id = id;
        Host = host;
        _serverState = serverState;
        ReplayRecordingAllowed = replayRecordingAllowed;
        _webSocketService = webSocketService;

        _users.Add(host);

        IsLive = true;
    }

    public bool IsHost(User user) => Host.Id == user.Id;

    public async Task<bool> AddUserAsync(User user, bool isMonitor)
    {
        await _lock.WaitAsync();
        try
        {
            //IsLive = true;
            if (isMonitor)
            {
                _monitors.Add(user);
                //IsLive = true;
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
    
    private static readonly UserInfo FakeMonitorInfo = new(
        20419,
        "L",
        true
    );
    public List<UserInfo> GetClientUserInfosWithFakeMonitor()
    {
        var users = GetAllUsers()
            .Select(u => u.ToInfo())
            .ToList();

        if (!users.Any(u => u.Id == FakeMonitorInfo.Id))
        {
            users.Add(FakeMonitorInfo);
        }

        return users;
    }

    // HTTP API 方法
    public int GetPlayerCount() => _users.Count;
    private int _fakeMonitorCount = 0;
    public int GetMonitorCount() => _monitors.Count + _fakeMonitorCount;
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
        var users = GetClientUserInfosWithFakeMonitor()
            .ToDictionary(u => u.Id, u => u);

        return new ClientRoomState(
            Id,
            State,
            true,
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

            if (!force && !_users.All(u => _readyUsers.Contains(u.Id)))
                throw new InvalidOperationException("Not all players are ready");

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
        if (!ReplayRecordingAllowed)
        {
            Console.WriteLine($"[Replay] Skipped for room {Id}: replay recording disabled for this room");
            return;
        }

        if (SelectedChartId == null)
        {
            Console.WriteLine($"[Replay] Skipped for room {Id}: no selected chart");
            return;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var user in _users)
        {
            var fileName = $"{timestamp}.phirarec";
            var filePath = Path.Combine(_serverState.GetReplayRootPath(), user.Id.ToString(), SelectedChartId.Value.ToString(), fileName);
            user.CurrentReplay = new ReplayWriter(
                filePath,
                SelectedChartId.Value,
                SelectedChartName ?? $"Chart-{SelectedChartId.Value}",
                user.Id,
                user.Name);
            Console.WriteLine($"[Replay] Started for room {Id}, user {user.Id}, chart {SelectedChartId.Value}: {filePath}");
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

    public async Task DisableReplayRecordingAsync()
    {
        ReplayRecordingAllowed = false;
        Console.WriteLine($"[Replay] Disabled for room {Id}");
        await StopReplayRecordingAsync();
    }

    private async Task<List<(User User, string ReplayPath)>> StopReplayRecordingAndCollectAsync()
    {
        var completedReplays = new List<(User User, string ReplayPath)>();

        foreach (var user in _users)
        {
            if (user.CurrentReplay == null)
                continue;

            var replayPath = user.CurrentReplay.FilePath;
            var recordId = user.CurrentReplay.RecordId;
            await user.CurrentReplay.DisposeAsync();
            user.CurrentReplay = null;
            completedReplays.Add((user, replayPath));
            Console.WriteLine($"[Replay] Saved for room {Id}, user {user.Id}: {replayPath} (recordId={recordId})");
        }

        return completedReplays;
    }

    private void ScheduleAutoUploads(IEnumerable<(User User, string ReplayPath)> completedReplays)
    {
        foreach (var (user, replayPath) in completedReplays)
        {
            var config = _serverState.GetReplayAutoUploadConfig(user.Id);
            if (!config.Enabled)
                continue;

            _serverState.ScheduleReplayAutoUpload(replayPath, user.Id, config.Show);
        }
    }

    private async Task CheckAllReadyAsync()
    {
        if (State == RoomState.WaitingForReady)
        {
            // 比赛模式下不自动开始
            if (IsContestMode)
                return;

            // 只判断玩家，不判断 monitor
            var players = _users;

            if (players.Count > 0 && players.All(u => _readyUsers.Contains(u.Id)))
            {
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
            // 只有玩家需要完成游戏，monitor 不参与结算
            var players = _users;

            if (players.All(u => _playResults.ContainsKey(u.Id) || _abortedUsers.Contains(u.Id)))
            {
                var completedReplays = await StopReplayRecordingAndCollectAsync();
                await SendMessageAsync(new Message.GameEnd());
                ScheduleAutoUploads(completedReplays);

                // 比赛模式：结算后解散房间
                if (IsContestMode)
                {
                    Console.WriteLine(
                        $"[CONTEST] Room {Id} finished. Chart: {SelectedChartId}. Results: {JsonSerializer.Serialize(_playResults)}"
                    );

                    _ = Task.Run(() => _serverState.DisbandRoomAsync(Id, "比赛已结束"));
                    return;
                }

                State = RoomState.SelectChart;
                _readyUsers.Clear();

                // 循环模式：切换房主
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
    public async Task MarkLiveAsync()
    {
        if (_webSocketService != null)
        {
            await _webSocketService.SendRoomUpdateAsync(Id);
        }
    }
    private User? _fakeMonitor;

    public async Task AddFakeMonitorAsync()
    {
        await _lock.WaitAsync();

        User fakeMonitor;

        try
        {
            if (_fakeMonitor != null)
                return;

            fakeMonitor = new User(20419, "L")
            {
                Room = this,
                IsMonitor = true
            };

            _fakeMonitor = fakeMonitor;
            //_monitors.Add(fakeMonitor);

            IsLive = true;
        }
        finally
        {
            _lock.Release();
        }

        // 关键：通知真实客户端房间里来了一个 monitor
        await BroadcastAsync(new ServerCommand.OnJoinRoom(fakeMonitor.ToInfo()));

        // 可选：让客户端聊天/事件流也收到加入提示
        await SendMessageAsync(new Message.JoinRoom(fakeMonitor.Id, fakeMonitor.Name));

        if (_webSocketService != null)
        {
            await _webSocketService.SendRoomUpdateAsync(Id);
        }

        Console.WriteLine($"[Monitor] Fake monitor joined room {Id}: {fakeMonitor.Id} {fakeMonitor.Name}");
    }

    public async Task RemoveFakeMonitorAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _fakeMonitorCount = 0;

            if (_webSocketService != null)
            {
                await _webSocketService.SendRoomUpdateAsync(Id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
    
    public User AddFakeMonitorIfMissing()
    {
        var existing = _monitors.FirstOrDefault(u => u.Id == 0);
        if (existing != null)
            return existing;

        var fake = new User(20419, "L")
        {
            Room = this,
            IsMonitor = true
        };

        _monitors.Add(fake);
        IsLive = true;

        return fake;
    }
    

}
