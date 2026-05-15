using System.Text;

namespace WEDM.Engine.Discovery.Scanners;

/// <summary>Safe binary pattern scanning for Oracle Forms .fmb modules (no UTF-8 text decoding).</summary>
public static class FormsBinaryScanner
{
    private static readonly byte[][] Patterns =
    [
        "WEBUTIL"u8.ToArray(),
        "webutil"u8.ToArray(),
        "OLE"u8.ToArray(),
        "HOST"u8.ToArray(),
        "TEXT_IO"u8.ToArray(),
        "CLIENT_OLE2"u8.ToArray(),
        "RUN_REPORT_OBJECT"u8.ToArray(),
    ];

    private const int WindowSize = 65536;
    private const long MaxScanBytes = 8 * 1024 * 1024;

    public static bool ContainsPattern(string filePath, string pattern)
    {
        if (!File.Exists(filePath)) return false;
        var needle = Encoding.ASCII.GetBytes(pattern);
        return ScanFile(filePath, needle);
    }

    public static IReadOnlyList<string> DetectDependencies(string filePath)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(filePath)) return [];

        foreach (var label in new[] { "WEBUTIL", "OLE", "HOST", "TEXT_IO", "CLIENT_OLE2", "RUN_REPORT_OBJECT" })
        {
            if (ContainsPattern(filePath, label))
                found.Add(label);
        }

        return found.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool ScanFile(string filePath, byte[] needle)
    {
        if (needle.Length == 0) return false;

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length == 0) return false;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[Math.Min(WindowSize, (int)Math.Min(info.Length, MaxScanBytes))];
            long totalRead = 0;
            var carry = Array.Empty<byte>();

            while (totalRead < Math.Min(info.Length, MaxScanBytes))
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                var combined = carry.Length == 0 ? buffer.AsSpan(0, read).ToArray() : Concat(carry, buffer, read);
                if (IndexOf(combined, needle) >= 0)
                    return true;

                carry = combined.Length > needle.Length
                    ? combined.AsSpan(combined.Length - needle.Length + 1).ToArray()
                    : combined;
                totalRead += read;
            }

            return carry.Length > 0 && IndexOf(carry, needle) >= 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] Concat(byte[] left, byte[] right, int rightLength)
    {
        var result = new byte[left.Length + rightLength];
        Buffer.BlockCopy(left, 0, result, 0, left.Length);
        Buffer.BlockCopy(right, 0, result, left.Length, rightLength);
        return result;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }
}
