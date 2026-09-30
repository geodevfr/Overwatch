using System.Text;
using Overwatch.Config;
using Overwatch.Decode;
using Overwatch.Hosts;
using Overwatch.Market;
using Overwatch.Persist;
using Overwatch.Watchdog;

namespace Overwatch.Tests;

public class MarketTests
{
    [Theory]
    [InlineData("Hell Mina", "hellmina")]
    [InlineData("hellmina", "hellmina")]
    [InlineData("  Hell   Mina ", "hellmina")]
    public void Server_names_drop_spaces_and_case(string raw, string expected)
    {
        Assert.Equal(expected, ServerNames.Normalize(raw));
    }

    [Fact]
    public void A_zero_lot_does_not_replace_a_real_price()
    {
        var book = new PriceBook();
        Assert.Equal(PriceDecision.Stored, book.TrySetLot("Hell Mina", 42, 10, 500, "a", out _));
        Assert.Equal(PriceDecision.Stored, book.TrySetLot("hellmina", 42, 1, 80, "a", out _));
        Assert.Equal(50, book.BestUnitPrice("Hell Mina", 42));

        Assert.Equal(PriceDecision.Ignored, book.TrySetLot("Hell Mina", 42, 1, 0, "b", out var ignored));
        Assert.Null(ignored);
        Assert.Equal(50, book.BestUnitPrice("hellmina", 42));
    }

    [Fact]
    public void An_uneven_division_is_not_rounded()
    {
        var book = new PriceBook();
        Assert.Equal(PriceDecision.Stored, book.TrySetLot("s", 7, 10, 25, "a", out var row));
        Assert.Null(row!.UnitPrice);
        Assert.Equal(25, row.Total);
        Assert.Null(book.BestUnitPrice("s", 7));
    }

    [Fact]
    public void The_same_price_seen_by_two_clients_is_stored_once()
    {
        var book = new PriceBook();
        Assert.Equal(PriceDecision.Stored, book.TrySetAverage("Hell Mina", 3, 1200, "fenetre-a", out _));
        Assert.Equal(PriceDecision.Duplicate, book.TrySetAverage("hellmina", 3, 1200, "fenetre-b", out _));
        Assert.Equal(1200, book.AveragePrice("Hell Mina", 3));
    }

    [Fact]
    public void A_zero_average_does_not_erase_the_previous_one()
    {
        var book = new PriceBook();
        book.TrySetAverage("s", 1, 40, "a", out _);
        Assert.Equal(PriceDecision.Ignored, book.TrySetAverage("s", 1, 0, "a", out _));
        Assert.Equal(40, book.AveragePrice("s", 1));
    }

    [Fact]
    public void Prices_wait_for_a_server_and_stay_on_that_connection()
    {
        var market = new SessionMarket();
        var rules = Rules();
        var averages = Frame(rules, "hdv_moyennes", AverageMessage(9, 1500));
        var first = market.Accept("client-a", Rule(rules, "hdv_moyennes"), averages);
        Assert.Empty(first.Prices);
        Assert.Null(market.Prices.AveragePrice("Hell Mina", 9));

        var server = Frame(rules, "serveur", ServerMessage("Hell Mina"));
        var named = market.Accept("client-a", Rule(rules, "serveur"), server);
        Assert.Equal("hellmina", named.Session!.ServerKey);
        Assert.Contains(named.Prices, price => price.ItemId == 9 && price.UnitPrice == 1500);

        var other = Frame(rules, "hdv_moyennes", AverageMessage(9, 10));
        market.Accept("client-b", Rule(rules, "hdv_moyennes"), other);
        Assert.Equal(1500, market.Prices.AveragePrice("Hell Mina", 9));
        Assert.Null(market.Session("client-b").ServerKey);
    }

    [Fact]
    public void Combat_state_does_not_cross_connections()
    {
        var market = new SessionMarket();
        var rules = Rules();
        market.Accept("a", Rule(rules, "combat"), Frame(rules, "combat", CombatMessage(1)));
        market.Accept("b", Rule(rules, "combat"), Frame(rules, "combat", CombatMessage(2)));

        Assert.Equal("round=1", market.Session("a").Combat);
        Assert.Equal("round=2", market.Session("b").Combat);
    }

