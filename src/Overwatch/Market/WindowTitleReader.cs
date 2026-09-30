using System.Diagnostics;

namespace Overwatch.Market;

/// <summary>
/// Titres déjà publiés par le gestionnaire de fenêtres. Désactivé par défaut.
/// N'ouvre aucun processus et ne lit aucune mémoire de programme.
/// </summary>
public static class WindowTitleReader
{
    public static IReadOnlyList<string> Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return ReadWindows();
            return ReadWmctrl();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ReadWmctrl()
    {
        var start = new ProcessStartInfo
        {
            FileName = "wmctrl",
            Arguments = "-l",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(start);
        if (process is null)
            return Array.Empty<string>();
        if (!process.WaitForExit(500))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            return Array.Empty<string>();
        }

        var titles = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || string.IsNullOrWhiteSpace(parts[3]))
                continue;
            titles.Add(parts[3]);
        }

        return titles;
    }

    private static IReadOnlyList<string> ReadWindows()
    {
        var titles = new List<string>();
        EnumWindows((handle, _) =>
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0)
                return true;
            var buffer = new char[length + 1];
            _ = GetWindowText(handle, buffer, buffer.Length);
            var title = new string(buffer, 0, length).Trim();
            if (title.Length > 0)
                titles.Add(title);
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, char[] buffer, int capacity);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);
}
