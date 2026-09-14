namespace CWGaming.Shared;

/// <summary>
/// ANSI escape code helpers shared by BBS host and game module.
/// </summary>
public static class Ansi
{
    // Custom xterm 256-color (color 24, deep blue)
    public const string XtermBlue24 = "\x1b[38;5;24m";
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    // Standard foreground colors
    public const string Black = "\x1b[0;30m";
    public const string Red = "\x1b[0;31m";
    public const string Green = "\x1b[0;32m";
    public const string Yellow = "\x1b[0;33m";
    public const string Blue = "\x1b[0;34m";
    public const string Magenta = "\x1b[0;35m";
    public const string Cyan = "\x1b[0;36m";
    public const string White = "\x1b[0;37m";

    // Bright foreground colors (bold/bright)
    public const string BrightBlack = "\x1b[1;30m";   // Sometimes called DarkGray
    public const string BrightRed = "\x1b[1;31m";
    public const string BrightGreen = "\x1b[1;32m";
    public const string BrightYellow = "\x1b[1;33m";
    public const string BrightBlue = "\x1b[1;34m";
    public const string BrightMagenta = "\x1b[1;35m";
    public const string BrightCyan = "\x1b[1;36m";
    public const string BrightWhite = "\x1b[1;37m";
    public const string DarkGray = "\x1b[1;30m";

    // Standard background colors
    public const string BgBlack = "\x1b[40m";
    public const string BgRed = "\x1b[41m";
    public const string BgGreen = "\x1b[42m";
    public const string BgYellow = "\x1b[43m";
    public const string BgBlue = "\x1b[44m";
    public const string BgMagenta = "\x1b[45m";
    public const string BgCyan = "\x1b[46m";
    public const string BgWhite = "\x1b[47m";

    // Bright background colors (not always supported on all terminals)
    public const string BgBrightBlack = "\x1b[100m";
    public const string BgBrightRed = "\x1b[101m";
    public const string BgBrightGreen = "\x1b[102m";
    public const string BgBrightYellow = "\x1b[103m";
    public const string BgBrightBlue = "\x1b[104m";
    public const string BgBrightMagenta = "\x1b[105m";
    public const string BgBrightCyan = "\x1b[106m";
    public const string BgBrightWhite = "\x1b[107m";

    // Composite — real BBS sends [0;37;40m before room descriptions
    public const string WhiteOnBlack = "\x1b[0;37;40m";

    public const string LinePreamble = "\x1b[79D\x1b[K";

    public const string ClearScreen = "\x1b[2J\x1b[H";
    public const string ClearLine = "\x1b[2K";

    // NOTE: the MMUD "[HP=.../MA=...]" prompt is a DOOR (game) concern and now lives in mmudreborn
    // (MudAnsi.Prompt / GameAnsi.Prompt), which is also class-aware (MA vs Kai, no resource for martial
    // classes). It was removed from this generic BBS ANSI library so the host carries no game-specific
    // prompt knowledge.

    public static string Error(string text) => $"{BrightRed}{text}{Reset}";
    public static string Info(string text) => $"{BrightCyan}{text}{Reset}";
    public static string SystemMsg(string text) => $"{BrightYellow}{text}{Reset}";
}
