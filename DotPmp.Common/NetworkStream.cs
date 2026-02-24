using System.Net.Sockets;
using System.Threading.Channels;

namespace DotPmp.Common;

public class NetworkStream<TSend, TReceive>
{
    private readonly TcpClient _client;
    private readonly Channel<TSend> _sendChannel;
    private readonly Func<TSend, byte[]> _serializer;
    private readonly Func<byte[], TReceive> _deserializer;
    private readonly Func<TReceive, Task> _messageHandler;
    private readonly CancellationTokenSource _cts;
    private readonly Task _sendTask;
    private readonly Task _receiveTask;
    private DateTime _lastReceiveTime;

    public byte Version { get; }
    public bool IsConnected => _client.Connected;

    public NetworkStream(
        TcpClient client,
        byte? version,
        Func<TSend, byte[]> serializer,
        Func<byte[], TReceive> deserializer,
        Func<TReceive, Task> messageHandler)
    {
        _client = client;
        _serializer = serializer;
        _deserializer = deserializer;
        _messageHandler = messageHandler;
        _cts = new CancellationTokenSource();
        _sendChannel = Channel.CreateUnbounded<TSend>();
        _lastReceiveTime = DateTime.UtcNow;

        var stream = _client.GetStream();

        if (version.HasValue)
        {
            Console.WriteLine($"[NetworkStream] Sending version: {version.Value}");
            stream.WriteByte(version.Value);
            stream.Flush();
            Version = version.Value;
        }
        else
        {
            Console.WriteLine("[NetworkStream] Waiting for client version...");
            var versionByte = stream.ReadByte();
            if (versionByte == -1)
                throw new IOException("Client disconnected before sending version");
            Version = (byte)versionByte;
            Console.WriteLine($"[NetworkStream] Received version: {Version}");
        }

        _sendTask = Task.Run(SendLoop);
        _receiveTask = Task.Run(ReceiveLoop);
    }

    public async Task SendAsync(TSend message)
    {
        await _sendChannel.Writer.WriteAsync(message);
    }

    public void Send(TSend message)
    {
        _sendChannel.Writer.TryWrite(message);
    }

    private async Task SendLoop()
    {
        var stream = _client.GetStream();
        var buffer = new byte[5];

        try
        {
            await foreach (var message in _sendChannel.Reader.ReadAllAsync(_cts.Token))
            {
                var data = _serializer(message);
                Console.WriteLine($"[NetworkStream] Sending {data.Length} bytes for {message?.GetType().Name}");

                // 写入长度（使用 VarInt 编码）
                var length = (uint)data.Length;
                var n = 0;
                do
                {
                    buffer[n] = (byte)(length & 0x7F);
                    length >>= 7;
                    if (length != 0)
                        buffer[n] |= 0x80;
                    n++;
                } while (length != 0);

                await stream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                await stream.WriteAsync(data, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Send error: {ex.Message}");
        }
    }

    private async Task ReceiveLoop()
    {
        var stream = _client.GetStream();

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                // 读取长度
                uint length = 0;
                int shift = 0;
                while (true)
                {
                    var b = stream.ReadByte();
                    if (b == -1)
                        return;

                    length |= (uint)(b & 0x7F) << shift;
                    if ((b & 0x80) == 0)
                        break;
                    shift += 7;
                    if (shift > 32)
                        throw new InvalidDataException("Invalid length");
                }

                if (length > 2 * 1024 * 1024)
                    throw new InvalidDataException("Packet too large");

                // 读取数据
                var data = new byte[length];
                var offset = 0;
                while (offset < length)
                {
                    var read = await stream.ReadAsync(data.AsMemory(offset, (int)length - offset), _cts.Token);
                    if (read == 0)
                        return;
                    offset += read;
                }

                _lastReceiveTime = DateTime.UtcNow;

                // 反序列化并处理消息
                try
                {
                    var message = _deserializer(data);
                    Console.WriteLine($"[NetworkStream] Received message: {message?.GetType().Name}");
                    await _messageHandler(message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NetworkStream] Failed to deserialize message: {ex.Message}");
                    Console.WriteLine($"[NetworkStream] Data: {BitConverter.ToString(data)}");
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Receive error: {ex.Message}");
        }
    }

    public DateTime GetLastReceiveTime() => _lastReceiveTime;

    public async Task CloseAsync()
    {
        _cts.Cancel();
        _sendChannel.Writer.Complete();

        try
        {
            await Task.WhenAll(_sendTask, _receiveTask);
        }
        catch { }

        _client.Close();
    }
}
