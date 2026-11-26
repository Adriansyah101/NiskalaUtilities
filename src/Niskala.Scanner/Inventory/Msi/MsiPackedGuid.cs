namespace Niskala.Scanner.Inventory.Msi;

public static class MsiPackedGuid
{
    public static string Pack(Guid code)
    {
        Span<char> packed = stackalloc char[32];
        Permute(code.ToString("N").ToUpperInvariant(), packed);
        return new string(packed);
    }

    public static bool TryUnpack(ReadOnlySpan<char> packed, out Guid code)
    {
        code = Guid.Empty;
        if (packed.Length != 32)
        {
            return false;
        }

        Span<char> hex = stackalloc char[32];
        Permute(packed, hex);
        return Guid.TryParseExact(hex, "N", out code);
    }

    private static void Permute(ReadOnlySpan<char> source, Span<char> target)
    {
        Reverse(source[..8], target[..8]);
        Reverse(source[8..12], target[8..12]);
        Reverse(source[12..16], target[12..16]);
        for (int i = 16; i < 32; i += 2)
        {
            target[i] = source[i + 1];
            target[i + 1] = source[i];
        }
    }

    private static void Reverse(ReadOnlySpan<char> source, Span<char> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            target[i] = source[source.Length - 1 - i];
        }
    }
}
