namespace DotPmp.Common;

public static class MessageSerializer
{
    // ClientCommand 枚举顺序 (来自 phira-mp-common/src/command.rs):
    // 0: Ping
    // 1: Authenticate
    // 2: Chat
    // 3: Touches
    // 4: Judges
    // 5: CreateRoom
    // 6: JoinRoom
    // 7: LeaveRoom
    // 8: LockRoom
    // 9: CycleRoom
    // 10: SelectChart
    // 11: RequestStart
    // 12: Ready
    // 13: CancelReady
    // 14: Played
    // 15: Abort

    public static ClientCommand DeserializeClientCommand(byte[] data)
    {
        var reader = new BinaryReader(data);
        var type = reader.ReadByte();

        return type switch
        {
            0 => new ClientCommand.Ping(),
            1 => new ClientCommand.Authenticate(reader.ReadString()),
            2 => new ClientCommand.Chat(reader.ReadString()),
            3 => new ClientCommand.Touches(ReadTouchFrames(reader)),
            4 => new ClientCommand.Judges(ReadJudgeEvents(reader)),
            5 => new ClientCommand.CreateRoom(reader.ReadString()),
            6 => new ClientCommand.JoinRoom(reader.ReadString(), reader.ReadBool()),
            7 => new ClientCommand.LeaveRoom(),
            8 => new ClientCommand.LockRoom(reader.ReadBool()),
            9 => new ClientCommand.CycleRoom(reader.ReadBool()),
            10 => new ClientCommand.SelectChart(reader.ReadInt32()),
            11 => new ClientCommand.RequestStart(),
            12 => new ClientCommand.Ready(),
            13 => new ClientCommand.CancelReady(),
            14 => new ClientCommand.Played(reader.ReadInt32()),
            15 => new ClientCommand.Abort(),
            _ => throw new InvalidDataException($"Unknown command type: {type}")
        };
    }

    public static byte[] SerializeTouches(List<TouchFrame> frames)
    {
        var writer = new BinaryWriter();
        writer.WriteByte(3); // ClientCommand.Touches type
        WriteTouchFrames(writer, frames);
        return writer.ToArray();
    }

    public static byte[] SerializeJudges(List<JudgeEvent> events)
    {
        var writer = new BinaryWriter();
        writer.WriteByte(4); // ClientCommand.Judges type
        WriteJudgeEvents(writer, events);
        return writer.ToArray();
    }

    private static List<TouchFrame> ReadTouchFrames(BinaryReader reader)
    {
        var count = (int)reader.ReadULeb128();
        var frames = new List<TouchFrame>(count);
        for (int i = 0; i < count; i++)
        {
            var time = reader.ReadFloat();
            var pointCount = (int)reader.ReadULeb128();
            var points = new List<(sbyte Id, CompactPos Pos)>(pointCount);
            for (int j = 0; j < pointCount; j++)
            {
                var id = reader.ReadSByte();
                var x = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
                var y = BitConverter.UInt16BitsToHalf(reader.ReadUInt16());
                points.Add((id, new CompactPos((float)x, (float)y)));
            }
            frames.Add(new TouchFrame(time, points));
        }
        return frames;
    }

    private static List<JudgeEvent> ReadJudgeEvents(BinaryReader reader)
    {
        var count = (int)reader.ReadULeb128();
        var events = new List<JudgeEvent>(count);
        for (int i = 0; i < count; i++)
        {
            var time = reader.ReadFloat();
            var lineId = reader.ReadUInt32();
            var noteId = reader.ReadUInt32();
            var judgement = (Judgement)reader.ReadByte();
            events.Add(new JudgeEvent(time, lineId, noteId, judgement));
        }
        return events;
    }

    // ServerCommand 枚举顺序 (来自 phira-mp-common/src/command.rs):
    // 0: Pong
    // 1: Authenticate
    // 2: Chat
    // 3: Touches
    // 4: Judges
    // 5: Message
    // 6: ChangeState
    // 7: ChangeHost
    // 8: CreateRoom
    // 9: JoinRoom
    // 10: OnJoinRoom
    // 11: LeaveRoom
    // 12: LockRoom
    // 13: CycleRoom
    // 14: SelectChart
    // 15: RequestStart
    // 16: Ready
    // 17: CancelReady
    // 18: Played
    // 19: Abort

