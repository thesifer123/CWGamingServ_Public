using CWGaming.Shared;

namespace CWGamingServ;

public static class BbsAnsi
{
    public static string XtermBlue24 => Ansi.XtermBlue24;
    public static string Reset => Ansi.Reset;
    public static string Bold => Ansi.Bold;
    public static string Black => Ansi.Black;
    public static string Red => Ansi.Red;
    public static string Green => Ansi.Green;
    public static string Yellow => Ansi.Yellow;
    public static string Blue => Ansi.Blue;
    public static string Magenta => Ansi.Magenta;
    public static string Cyan => Ansi.Cyan;
    public static string White => Ansi.White;
    public static string BrightBlack => Ansi.BrightBlack;
    public static string BrightRed => Ansi.BrightRed;
    public static string BrightGreen => Ansi.BrightGreen;
    public static string BrightYellow => Ansi.BrightYellow;
    public static string BrightBlue => Ansi.BrightBlue;
    public static string BrightMagenta => Ansi.BrightMagenta;
    public static string BrightCyan => Ansi.BrightCyan;
    public static string BrightWhite => Ansi.BrightWhite;
    public static string DarkGray => Ansi.DarkGray;
    public static string BgBlack => Ansi.BgBlack;
    public static string BgRed => Ansi.BgRed;
    public static string BgGreen => Ansi.BgGreen;
    public static string BgYellow => Ansi.BgYellow;
    public static string BgBlue => Ansi.BgBlue;
    public static string BgMagenta => Ansi.BgMagenta;
    public static string BgCyan => Ansi.BgCyan;
    public static string BgWhite => Ansi.BgWhite;
    public static string BgBrightBlack => Ansi.BgBrightBlack;
    public static string BgBrightRed => Ansi.BgBrightRed;
    public static string BgBrightGreen => Ansi.BgBrightGreen;
    public static string BgBrightYellow => Ansi.BgBrightYellow;
    public static string BgBrightBlue => Ansi.BgBrightBlue;
    public static string BgBrightMagenta => Ansi.BgBrightMagenta;
    public static string BgBrightCyan => Ansi.BgBrightCyan;
    public static string BgBrightWhite => Ansi.BgBrightWhite;
    public static string WhiteOnBlack => Ansi.WhiteOnBlack;
    public static string LinePreamble => Ansi.LinePreamble;
    public static string ClearScreen => Ansi.ClearScreen;
    public static string ClearLine => Ansi.ClearLine;

    public static string MainMenuSelectionPrompt()
        => $"{BrightWhite}Selection: {Reset}";

    public static string LoginNamePrompt()
        => "Enter your BBS account name (or 'new' to create): ";

    public static string PasswordPrompt()
        => "Enter your Password: ";

    public static string NewAccountNamePrompt()
        => "Choose a BBS account name: ";

    public static string NewPasswordPrompt()
        => "Choose a password: ";

    public static string ConfirmPasswordPrompt()
        => "Confirm password: ";

    public static string Error(string text) => Ansi.Error(text);
    public static string Info(string text) => Ansi.Info(text);
    public static string SystemMsg(string text) => Ansi.SystemMsg(text);
}