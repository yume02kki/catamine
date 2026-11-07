using System.Buffers;
using System.IO.Compression;

namespace Mirror.Core.Compression;

/// <summary>Storage compression for bodies/headers/diffs (Brotli, fast setting) and HTTP content-encoding decoding.</summary>
public static class BodyCodec
{
    public const short None = 0;
    public const short Brotli = 1;

    private const int Quality = 4;
    private const int Window = 22;

    public static byte[]? Compress(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;
        var max = BrotliEncoder.GetMaxCompressedLength(data.Length);
        var rented = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            if (!BrotliEncoder.TryCompress(data, rented, out var written, Quality, Window))
                throw new InvalidOperationException("brotli compression failed");
            return rented.AsSpan(0, written).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static byte[] Decompress(byte[]? data)
    {
        if (data is null || data.Length == 0) return [];
        using var input = new MemoryStream(data, writable: false);
        using var br = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(data.Length * 4);
        br.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Decodes an HTTP body according to its Content-Encoding. Unknown encodings are returned as-is.</summary>
    public static byte[] DecodeContent(ReadOnlyMemory<byte> body, string? contentEncoding, int maxBytes, out bool decoded)
    {
        decoded = false;
        if (body.IsEmpty || string.IsNullOrWhiteSpace(contentEncoding) || contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return body.ToArray();

        var encodings = contentEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var current = body.ToArray();
        for (var i = encodings.Length - 1; i >= 0; i--)
        {
            Stream? s = encodings[i].ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(new MemoryStream(current), CompressionMode.Decompress),
                "deflate" => new ZLibStream(new MemoryStream(current), CompressionMode.Decompress),
                "br" => new BrotliStream(new MemoryStream(current), CompressionMode.Decompress),
                _ => null,
            };
            if (s is null) return body.ToArray();
            using (s)
            {
                using var output = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int n;
                    while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + n > maxBytes) throw new InvalidDataException("decoded body exceeds limit");
                        output.Write(buffer, 0, n);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                current = output.ToArray();
            }
        }
        decoded = true;
        return current;
    }
}
