namespace GameTrainer.Core.Memory;

public sealed class AobPattern
{
    public byte?[] Bytes { get; }

    public AobPattern(string pattern)
    {
        Bytes = pattern
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token is "?" or "??" ? (byte?)null : Convert.ToByte(token, 16))
            .ToArray();
    }

    public bool IsMatch(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + Bytes.Length > data.Length) return false;
        for (var i = 0; i < Bytes.Length; i++)
        {
            if (Bytes[i].HasValue && data[offset + i] != Bytes[i]!.Value)
                return false;
        }
        return true;
    }
}
