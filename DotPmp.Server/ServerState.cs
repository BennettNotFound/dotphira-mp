using System.Collections.Concurrent;
using DotPmp.Common;

namespace DotPmp.Server;

public class ServerState
{
    // 功能开关
    public bool ReplayRecordingEnabled { get; set; } = false;
    public bool RoomCreationEnabled { get; set; } = true;

    private readonly AdminDataService _adminDataService;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<int, User> _users = new();
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private WebSocketService? _webSocketService;

    public ServerState(AdminDataService adminDataService)
    {
        _adminDataService = adminDataService;
        // 注册系统用户 "L"
        _users[0] = new User(0, "L");
    }

    public void SetWebSocketService(WebSocketService webSocketService)
    {
        _webSocketService = webSocketService;
    }

    public bool IsUserBanned(long userId) => _adminDataService.IsBanned(userId);
    public async Task BanUserAsync(long userId) => await _adminDataService.BanUserAsync(userId);
    public async Task UnbanUserAsync(long userId) => await _adminDataService.UnbanUserAsync(userId);
    
    public bool IsBannedFromRoom(string roomId, long userId) => _adminDataService.IsBannedFromRoom(roomId, userId);
    public async Task BanFromRoomAsync(string roomId, long userId) => await _adminDataService.BanFromRoomAsync(roomId, userId);
    public async Task UnbanFromRoomAsync(string roomId, long userId) => await _adminDataService.UnbanFromRoomAsync(roomId, userId);

    public User? GetUser(long userId) => _users.TryGetValue((int)userId, out var user) ? user : null;

    public async Task<User> GetOrCreateUserAsync(int userId, string userName)
    {
        if (IsUserBanned(userId)) throw new Exception("此账号已被封禁");
        return _users.GetOrAdd(userId, _ => new User(userId, userName));
    }

    public async Task<Room> CreateRoomAsync(string roomId, User host)
    {
        var room = new Room(roomId, host, this, _webSocketService);
        if (!_rooms.TryAdd(roomId, room)) throw new InvalidOperationException("Room already exists");

        // WebSocket 通知
        _ = Task.Run(async () => {
            if (_webSocketService != null)
            {
                await _webSocketService.SendAdminUpdateAsync();
            }
        });

        return room;
    }

    public async Task<Room?> GetRoomAsync(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public async Task DisbandRoomAsync(string roomId,string message)
    {
        if (_rooms.TryRemove(roomId, out var room))
        {
            var users = room.GetAllUsers().ToList();
            await room.SendMessageAsync(new Message.Chat(0, $"房间已被管理员解散:{message}"));
            foreach (var u in users) if (u.Session != null) await u.Session.CloseAsync();

            // WebSocket 通知
            _ = Task.Run(async () => {
                if (_webSocketService != null)
                {
                    await _webSocketService.SendAdminUpdateAsync();
                }
            });
        }
    }

    public void AddSession(Guid id, Session session) => _sessions[id] = session;
    public void RemoveSession(Guid id) => _sessions.TryRemove(id, out _);
    public void RemoveRoom(string roomId) => _rooms.TryRemove(roomId, out _);

    public async Task OnConnectionLostAsync(Guid sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            if (session.User != null)
            {
                var user = session.User;
                user.Session = null;
                if (user.Room != null) await user.Room.OnUserLeaveAsync(user);
            }
            await session.CloseAsync();
        }
    }

    // HTTP API 
    public IEnumerable<Room> GetAllRooms() => _rooms.Values;
    public int GetRoomCount() => _rooms.Count;
    public int GetSessionCount() => _sessions.Count;
    public int GetUserCount() => _users.Count;

    public Room? GetRandomRecruitingRoom()
    {
        var list = _rooms.Values.Where(r => r.IsRecruiting && !r.IsLocked && r.GetPlayerCount() < r.MaxPlayerCount).ToList();
        return list.Count == 0 ? null : list[new Random().Next(list.Count)];
    }

    public WebSocketService? GetWebSocketService() => _webSocketService;
}
