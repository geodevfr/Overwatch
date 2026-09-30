using Overwatch.Hosts;

namespace Overwatch.Tests;

public class HostsFileTests
{
    [Fact]
    public void Install_is_idempotent_and_remove_restores_the_file()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-unit");
        try
        {
            var path = Path.Combine(directory.FullName, "hosts");
            const string original = "127.0.0.1 localhost\n# conserve\n";
            File.WriteAllText(path, original);
            var manager = new HostsFileManager(path);
            var entry = HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1");

            Assert.Equal(HostsChange.Installed, manager.Install(new[] { entry }));
            Assert.Equal(HostsChange.Unchanged, manager.Install(new[] { entry }));
            var installed = File.ReadAllText(path);
            Assert.Equal(1, Count(installed, HostsFileManager.BeginMarker));
            Assert.Contains("127.0.0.1 jeu.exemple.invalid", installed, StringComparison.Ordinal);
            Assert.Contains("# conserve", installed, StringComparison.Ordinal);

            Assert.Equal(HostsChange.Removed, manager.Remove());
            Assert.Equal(HostsChange.Unchanged, manager.Remove());
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_block_without_end_marker_is_replaced()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-crash");
        try
        {
            var path = Path.Combine(directory.FullName, "hosts");
            File.WriteAllText(path, "127.0.0.1 localhost\n# OVERWATCH-BEGIN\n10.9.8.7 residuel.exemple\n");
            var manager = new HostsFileManager(path);
            manager.Install(new[] { HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1") });

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("residuel.exemple", text, StringComparison.Ordinal);
            Assert.Equal(1, Count(text, HostsFileManager.BeginMarker));
            Assert.Equal(1, Count(text, HostsFileManager.EndMarker));
            Assert.Contains("127.0.0.1 localhost", text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("jeu.exemple.invalid", true)]
    [InlineData("mauvais nom", false)]
    [InlineData("jeu.exemple\n10.0.0.1 pirate", false)]
    [InlineData("-mauvais", false)]
    public void Hostnames_cannot_inject_extra_lines(string hostname, bool accepted)
    {
        Assert.Equal(accepted, HostsFileManager.IsSafeHostname(hostname));
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
