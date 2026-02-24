namespace DotPmp.Common;

public record CompactPos(float X, float Y);

public record TouchFrame(float Time, List<(sbyte Id, CompactPos Pos)> Points);

public enum Judgement : byte
{
    Perfect,
    Good,
    Bad,
    Miss,
    HoldPerfect,
    HoldGood
}

public record JudgeEvent(float Time, uint LineId, uint NoteId, Judgement Judgement);

public abstract record ClientCommand
{
    public record Ping : ClientCommand;
    public record Authenticate(string Token) : ClientCommand;
    public record Chat(string Message) : ClientCommand;
    public record Touches(List<TouchFrame> Frames) : ClientCommand;
    public record Judges(List<JudgeEvent> JudgeEvents) : ClientCommand;
    public record CreateRoom(string RoomId) : ClientCommand;
    public record JoinRoom(string RoomId, bool Monitor) : ClientCommand;
    public record LeaveRoom : ClientCommand;
    public record LockRoom(bool Lock) : ClientCommand;
    public record CycleRoom(bool Cycle) : ClientCommand;
    public record SelectChart(int ChartId) : ClientCommand;
    public record RequestStart : ClientCommand;
    public record Ready : ClientCommand;
    public record CancelReady : ClientCommand;
    public record Played(int RecordId) : ClientCommand;
    public record Abort : ClientCommand;
}

public abstract record Message
{
    public record Chat(int UserId, string Content) : Message;
    public record CreateRoom(int UserId) : Message;
    public record JoinRoom(int UserId, string Name) : Message;
    public record LeaveRoom(int UserId, string Name) : Message;
    public record NewHost(int UserId) : Message;
    public record SelectChart(int UserId, string Name, int ChartId) : Message;
    public record GameStart(int UserId) : Message;
    public record Ready(int UserId) : Message;
    public record CancelReady(int UserId) : Message;
    public record CancelGame(int UserId) : Message;
    public record StartPlaying : Message;
    public record Played(int UserId, int Score, float Accuracy, bool FullCombo) : Message;
    public record GameEnd : Message;
    public record Abort(int UserId) : Message;
    public record LockRoom(bool Lock) : Message;
    public record CycleRoom(bool Cycle) : Message;
}

public enum RoomState : byte
{
    SelectChart,
    WaitingForReady,
    Playing
}

public record UserInfo(int Id, string Name, bool Monitor);

public record ClientRoomState(
    string RoomId,
    RoomState State,
    bool Live,
    bool Locked,
    bool Cycle,
    bool IsHost,
    bool IsReady,
    Dictionary<int, UserInfo> Users,
    int? SelectedChartId
);

public record JoinRoomResponse(
    RoomState State,
    List<UserInfo> Users,
    bool Live
);

public abstract record ServerCommand
{
    public record Pong : ServerCommand;
    public record Authenticate(Result<(UserInfo User, ClientRoomState? RoomState)> Result) : ServerCommand;
    public record Chat(Result<object?> Result) : ServerCommand;
    public record Touches(int PlayerId, List<TouchFrame> Frames) : ServerCommand;
    public record Judges(int PlayerId, List<JudgeEvent> JudgeEvents) : ServerCommand;
    public record Message(Common.Message Msg) : ServerCommand;
    public record ChangeState(RoomState State, int? ChartId) : ServerCommand;
    public record ChangeHost(bool IsHost) : ServerCommand;
    public record CreateRoom(Result<object?> Result) : ServerCommand;
    public record JoinRoom(Result<JoinRoomResponse> Result) : ServerCommand;
    public record OnJoinRoom(UserInfo User) : ServerCommand;
    public record LeaveRoom(Result<object?> Result) : ServerCommand;
    public record LockRoom(Result<object?> Result) : ServerCommand;
    public record CycleRoom(Result<object?> Result) : ServerCommand;
    public record SelectChart(Result<object?> Result) : ServerCommand;
    public record RequestStart(Result<object?> Result) : ServerCommand;
    public record Ready(Result<object?> Result) : ServerCommand;
    public record CancelReady(Result<object?> Result) : ServerCommand;
    public record Played(Result<object?> Result) : ServerCommand;
    public record Abort(Result<object?> Result) : ServerCommand;
}

public record Result<T>
{
    public bool IsSuccess { get; init; }
    public T? Value { get; init; }
    public string? Error { get; init; }

    public static Result<T> Success(T value) => new() { IsSuccess = true, Value = value };
    public static Result<T> Failure(string error) => new() { IsSuccess = false, Error = error };
}
