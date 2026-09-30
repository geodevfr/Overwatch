namespace Overwatch.Tests;

public class PassiveBoundaryTests
{
    private static readonly string[] Forbidden =
    {
        "ReadProcessMemory",
        "WriteProcessMemory",
        "OpenProcess",
        "NtReadVirtualMemory",
        "NtWriteVirtualMemory",
        "CreateRemoteThread",
        "VirtualAllocEx",
        "VirtualProtectEx",
        "CreateToolhelp32Snapshot",
        "EnumProcessModules",
        "Module32First",
        "SendInput",
        "SendMessage",
        "PostMessage",
        "SetWindowsHookEx",
        "mouse_event",
        "keybd_event",
        "LoadLibrary",
        "SslStream",
        "AuthenticateAsClient",
        "RemoteCertificateValidationCallback",
        "X509Certificate",
        "HttpClient",
        "HttpRequestMessage"
    };

    [Fact]
    public void The_program_does_not_touch_the_game_process_or_call_home()
    {
        var root = RepoDirectory("src/Overwatch");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var token in Forbidden)
            {
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_only_native_calls_read_public_window_titles()
    {
        var root = RepoDirectory("src/Overwatch");
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("DllImport", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        var file = Assert.Single(files);
        Assert.Equal("WindowTitleReader.cs", file);
        var text = File.ReadAllText(Path.Combine(root, "Market", "WindowTitleReader.cs"));
        Assert.Contains("EnumWindows", text, StringComparison.Ordinal);
        Assert.Contains("GetWindowText", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kernel32", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoDirectory(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(relative);
    }
}
