using System.Text;
using DotPmp.Common;
using ZstdSharp;

namespace DotPmp.Server;

public class ReplayWriter : IAsyncDisposable
{
    private static readonly byte[] FileHeader = Encoding.ASCII.GetBytes("PHIRAREC");
    private const int FileVersion = 1;
    private const byte CompressionTypeZstd = 0x01;

    private readonly string _filePath;
    private readonly int _chartId;
    private readonly string _chartName;
    private readonly int _userId;
    private readonly string _userName;
    private readonly long _timestamp;
    private readonly List<TouchFrame> _touchFrames = new();
    private readonly List<JudgeEvent> _judgeEvents = new();

    private int _recordId;
    private bool _isClosed;

    public string FilePath => _filePath;
    public long Timestamp => _timestamp;
    public int RecordId => _recordId;

    public ReplayWriter(string filePath, int chartId, string chartName, int userId, string userName)
    {
        _filePath = filePath;
        _chartId = chartId;
        _chartName = chartName;
        _userId = userId;
        _userName = userName;
        _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    public Task UpdateRecordIdAsync(int recordId)
    {
        if (!_isClosed)
            _recordId = recordId;
        return Task.CompletedTask;
    }

    public Task WriteTouchesAsync(List<TouchFrame> frames)
    {
        if (!_isClosed)
            _touchFrames.AddRange(frames);
        return Task.CompletedTask;
    }

    public Task WriteJudgesAsync(List<JudgeEvent> events)
    {
        if (!_isClosed)
            _judgeEvents.AddRange(events);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isClosed)
            return;

        _isClosed = true;

        var writer = new DotPmp.Common.BinaryWriter();
        foreach (var b in FileHeader)
            writer.WriteByte(b);

        writer.WriteInt32(FileVersion);
        writer.WriteByte(CompressionTypeZstd);

        var payload = new DotPmp.Common.BinaryWriter();
        payload.WriteInt32(_recordId);
        payload.WriteInt64(_timestamp);
        payload.WriteInt32(_chartId);
        payload.WriteString(_chartName);
        payload.WriteInt32(_userId);
        payload.WriteString(_userName);
        WriteTouchFrames(payload, _touchFrames);
        WriteJudgeEvents(payload, _judgeEvents);

        var compressedPayload = new Compressor().Wrap(payload.ToArray());
        foreach (var b in compressedPayload)
            writer.WriteByte(b);

        await File.WriteAllBytesAsync(_filePath, writer.ToArray());
    }

    private static void WriteTouchFrames(DotPmp.Common.BinaryWriter writer, List<TouchFrame> frames)
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

    private static void WriteJudgeEvents(DotPmp.Common.BinaryWriter writer, List<JudgeEvent> events)
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
}
