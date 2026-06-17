namespace GraphData.Core.Services;

public static class NodeNameValidator {
    public const string AllowedCharactersDescription =
        "Allowed characters: Unicode letters and digits, spaces, '.', '_', '-', and '/' as a hierarchy separator.";
    public const string AllowedSegmentCharactersDescription =
        "Allowed characters: Unicode letters and digits, spaces, '.', '_', and '-'.";

    public static bool TryValidate(string? nodeName, out string error) {
        return TryValidate(nodeName, "Node name", out error);
    }

    public static bool TryValidate(string? nodeName, string subject, out string error) {
        if (string.IsNullOrWhiteSpace(nodeName)) {
            error = $"{subject} must be provided.";
            return false;
        }

        if (nodeName.StartsWith('/') || nodeName.EndsWith('/')) {
            error = $"{subject} must not start or end with '/'. {AllowedCharactersDescription}";
            return false;
        }

        if (nodeName.Contains("//", StringComparison.Ordinal)) {
            error = $"{subject} must not contain empty path segments ('//'). {AllowedCharactersDescription}";
            return false;
        }

        for (var i = 0; i < nodeName.Length; i++) {
            var ch = nodeName[i];
            if (IsAllowedCharacter(ch)) {
                continue;
            }

            var display = ch == '\\'
                ? "\\"
                : char.IsControl(ch)
                    ? $"U+{(int)ch:X4}"
                    : ch.ToString();
            var suffix = ch == '\\'
                ? " Use '/' to separate hierarchy segments."
                : string.Empty;
            error = $"{subject} contains invalid character '{display}' at position {i + 1}. {AllowedCharactersDescription}{suffix}";
            return false;
        }

        foreach (var segment in nodeName.Split('/')) {
            if (segment is "." or "..") {
                error = $"{subject} segment '{segment}' is not allowed.";
                return false;
            }

            if (segment.EndsWith(' ') || segment.EndsWith('.')) {
                error = $"{subject} segment '{segment}' must not end with space or '.'.";
                return false;
            }

            if (IsReservedWindowsDeviceName(segment)) {
                error = $"{subject} segment '{segment}' is reserved.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    public static bool TryValidateSegment(string? segment, string subject, out string error) {
        if (string.IsNullOrWhiteSpace(segment)) {
            error = $"{subject} must be provided.";
            return false;
        }

        for (var i = 0; i < segment.Length; i++) {
            var ch = segment[i];
            if (IsAllowedSegmentCharacter(ch)) {
                continue;
            }

            var display = ch == '\\'
                ? "\\"
                : char.IsControl(ch)
                    ? $"U+{(int)ch:X4}"
                    : ch.ToString();
            error = $"{subject} contains invalid character '{display}' at position {i + 1}. {AllowedSegmentCharactersDescription}";
            return false;
        }

        if (segment is "." or "..") {
            error = $"{subject} segment '{segment}' is not allowed.";
            return false;
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.')) {
            error = $"{subject} segment '{segment}' must not end with space or '.'.";
            return false;
        }

        if (IsReservedWindowsDeviceName(segment)) {
            error = $"{subject} segment '{segment}' is reserved.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static void Validate(string? nodeName, string paramName) {
        if (!TryValidate(nodeName, out var error))
            throw new ArgumentException(error, paramName);
    }

    private static bool IsAllowedCharacter(char ch) {
        return char.IsLetterOrDigit(ch)
            || ch is ' ' or '.' or '_' or '-' or '/';
    }

    private static bool IsAllowedSegmentCharacter(char ch) {
        return char.IsLetterOrDigit(ch)
            || ch is ' ' or '.' or '_' or '-';
    }

    private static bool IsReservedWindowsDeviceName(string segment) {
        var baseName = segment.TrimEnd(' ', '.');
        var dotIndex = baseName.IndexOf('.');
        if (dotIndex >= 0) {
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

    private static bool IsReservedDeviceRange(string name, string prefix) {
        return name.Length == prefix.Length + 1
            && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && name[^1] is >= '1' and <= '9';
    }
}
