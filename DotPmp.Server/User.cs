using DotPmp.Common;

namespace DotPmp.Server;

public class User
{
    public int Id { get; }
    public string Name { get; }
    public Session? Session { get; set; }
    public Room? Room { get; set; }
    public ReplayWriter? CurrentReplay { get; set; }
    public bool IsMonitor { get; set; }
    public float GameTime { get; set; }
    public bool IsConnected => Session != null;

    public User(int id, string name)
    {
        Id = id;
        Name = name;
    }

    public UserInfo ToInfo() => new(Id, Name, IsMonitor);

    public async Task SendAsync(ServerCommand command)
    {
        if (Session != null)
        {
            await Session.SendAsync(command);
        }
    }
}
