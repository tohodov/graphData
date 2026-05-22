using System.Text;

namespace SymLinkStorage;

internal static class NodeNamePathCodec
{
    private const string EncodedPrefix = "__gdn_";

    private static readonly char[] PathSeparators = new[] { '/', '\\' };
    private static readonly HashSet<char> InvalidFileNameChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
        .ToHashSet();

    public static string EncodePath(string nodeName)
    {
        return Path.Combine(GetPathSegments(nodeName).Select(EncodeFileName).ToArray());
    }

    public static string CombinePath(string rootPath, string nodeName)
    {
        return Path.Combine(rootPath, EncodePath(nodeName));
    }

    public static string DecodePath(string relativePath)
    {
        return string.Join(
            '/',
            GetPathSegments(relativePath).Select(DecodeFileName));
    }

    public static string EncodeFileName(string name)
    {
        return NeedsEncoding(name)
            ? EncodedPrefix + EncodeBase64Url(Encoding.UTF8.GetBytes(name))
            : name;
    }

    public static string DecodeFileName(string fileName)
    {
        if (!fileName.StartsWith(EncodedPrefix, StringComparison.Ordinal))
        {
            return fileName;
        }

        try
        {
            var encoded = fileName[EncodedPrefix.Length..];
            return Encoding.UTF8.GetString(DecodeBase64Url(encoded));
        }
        catch (FormatException)
        {
            return fileName;
        }
        catch (ArgumentException)
        {
            return fileName;
        }
    }

    private static IEnumerable<string> GetPathSegments(string nodeName)
    {
        return nodeName.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool NeedsEncoding(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return true;
        }

        if (name.StartsWith(EncodedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.'))
        {
            return true;
        }

        if (IsReservedWindowsDeviceName(name))
        {
            return true;
        }

        return name.Any(static ch => char.IsControl(ch) || InvalidFileNameChars.Contains(ch));
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        var baseName = name.TrimEnd(' ', '.');
        var dotIndex = baseName.IndexOf('.');
        if (dotIndex >= 0)
        {
            baseName = baseName[..dotIndex];
        }

        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || baseName.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || IsReservedDeviceRange(baseName, "COM")
            || IsReservedDeviceRange(baseName, "LPT");
    }

    private static bool IsReservedDeviceRange(string name, string prefix)
    {
        return name.Length == prefix.Length + 1
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name[^1] is >= '1' and <= '9';
    }

    private static string EncodeBase64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] DecodeBase64Url(string encoded)
    {
        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        base64 = base64.PadRight(base64.Length + padding, '=');
        return Convert.FromBase64String(base64);
    }
}