    [Fact]
    public void A_bulk_packet_whose_count_does_not_fit_yields_no_price()
    {
        var rules = Rules();
        var broken = AverageMessage(9, 1500);
        broken[4] = 2;
        var reassembler = new FrameReassembler(65_536);
        var frames = reassembler.Push(broken, rules, Direction.ServerToClient, new ConversationState(), 64);
        Assert.DoesNotContain(frames, frame => frame.RuleId == "hdv_moyennes");
        Assert.True(reassembler.Withheld > 0 || reassembler.ResyncBytes > 0);
    }

    [Fact]
    public void Two_signatures_for_the_same_bytes_produce_no_value()
    {
        const string yaml = """
            rules:
              - id: un
                direction: s2c
                min_length: 4
                max_length: 4
                header_hex: "AA"
                extract:
                  - name: value
                    offset: 1
                    type: uint8
              - id: deux
                direction: s2c
                min_length: 4
                max_length: 4
                header_hex: "AA"
                extract:
                  - name: value
                    offset: 2
                    type: uint8
            """;
        var rules = RuleCompiler.Compile(RuleLoader.Parse(yaml));
        var reassembler = new FrameReassembler(65_536);
        var frames = reassembler.Push(new byte[] { 0xAA, 1, 2, 3 }, rules, Direction.ServerToClient, new ConversationState(), 16);
        Assert.Empty(frames);
        Assert.Equal(1, reassembler.Withheld);
        Assert.Equal(0, reassembler.Buffered);
    }

    [Fact]
    public void Window_titles_match_only_when_the_name_is_unique()
    {
        var people = new (string Id, string Name)[] { ("a", "Alpha"), ("b", "Beta") };
        var unique = WindowCorrelator.Match(people, new[] { "Alpha - serveur", "Beta" });
        Assert.Equal("Alpha - serveur", unique["a"]);
        Assert.Equal("Beta", unique["b"]);

        var ambiguous = WindowCorrelator.Match(people, new[] { "Alpha", "Alpha (2)" });
        Assert.False(ambiguous.ContainsKey("a"));
        Assert.False(WindowCorrelator.Match(new[] { ("a", "Alpha") }, new[] { "Alphabet" }).ContainsKey("a"));
    }

    [Fact]
    public void A_guessed_signature_on_an_unknown_version_never_matches()
    {
        const string yaml = """
            client: dofus3
            detection:
              - version: "3.6.11.13"
                status: unknown
                rules:
                  - id: note
                    enabled: true
                    direction: s2c
                    min_length: 2
                    max_length: 2
                    header_hex: "AB"
                    extract:
                      - name: mark
                        offset: 1
                        type: uint8
            """;

        var rules = RuleCompiler.Compile(RuleLoader.Parse(yaml));
        Assert.Empty(rules.Rules);
        Assert.Contains("3.6.11.13:note", rules.Withheld);

        var frames = new FrameReassembler(65_536).Push(
            new byte[] { 0xAB, 0x01 },
            rules,
            Direction.ServerToClient,
            new ConversationState(),
            16);
        Assert.Empty(frames);
    }

    [Fact]
    public void A_captured_version_activates_only_its_own_table_entries()
    {
        var unknown = """
            client: dofus3
            detection:
              - version: "3.6.9.9"
                status: unknown
                rules: []
              - version: "3.6.11.13"
                status: captured
                rules:
                  - id: note
                    direction: s2c
                    min_length: 2
                    max_length: 2
                    header_hex: "CD"
                    extract:
                      - name: mark
                        offset: 1
                        type: uint8
            """;

        var rules = RuleCompiler.Compile(RuleLoader.Parse(unknown));
        Assert.Equal(new[] { "3.6.9.9" }, rules.UnknownVersions);
        Assert.Equal(new[] { "3.6.11.13" }, rules.CapturedVersions);
        var rule = Assert.Single(rules.Rules);
        Assert.Equal("3.6.11.13:note", rule.Id);
        Assert.Equal("3.6.11.13", rule.ClientVersion);
    }

