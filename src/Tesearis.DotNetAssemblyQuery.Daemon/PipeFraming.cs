using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Tesearis.DotNetAssemblyQuery;

/// <summary>
/// Length-prefixed message framing over a <see cref="PipeStream"/>: a 4-byte little-endian
/// length, followed by that many UTF-8 JSON bytes. Used for both the raw version handshake (a
/// bare int32, no JSON) and every request/response frame after it.
/// </summary>
internal static class PipeFraming
{
    public static void WriteInt32(PipeStream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BitConverter.TryWriteBytes(buffer, value);
        stream.Write(buffer);
    }

    public static int ReadInt32(PipeStream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExactly(stream, buffer);
        return BitConverter.ToInt32(buffer);
    }

    public static void WriteJson<T>(PipeStream stream, T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    public static T ReadJson<T>(PipeStream stream, JsonTypeInfo<T> typeInfo)
    {
        var length = ReadInt32(stream);
        var buffer = new byte[length];
        ReadExactly(stream, buffer);
        return JsonSerializer.Deserialize(buffer, typeInfo) ?? throw new InvalidDataException($"Received a null {typeof(T).Name} frame.");
    }

    private static void ReadExactly(PipeStream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                throw new EndOfStreamException("Pipe closed before the expected frame was fully read.");
            }

            read += n;
        }
    }
}