    public static byte[] SerializeServerCommand(ServerCommand command)
    {
        var writer = new BinaryWriter();

        switch (command)
        {
            case ServerCommand.Pong:
                writer.WriteByte(0);
                break;

            case ServerCommand.Authenticate auth:
                writer.WriteByte(1);
                writer.WriteBool(auth.Result.IsSuccess);
                if (auth.Result.IsSuccess)
                {
                    var (user, roomState) = auth.Result.Value!;
                    WriteUserInfo(writer, user);
                    writer.WriteBool(roomState != null);
                    if (roomState != null)
                        WriteClientRoomState(writer, roomState);
                }
                else
                {
                    writer.WriteString(auth.Result.Error ?? "Unknown error");
                }
                break;

            case ServerCommand.Chat chat:
                writer.WriteByte(2);
                WriteResult(writer, chat.Result);
                break;

            case ServerCommand.Touches touches:
                writer.WriteByte(3);
                writer.WriteInt32(touches.PlayerId);
                WriteTouchFrames(writer, touches.Frames);
                break;

            case ServerCommand.Judges judges:
                writer.WriteByte(4);
                writer.WriteInt32(judges.PlayerId);
                WriteJudgeEvents(writer, judges.JudgeEvents);
                break;

            case ServerCommand.Message msg:
                writer.WriteByte(5);
                WriteMessage(writer, msg.Msg);
                break;

            case ServerCommand.ChangeState state:
                writer.WriteByte(6);
                WriteRoomState(writer, state.State, state.ChartId);
                break;

            case ServerCommand.ChangeHost host:
                writer.WriteByte(7);
                writer.WriteBool(host.IsHost);
                break;

            case ServerCommand.CreateRoom create:
                writer.WriteByte(8);
                WriteResult(writer, create.Result);
                break;

            case ServerCommand.JoinRoom join:
                writer.WriteByte(9);
                writer.WriteBool(join.Result.IsSuccess);
                if (join.Result.IsSuccess && join.Result.Value != null)
                {
                    WriteRoomState(writer, join.Result.Value.State, null);
                    writer.WriteULeb128((ulong)join.Result.Value.Users.Count);
                    foreach (var user in join.Result.Value.Users)
                        WriteUserInfo(writer, user);
                    writer.WriteBool(join.Result.Value.Live);
                }
                else
                {
                    writer.WriteString(join.Result.Error ?? "Unknown error");
                }
                break;

            case ServerCommand.OnJoinRoom onJoin:
                writer.WriteByte(10);
                WriteUserInfo(writer, onJoin.User);
                break;

            case ServerCommand.LeaveRoom leave:
                writer.WriteByte(11);
                WriteResult(writer, leave.Result);
                break;

            case ServerCommand.LockRoom lockRoom:
                writer.WriteByte(12);
                WriteResult(writer, lockRoom.Result);
                break;

            case ServerCommand.CycleRoom cycleRoom:
                writer.WriteByte(13);
                WriteResult(writer, cycleRoom.Result);
                break;

            case ServerCommand.SelectChart select:
                writer.WriteByte(14);
                WriteResult(writer, select.Result);
                break;

            case ServerCommand.RequestStart start:
                writer.WriteByte(15);
                WriteResult(writer, start.Result);
                break;

            case ServerCommand.Ready ready:
                writer.WriteByte(16);
                WriteResult(writer, ready.Result);
                break;

            case ServerCommand.CancelReady cancelReady:
                writer.WriteByte(17);
                WriteResult(writer, cancelReady.Result);
                break;

            case ServerCommand.Played played:
                writer.WriteByte(18);
                WriteResult(writer, played.Result);
                break;

            case ServerCommand.Abort abort:
                writer.WriteByte(19);
                WriteResult(writer, abort.Result);
                break;
        }

        return writer.ToArray();
    }

    private static void WriteTouchFrames(BinaryWriter writer, List<TouchFrame> frames)
    {
        writer.WriteULeb128((ulong)frames.Count);
        foreach (var frame in frames)
        {
            writer.WriteFloat(frame.Time);
            writer.WriteULeb128((ulong)frame.Points.Count);
            foreach (var (id, pos) in frame.Points)
            {
                writer.WriteSByte(id);
                writer.WriteUInt16(BitConverter.HalfToUInt16Bits((Half)pos.X));
                writer.WriteUInt16(BitConverter.HalfToUInt16Bits((Half)pos.Y));
            }
        }
    }

    private static void WriteJudgeEvents(BinaryWriter writer, List<JudgeEvent> events)
    {
        writer.WriteULeb128((ulong)events.Count);
        foreach (var evt in events)
        {
            writer.WriteFloat(evt.Time);
            writer.WriteUInt32(evt.LineId);
            writer.WriteUInt32(evt.NoteId);
            writer.WriteByte((byte)evt.Judgement);
        }
    }

