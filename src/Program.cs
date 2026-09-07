// Program.cs
// Hockey Fight
//
// Entry point. Windows launches a .scr with one of a small set of switches:
//
//   /s              run full screen
//   /p <hwnd>       render a preview into the settings dialog's thumbnail window
//   /c[:<hwnd>]     show the configuration dialog
//   /a <hwnd>       change password (legacy, ignored)
//
// The switches arrive in several spellings ("/p 1234", "/p:1234", "-s", "/S"), so
// they are normalised before dispatch.

using System.Globalization;

namespace HockeyFight;

internal enum ScreenSaverMode
{
    FullScreen,
    Preview,
    Configure,
    Windowed,
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        ScreenSaverMode mode = ParseArguments(args, out IntPtr targetHandle);

        switch (mode)
        {
            case ScreenSaverMode.FullScreen:
                RunFullScreen();
                break;

            case ScreenSaverMode.Preview:
                if (targetHandle == IntPtr.Zero || !NativeMethods.IsWindow(targetHandle))
                {
                    return 1;
                }
                Application.Run(new ApplicationContextWithForms(ScreenSaverForm.CreatePreview(targetHandle)));
                break;

            case ScreenSaverMode.Windowed:
                Application.Run(new ApplicationContextWithForms(
                    ScreenSaverForm.CreateWindowed(new Size(1600, 900))));
                break;

            case ScreenSaverMode.Configure:
                ShowConfiguration();
                break;
        }

        return 0;
    }

    /// <summary>
    /// One full-screen window per monitor, mirroring the way the macOS
    /// ScreenSaver framework instantiates a view per display.
    /// </summary>
    private static void RunFullScreen()
    {
        var forms = new List<Form>();
        foreach (Screen screen in Screen.AllScreens)
        {
            forms.Add(ScreenSaverForm.CreateFullScreen(screen));
        }

        if (forms.Count == 0)
        {
            return;
        }

        Application.Run(new ApplicationContextWithForms([.. forms]));
    }

    /// <summary>
    /// Hockey_FightView returns NO from -hasConfigureSheet, so there is nothing to
    /// configure here either.
    /// </summary>
    private static void ShowConfiguration()
    {
        MessageBox.Show(
            "Hockey Fight has no settings.",
            "Hockey Fight",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static ScreenSaverMode ParseArguments(string[] args, out IntPtr targetHandle)
    {
        targetHandle = IntPtr.Zero;

        if (args.Length == 0)
        {
            // Windows launches with no arguments when the user picks "Settings"
            // from an older shell path.
            return ScreenSaverMode.Configure;
        }

        string first = args[0].Trim();
        if (first.Length < 2 || (first[0] != '/' && first[0] != '-'))
        {
            return ScreenSaverMode.Configure;
        }

        char switchChar = char.ToLowerInvariant(first[1]);

        // The handle may be glued on after a colon ("/p:1234") or arrive as the
        // next argument ("/p 1234").
        string inlineValue = first.Length > 2 && (first[2] == ':' || first[2] == '=')
            ? first[3..]
            : string.Empty;

        string handleText = inlineValue.Length > 0
            ? inlineValue
            : args.Length > 1 ? args[1].Trim() : string.Empty;

        targetHandle = ParseHandle(handleText);

        return switchChar switch
        {
            's' => ScreenSaverMode.FullScreen,
            'p' => ScreenSaverMode.Preview,
            'w' => ScreenSaverMode.Windowed,   // not a Windows switch; for development
            'c' => ScreenSaverMode.Configure,
            'a' => ScreenSaverMode.Configure,
            _ => ScreenSaverMode.Configure,
        };
    }

    private static IntPtr ParseHandle(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return IntPtr.Zero;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return (IntPtr)value;
        }

        return IntPtr.Zero;
    }
}

/// <summary>
/// Keeps the message loop alive until every window has closed, and closes the
/// remaining windows as soon as one of them exits.
/// </summary>
internal sealed class ApplicationContextWithForms : ApplicationContext
{
    private readonly List<Form> _forms;

    public ApplicationContextWithForms(params Form[] forms)
    {
        _forms = [.. forms];

        foreach (Form form in _forms)
        {
            form.FormClosed += OnFormClosed;
            form.Show();
        }

        _forms[0].Activate();
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        foreach (Form form in _forms)
        {
            if (!form.IsDisposed && form != sender)
            {
                form.Close();
            }
        }

        ExitThread();
    }
}
