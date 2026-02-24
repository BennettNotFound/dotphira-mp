using DotPmp.Common;

namespace DotPmp.Server;

public class ReplayWriter : IAsyncDisposable
{
    private readonly FileStream _fileStream;
    private readonly long _timestamp;
    private int _recordId = 0;
    private bool _isClosed = false;

    public long Timestamp => _timestamp;

    public ReplayWriter(string filePath, int chartId, int userId)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory != null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, true);
        _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 写入 14 字节文件头 (小端序)
        var header = new byte[14];
        BitConverter.TryWriteBytes(header.AsSpan(0, 2), (ushort)0x504D); // "PM"
        BitConverter.TryWriteBytes(header.AsSpan(2, 4), (uint)chartId);
        BitConverter.TryWriteBytes(header.AsSpan(6, 4), (uint)userId);
        BitConverter.TryWriteBytes(header.AsSpan(10, 4), (uint)0); // Record ID 初始为 0
        
        _fileStream.Write(header);
    }

    /// <summary>
    /// 当玩家上传成绩后，更新文件头中的 Record ID
    /// </summary>
    public async Task UpdateRecordIdAsync(int recordId)
    {
        if (_isClosed) return;
        
        _recordId = recordId;
        var pos = _fileStream.Position;
        _fileStream.Seek(10, SeekOrigin.Begin);
        
        var buffer = new byte[4];
        BitConverter.TryWriteBytes(buffer, (uint)recordId);
        await _fileStream.WriteAsync(buffer);
        
        _fileStream.Seek(pos, SeekOrigin.Begin);
    }

    public async Task WriteTouchesAsync(List<TouchFrame> frames)
    {
        if (_isClosed) return;
        var data = MessageSerializer.SerializeTouches(frames);
        await _fileStream.WriteAsync(data);
    }

    public async Task WriteJudgesAsync(List<JudgeEvent> events)
    {
        if (_isClosed) return;
        var data = MessageSerializer.SerializeJudges(events);
        await _fileStream.WriteAsync(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (_isClosed) return;
        _isClosed = true;
        await _fileStream.FlushAsync();
        await _fileStream.DisposeAsync();
    }
}
