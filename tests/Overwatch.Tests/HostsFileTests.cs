using Overwatch.Config;
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

            Assert.Equal(HostsChange.Installed, manager.Install(new[] { entry }).Change);
            Assert.Equal(HostsChange.Unchanged, manager.Install(new[] { entry }).Change);
            var installed = File.ReadAllText(path);
            Assert.Equal(1, Count(installed, HostsFileManager.BeginMarker));
            Assert.Contains("127.0.0.1 jeu.exemple.invalid", installed, StringComparison.Ordinal);
            Assert.Contains("# conserve", installed, StringComparison.Ordinal);

            Assert.Equal(HostsChange.Removed, manager.Remove().Change);
            Assert.Equal(HostsChange.Unchanged, manager.Remove().Change);
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
            File.WriteAllText(path, "127.0.0.1 localhost\n" + HostsFileManager.BeginMarker + "\n10.9.8.7 residuel.exemple\n");
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

    [Fact]
    public void Read_only_attribute_is_cleared_for_the_write_then_restored()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-ro");
        var path = Path.Combine(directory.FullName, "hosts");
        var journalPath = Path.Combine(directory.FullName, "hosts.log");
        try
        {
            File.WriteAllText(path, "127.0.0.1 localhost\n");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            var manager = new HostsFileManager(path, new HostsJournal(journalPath));
            var attempt = manager.Install(new[] { HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1") });

            Assert.True(attempt.Ok);
            Assert.Contains(HostsFileManager.BeginMarker, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.True((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0);
            var journal = File.ReadAllText(journalPath);
            Assert.Contains("succès", journal, StringComparison.Ordinal);
            Assert.Contains("lecture seule", journal, StringComparison.Ordinal);
            Assert.Contains("restauré", journal, StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_swallowed_write_is_reported_and_does_not_throw()
    {
        var store = new MemoryStore { Content = "127.0.0.1 localhost\n", SwallowWrite = true };
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-silent");
        try
        {
            var journalPath = Path.Combine(directory.FullName, "hosts.log");
            var manager = new HostsFileManager(Path.Combine(directory.FullName, "hosts"), new HostsJournal(journalPath), store);
            var attempt = manager.Install(new[] { HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1") });

            Assert.False(attempt.Ok);
            Assert.Contains("échec silencieux", attempt.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("jeu.exemple.invalid", store.Content, StringComparison.Ordinal);
            Assert.Contains("échec", File.ReadAllText(journalPath), StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Access_denied_stays_a_result()
    {
        var store = new MemoryStore { Content = "127.0.0.1 localhost\n", ThrowOnWrite = true };
        var manager = new HostsFileManager("hosts-test", journal: null, store);
        var attempt = manager.Install(new[] { HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1") });

        Assert.False(attempt.Ok);
        Assert.Contains("accès refusé", attempt.Detail, StringComparison.Ordinal);
        Assert.Contains("aucune capture", HostsInstaller.ExplainFailure(attempt.Detail), StringComparison.Ordinal);
    }

    [Fact]
    public void A_legacy_block_is_replaced_by_the_current_markers()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-legacy");
        try
        {
            var path = Path.Combine(directory.FullName, "hosts");
            File.WriteAllText(path, "127.0.0.1 localhost\n# OVERWATCH-BEGIN\n10.9.8.7 residuel.exemple\n# OVERWATCH-END\n");
            var manager = new HostsFileManager(path);
            var attempt = manager.Install(new[] { HostsFileManager.Normalize("jeu.exemple.invalid", "127.0.0.1") });

            Assert.True(attempt.Ok);
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("residuel.exemple", text, StringComparison.Ordinal);
            Assert.DoesNotContain(HostsFileManager.LegacyBeginMarker, text, StringComparison.Ordinal);
            Assert.Contains(HostsFileManager.BeginMarker, text, StringComparison.Ordinal);
            Assert.Contains(HostsFileManager.EndMarker, text, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Opening_the_app_removes_a_block_left_by_a_killed_process()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-sweep");
        try
        {
            var path = Path.Combine(directory.FullName, "hosts");
            File.WriteAllText(path, "127.0.0.1 localhost\n" + HostsFileManager.BeginMarker + "\n127.0.0.1 jeu.exemple.invalid\n" + HostsFileManager.EndMarker + "\n");

            Assert.Null(HostsInstaller.SweepLeftover(path));
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(HostsFileManager.BeginMarker, text, StringComparison.Ordinal);
            Assert.Contains("127.0.0.1 localhost", text, StringComparison.Ordinal);
            Assert.Null(HostsInstaller.SweepLeftover(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_rights_do_not_throw_out_of_the_installer()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-hosts-degraded");
        try
        {
            var config = new AppConfig
            {
                Hosts =
                {
                    Enabled = true,
                    Path = directory.FullName,
                    Entries = { new HostsEntryConfig { Hostname = "jeu.exemple.invalid", Address = "127.0.0.1" } }
                }
            };

            var attached = HostsInstaller.Attach(config);
            Assert.Null(attached.Session);
            Assert.False(attached.Redirected);
            Assert.NotNull(attached.Warning);
            Assert.Contains("aucune capture", attached.Warning, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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

    private sealed class MemoryStore : IHostsStore
    {
        public string Content { get; set; } = "";

        public bool ReadOnly { get; set; }

        public bool SwallowWrite { get; set; }

        public bool ThrowOnWrite { get; set; }

        public bool Exists() => true;

        public string Read() => Content;

        public bool IsReadOnly() => ReadOnly;

        public void SetReadOnly(bool value) => ReadOnly = value;

        public void Write(string content)
        {
            if (ReadOnly)
                throw new UnauthorizedAccessException("lecture seule");
            if (ThrowOnWrite)
                throw new UnauthorizedAccessException("accès contrôlé");
            if (!SwallowWrite)
                Content = content;
        }
    }
}
