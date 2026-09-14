namespace CWGaming.Shared;

public static class BbsMenuSettings
{
    public const string AutoAdvanceEnvironmentVariable = "MMUDREBORN_MENU_AUTO_ADVANCE";
    public const string AutoAdvanceSettingKey = "BbsMenuAutoAdvanceEnabled";

    public static bool GetEffectiveAutoAdvanceEnabled(IBbsUserRepository? bbsUserRepository, bool defaultValue = true)
    {
        return bbsUserRepository != null && TryGetPersistedAutoAdvanceEnabled(bbsUserRepository, out var enabled)
            ? enabled
            : ReadEnvironmentToggle(AutoAdvanceEnvironmentVariable, defaultValue);
    }

    public static bool TryGetPersistedAutoAdvanceEnabled(IBbsUserRepository bbsUserRepository, out bool enabled)
    {
        ArgumentNullException.ThrowIfNull(bbsUserRepository);
        return TryParseToggle(bbsUserRepository.GetSettingText(AutoAdvanceSettingKey, string.Empty), out enabled);
    }

    public static void SetAutoAdvanceEnabled(IBbsUserRepository bbsUserRepository, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(bbsUserRepository);
        bbsUserRepository.SetSettingText(AutoAdvanceSettingKey, enabled ? "1" : "0");
    }

    public static bool TryParseToggle(string? rawValue, out bool enabled)
    {
        switch (rawValue?.Trim().ToUpperInvariant())
        {
            case "1":
            case "TRUE":
            case "YES":
            case "ON":
                enabled = true;
                return true;

            case "0":
            case "FALSE":
            case "NO":
            case "OFF":
                enabled = false;
                return true;

            default:
                enabled = false;
                return false;
        }
    }

    private static bool ReadEnvironmentToggle(string variableName, bool defaultValue)
    {
        var rawValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(rawValue))
            return defaultValue;

        return TryParseToggle(rawValue, out var enabled) ? enabled : defaultValue;
    }
}
