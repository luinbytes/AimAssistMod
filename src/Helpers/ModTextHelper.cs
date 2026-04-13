using System;

internal static class ModTextHelper
{
    internal static string EscapeRichText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    internal static string NormalizeDisplayName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        string normalized = value.Trim();
        if (normalized.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(0, normalized.Length - "(Clone)".Length).Trim();
        }

        return normalized;
    }

    internal static bool TryGetValidDisplayName(string value, out string displayName)
    {
        displayName = null;
        string normalized = NormalizeDisplayName(value);
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        string lowered = normalized.ToLowerInvariant();
        if (lowered == "null" || lowered == "none" || lowered == "unknown")
        {
            return false;
        }

        displayName = normalized;
        return true;
    }
}
