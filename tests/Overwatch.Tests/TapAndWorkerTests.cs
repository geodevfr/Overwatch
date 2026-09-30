using Overwatch.Decode;
using Overwatch.Persist;
using Overwatch.Proxy;
using Overwatch.Sample;
using Overwatch.SelfTest;
using Overwatch.Watchdog;

namespace Overwatch.Tests;

public class TapAndWorkerTests
{
    [Fact]
    public void Publish_stays_bounded_when_nobody_reads()
    {
        var tap = new ObservationTap(4);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var payload = new byte[128];
        for (var i = 0; i < 1000; i++)
            tap.Publish("c", "essai", Direction.ClientToServer, i + 1, payload);
        started.Stop();

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(4, tap.PendingCount);
        Assert.Equal(996, tap.DroppedChunks);
    }

    [Fact]
    public async Task Reader_and_writer_finish_without_deadlock()
    {
        var tap = new ObservationTap(8);
        var received = 0;
        var reader = Task.Run(async () =>
        {
            await foreach (var _ in tap.ReadAllAsync())
                Interlocked.Increment(ref received);
        });

        var payload = new byte[8];
        for (var i = 0; i < 500; i++)
            tap.Publish("c", "essai", Direction.ClientToServer, i + 1, payload);
        tap.Complete();

        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(tap.PublishedChunks, received + tap.DroppedChunks);
        Assert.True(received > 0);
    }

    [Fact]
    public async Task A_sequence_gap_discards_the_partial_frame()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-gap");
        try
        {
            var tap = new ObservationTap(8);
            var database = Path.Combine(directory.FullName, "gap.db");
            var store = new SqliteSink(database, 50, 10);
            var storeTask = store.RunAsync();
            var rules = RuleCatalog.Create(RuleCompiler.Compile(RuleLoader.Parse(SampleRules.Yaml)));
            var worker = new DecodeWorker(tap, rules, store, new DecoderWatchdog(TimeSpan.FromMilliseconds(50)), 65_536, 64);
            var workerTask = worker.RunAsync();

            var hello = FictionalProtocol.Hello(0x1234, 2, 3);
            tap.Publish("c1", "essai", Direction.ClientToServer, 1, hello.AsSpan(0, 4));
            tap.Publish("c1", "essai", Direction.ClientToServer, 3, hello);
            tap.Complete();
            await workerTask;
            store.Complete();
            await storeTask;

            var rows = SqliteSink.ReadAll(database);
            var row = Assert.Single(rows);
            using var fields = System.Text.Json.JsonDocument.Parse(row.FieldsJson);
            Assert.Equal("4660", fields.RootElement.GetProperty("proto").GetString());
            Assert.Equal("2", fields.RootElement.GetProperty("seq").GetString());
            Assert.True(worker.Gaps >= 1);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Watchdog_remembers_the_slowest_sample()
    {
        var watchdog = new DecoderWatchdog(TimeSpan.FromMilliseconds(50));
        watchdog.Record(TimeSpan.FromMilliseconds(10));
        watchdog.Record(TimeSpan.FromMilliseconds(80));

        var snapshot = watchdog.SnapshotAndResetMax();

        Assert.Equal(2, snapshot.Samples);
        Assert.Equal(1, snapshot.OverThreshold);
        Assert.Equal("inconnu", snapshot.Offender);
        Assert.True(snapshot.MaxSincePreviousSnapshot >= TimeSpan.FromMilliseconds(80));
        Assert.Equal(TimeSpan.Zero, watchdog.SnapshotAndResetMax().MaxSincePreviousSnapshot);
    }
}
