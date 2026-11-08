using System.Security.Cryptography;

namespace Mirror.Core.Models;

/// <summary>
/// RFC 9562 version 7 UUIDs: 48-bit big-endian Unix milliseconds, then random bits. They sort by creation
/// time, which keeps the log's primary key index append-mostly. .NET 8 has no Guid.CreateVersion7, so this
/// produces the same layout (identical to Guid.CreateVersion7 on .NET 9 and later).
/// </summary>
public static class Uuid7
{
    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        var ms = timestamp.ToUnixTimeMilliseconds();
        if (ms < 0) throw new ArgumentOutOfRangeException(nameof(timestamp), "UUIDv7 timestamps start at the Unix epoch");

        Span<byte> b = stackalloc byte[16];
        RandomNumberGenerator.Fill(b[6..]);
        b[0] = (byte)(ms >> 40);
        b[1] = (byte)(ms >> 32);
        b[2] = (byte)(ms >> 24);
        b[3] = (byte)(ms >> 16);
        b[4] = (byte)(ms >> 8);
        b[5] = (byte)ms;
        b[6] = (byte)((b[6] & 0x0F) | 0x70); // version 7
        b[8] = (byte)((b[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(b, bigEndian: true);
    }
}
