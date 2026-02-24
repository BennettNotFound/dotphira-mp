using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotPmp.Server;

public record BanInfo(long UserId);

public class AdminData
{
    [JsonInclude] public List<BanInfo> UserBans { get; set; } = new();
    [JsonInclude] public Dictionary<string, List<long>> RoomBans { get; set; } = new();
}

public class AdminDataService
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<long, BanInfo> _bannedUsers = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<long, bool>> _bannedFromRooms = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AdminDataService(ServerConfig config)
    {
        _filePath = Path.IsPathRooted(config.AdminDataPath) 
            ? config.AdminDataPath 
            : Path.Combine(AppContext.BaseDirectory, config.AdminDataPath);
    }

    public void Load()
    {
        if (!File.Exists(_filePath)) return;
        try {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<AdminData>(json);
            if (data == null) return;

            _bannedUsers.Clear();
            foreach (var b in data.UserBans) _bannedUsers[b.UserId] = b;

            _bannedFromRooms.Clear();
            foreach (var rb in data.RoomBans) {
                var set = new ConcurrentDictionary<long, bool>();
                foreach (var id in rb.Value) set[id] = true;
                _bannedFromRooms[rb.Key] = set;
            }
        } catch (Exception ex) { Console.WriteLine($"Load error: {ex.Message}"); }
    }

    private async Task SaveAsync()
    {
        try {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var data = new AdminData {
                UserBans = _bannedUsers.Values.ToList(),
                RoomBans = _bannedFromRooms.ToDictionary(k => k.Key, v => v.Value.Keys.ToList())
            };
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(data, JsonOptions));
        } catch (Exception ex) { Console.WriteLine($"Save error: {ex.Message}"); }
    }

    public bool IsBanned(long userId) => _bannedUsers.ContainsKey(userId);
    public async Task BanUserAsync(long userId) { _bannedUsers[userId] = new BanInfo(userId); await SaveAsync(); }
    public async Task UnbanUserAsync(long userId) { if (_bannedUsers.TryRemove(userId, out _)) await SaveAsync(); }

    public bool IsBannedFromRoom(string roomId, long userId) => 
        _bannedFromRooms.TryGetValue(roomId, out var set) && set.ContainsKey(userId);

    public async Task BanFromRoomAsync(string roomId, long userId) {
        _bannedFromRooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<long, bool>())[userId] = true;
        await SaveAsync();
    }

    public async Task UnbanFromRoomAsync(string roomId, long userId) {
        if (_bannedFromRooms.TryGetValue(roomId, out var set) && set.TryRemove(userId, out _)) await SaveAsync();
    }
}