    [Fact]
    public void Named_client_rejects_signatures_outside_the_detection_table()
    {
        var yaml = """
            client: dofus3
            rules:
              - id: session_hello
                direction: c2s
                min_length: 1
                max_length: 1
                header_hex: "01"
            detection: []
            """;

        var error = Assert.Throws<ConfigException>(() => RuleCompiler.Compile(RuleLoader.Parse(yaml)));
        Assert.Contains("table detection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_names_the_slow_packet()
    {
        var watchdog = new DecoderWatchdog(TimeSpan.FromMilliseconds(300));
        watchdog.Record(TimeSpan.FromMilliseconds(10), "bruit");
        watchdog.Record(TimeSpan.FromMilliseconds(450), "hdv_moyennes");
        watchdog.Record(TimeSpan.FromMilliseconds(320), "autre");

        var snapshot = watchdog.SnapshotAndResetMax();
        Assert.Equal(2, snapshot.OverThreshold);
        Assert.Equal("hdv_moyennes", snapshot.Offender);
    }

    [Fact]
    public async Task Prices_are_flushed_as_a_batch()
    {
        var directory = Directory.CreateTempSubdirectory("overwatch-prices");
        try
        {
            var path = Path.Combine(directory.FullName, "prix.db");
            var store = new SqliteSink(path, 50, 20);
            var running = store.RunAsync();
            var row = new PriceRow("hellmina", 9, "average", 0, null, 1500, "client-a", DateTimeOffset.UnixEpoch);
            Assert.True(store.TryEnqueueMarket(new[] { row }, null));
            store.Complete();
            await running;

            var stored = Assert.Single(SqliteSink.ReadPrices(path));
            Assert.Equal(1500, stored.UnitPrice);
            Assert.Equal("hellmina", stored.ServerKey);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Cleanup_task_only_removes_the_hosts_block()
    {
        var plan = CleanupTasks.Plan("/usr/local/bin/overwatch", "/etc/hosts");
        Assert.Contains("--remove-hosts", plan, StringComparison.Ordinal);
        Assert.Contains("/etc/hosts", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("5555", plan, StringComparison.Ordinal);
    }

    private static RuleSet Rules()
    {
        const string yaml = """
            rules:
              - id: serveur
                kind: server_name
                name_field: server_name
                direction: s2c
                min_length: 12
                max_length: 12
                header_hex: "5356"
                extract:
                  - name: server_name
                    offset: 2
                    type: utf8
                    size: 10
              - id: hdv_moyennes
                kind: average_prices
                direction: s2c
                min_length: 6
                max_length: 4096
                header_hex: "4156"
                item_field: item_id
                value_field: average
                length_field:
                  offset: 2
                  size: 2
                  endian: little
                  bias: 0
                repeat:
                  count_offset: 4
                  count_size: 2
                  entry_offset: 6
                  entry_size: 8
                  fields:
                    - name: item_id
                      offset: 0
                      type: uint32
                    - name: average
                      offset: 4
                      type: uint32
              - id: combat
                kind: combat
                direction: s2c
                min_length: 3
                max_length: 3
                header_hex: "4342"
                extract:
                  - name: round
                    offset: 2
                    type: uint8
            """;
        return RuleCompiler.Compile(RuleLoader.Parse(yaml));
    }

    private static CompiledRule Rule(RuleSet rules, string id) => rules.Rules.Single(rule => rule.Id == id);

    private static ParsedFrame Frame(RuleSet rules, string id, byte[] message)
    {
        var frames = new FrameReassembler(65_536).Push(message, rules, Direction.ServerToClient, new ConversationState(), 64);
        return Assert.Single(frames, frame => frame.RuleId == id);
    }

    private static byte[] AverageMessage(uint itemId, uint average)
    {
        var message = new byte[14];
        message[0] = 0x41;
        message[1] = 0x56;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(2), 14);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(4), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(6), itemId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(10), average);
        return message;
    }

    private static byte[] ServerMessage(string name)
    {
        var message = new byte[12];
        message[0] = 0x53;
        message[1] = 0x56;
        Encoding.UTF8.GetBytes(name).CopyTo(message.AsSpan(2));
        return message;
    }

    private static byte[] CombatMessage(byte round) => new byte[] { 0x43, 0x42, round };

    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
