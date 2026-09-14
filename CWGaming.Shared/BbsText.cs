namespace CWGaming.Shared;

/// <summary>
/// Generic text helpers shared by the BBS host and any hosted door. No game semantics.
/// </summary>
public static class BbsText
{
    /// <summary>
    /// Title-case a single name part: capitalize the first character, preserve the rest. Used to
    /// normalize account / character / ticket names consistently across the BBS.
    /// </summary>
    public static string NormalizeNamePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string trimmed = value.Trim();
        if (trimmed.Length == 1)
            return trimmed.ToUpperInvariant();

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }
}
