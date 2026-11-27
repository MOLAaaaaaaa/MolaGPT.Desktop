using System.Buffers;

namespace MolaGPT.Core.Net;

public static class BoundedRead
{
    public static async Task<byte[]> ReadAsync(Stream source, int limit, CancellationToken ct)
    {
        var length = source.CanSeek ? source.Length - source.Position : 0;
        if (length > limit) throw new InvalidDataException($"Body exceeds {limit} bytes.");
        using var result = new MemoryStream((int)length);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (true)
            {
                var room = limit - (int)result.Length;
                var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, room + 1)), ct)
                    .ConfigureAwait(false);
                if (count == 0) return result.ToArray();
                if (count > room) throw new InvalidDataException($"Body exceeds {limit} bytes.");
                result.Write(buffer, 0, count);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
