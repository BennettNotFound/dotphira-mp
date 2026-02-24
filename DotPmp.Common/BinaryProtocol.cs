using System.Text;

namespace DotPmp.Common;

public class BinaryWriter
{
    private readonly MemoryStream _stream;
    private readonly System.IO.BinaryWriter _writer;

    public BinaryWriter()
    {
        _stream = new MemoryStream();
        _writer = new System.IO.BinaryWriter(_stream, Encoding.UTF8);
    }

    public void WriteULeb128(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            _writer.Write(b);
        } while (value != 0);
    }

    public void WriteString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteULeb128((ulong)bytes.Length);
        _writer.Write(bytes);
    }

    public void WriteSByte(sbyte value) => _writer.Write(value);
    public void WriteByte(byte value) => _writer.Write(value);
    public void WriteInt32(int value) => _writer.Write(value);
    public void WriteUInt16(ushort value) => _writer.Write(value);
    public void WriteUInt32(uint value) => _writer.Write(value);
    public void WriteFloat(float value) => _writer.Write(value);
    public void WriteBool(bool value) => _writer.Write(value);

    public byte[] ToArray() => _stream.ToArray();
}

public class BinaryReader
{
    private readonly System.IO.BinaryReader _reader;

    public BinaryReader(byte[] data)
    {
        _reader = new System.IO.BinaryReader(new MemoryStream(data), Encoding.UTF8);
    }

    public ulong ReadULeb128()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = _reader.ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
            if (shift > 64)
                throw new InvalidDataException("Invalid ULeb128");
        }
        return result;
    }

    public string ReadString()
    {
        var length = (int)ReadULeb128();
        var bytes = _reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }

    public sbyte ReadSByte() => _reader.ReadSByte();
    public byte ReadByte() => _reader.ReadByte();
    public int ReadInt32() => _reader.ReadInt32();
    public ushort ReadUInt16() => _reader.ReadUInt16();
    public uint ReadUInt32() => _reader.ReadUInt32();
    public float ReadFloat() => _reader.ReadSingle();
    public bool ReadBool() => _reader.ReadBoolean();
}