    private static void WriteUserInfo(BinaryWriter writer, UserInfo user)
    {
        writer.WriteInt32(user.Id);
        writer.WriteString(user.Name);
        writer.WriteBool(user.Monitor);
    }

    private static void WriteRoomState(BinaryWriter writer, RoomState state, int? chartId)
    {
        // RoomState 枚举:
        // 0: SelectChart(Option<i32>)
        // 1: WaitingForReady
        // 2: Playing
        writer.WriteByte((byte)state);
        if (state == RoomState.SelectChart)
        {
            writer.WriteBool(chartId.HasValue);
            if (chartId.HasValue)
                writer.WriteInt32(chartId.Value);
        }
    }

    private static void WriteClientRoomState(BinaryWriter writer, ClientRoomState state)
    {
        writer.WriteString(state.RoomId);
        WriteRoomState(writer, state.State, state.SelectedChartId);
        writer.WriteBool(state.Live);
        writer.WriteBool(state.Locked);
        writer.WriteBool(state.Cycle);
        writer.WriteBool(state.IsHost);
        writer.WriteBool(state.IsReady);
        // HashMap<i32, UserInfo>
        writer.WriteULeb128((ulong)state.Users.Count);
        foreach (var (id, user) in state.Users)
        {
            writer.WriteInt32(id);
            WriteUserInfo(writer, user);
        }
    }

    // Message 枚举顺序:
    // 0: Chat
    // 1: CreateRoom
    // 2: JoinRoom
    // 3: LeaveRoom
    // 4: NewHost
    // 5: SelectChart
    // 6: GameStart
    // 7: Ready
    // 8: CancelReady
    // 9: CancelGame
    // 10: StartPlaying
    // 11: Played
    // 12: GameEnd
    // 13: Abort
    // 14: LockRoom
    // 15: CycleRoom

    private static void WriteMessage(BinaryWriter writer, Message message)
    {
        switch (message)
        {
            case Message.Chat chat:
                writer.WriteByte(0);
                writer.WriteInt32(chat.UserId);
                writer.WriteString(chat.Content);
                break;
            case Message.CreateRoom create:
                writer.WriteByte(1);
                writer.WriteInt32(create.UserId);
                break;
            case Message.JoinRoom join:
                writer.WriteByte(2);
                writer.WriteInt32(join.UserId);
                writer.WriteString(join.Name);
                break;
            case Message.LeaveRoom leave:
                writer.WriteByte(3);
                writer.WriteInt32(leave.UserId);
                writer.WriteString(leave.Name);
                break;
            case Message.NewHost host:
                writer.WriteByte(4);
                writer.WriteInt32(host.UserId);
                break;
            case Message.SelectChart select:
                writer.WriteByte(5);
                writer.WriteInt32(select.UserId);
                writer.WriteString(select.Name);
                writer.WriteInt32(select.ChartId);
                break;
            case Message.GameStart start:
                writer.WriteByte(6);
                writer.WriteInt32(start.UserId);
                break;
            case Message.Ready ready:
                writer.WriteByte(7);
                writer.WriteInt32(ready.UserId);
                break;
            case Message.CancelReady cancelReady:
                writer.WriteByte(8);
                writer.WriteInt32(cancelReady.UserId);
                break;
            case Message.CancelGame cancelGame:
                writer.WriteByte(9);
                writer.WriteInt32(cancelGame.UserId);
                break;
            case Message.StartPlaying:
                writer.WriteByte(10);
                break;
            case Message.Played played:
                writer.WriteByte(11);
                writer.WriteInt32(played.UserId);
                writer.WriteInt32(played.Score);
                writer.WriteFloat(played.Accuracy);
                writer.WriteBool(played.FullCombo);
                break;
            case Message.GameEnd:
                writer.WriteByte(12);
                break;
            case Message.Abort abort:
                writer.WriteByte(13);
                writer.WriteInt32(abort.UserId);
                break;
            case Message.LockRoom lockRoom:
                writer.WriteByte(14);
                writer.WriteBool(lockRoom.Lock);
                break;
            case Message.CycleRoom cycleRoom:
                writer.WriteByte(15);
                writer.WriteBool(cycleRoom.Cycle);
                break;
        }
    }

    private static void WriteResult(BinaryWriter writer, Result<object?> result)
    {
        writer.WriteBool(result.IsSuccess);
        if (!result.IsSuccess)
            writer.WriteString(result.Error ?? "Unknown error");
    }
}
