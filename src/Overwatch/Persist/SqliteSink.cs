using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Overwatch.Persist;

/// <summary>
/// Insertions groupées sur un thread dédié. TryEnqueue ne bloque pas :
/// une file pleine abandonne l'observation.
/// </summary>
public sealed class SqliteSink
{
    private readonly string _path;
    private readonly int _flushIntervalMs;
    private readonly int _batchSize;
    private readonly Channel<StoredWork> _channel;
    private long _dropped;
    private long _written;

    public SqliteSink(string path, int flushIntervalMs, int batchSize, int capacity = 8192)
    {
        _path = path;
        _flushIntervalMs = flushIntervalMs;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<StoredWork>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public string Path => _path;

    public long Dropped => Interlocked.Read(ref _dropped);

    public long Written => Interlocked.Read(ref _written);

    public int Pending => _channel.Reader.Count;

    public bool TryEnqueue(Observation observation) => TryWrite(new StoredWork { Observation = observation });

    public bool TryEnqueueMarket(IReadOnlyList<Overwatch.Market.PriceRow> prices, SessionRow? session)
    {
        if (prices.Count == 0 && session is null)
            return true;
        return TryWrite(new StoredWork { Prices = prices, Session = session });
    }

    private bool TryWrite(StoredWork work)
    {
        if (_channel.Writer.TryWrite(work))
            return true;
        Interlocked.Increment(ref _dropped);
        return false;
    }

    public void Complete() => _channel.Writer.TryComplete();

    public async Task RunAsync()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA busy_timeout=2000;
                """;
            await pragma.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS observations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    observed_at TEXT NOT NULL,
                    connection_id TEXT NOT NULL,
                    listener TEXT NOT NULL,
                    direction TEXT NOT NULL,
                    rule_id TEXT NOT NULL,
                    length INTEGER NOT NULL,
                    fields_json TEXT NOT NULL,
                    payload_hex TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        await using (var prices = connection.CreateCommand())
        {
            prices.CommandText = """
                CREATE TABLE IF NOT EXISTS prices (
                    server_key TEXT NOT NULL,
                    item_id INTEGER NOT NULL,
                    source TEXT NOT NULL,
                    lot_quantity INTEGER NOT NULL,
                    total INTEGER,
                    unit_price INTEGER,
                    connection_id TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (server_key, item_id, source, lot_quantity)
                );
                CREATE TABLE IF NOT EXISTS sessions (
                    connection_id TEXT PRIMARY KEY,
                    server_key TEXT,
                    character_name TEXT,
                    window_title TEXT,
                    position TEXT,
                    combat TEXT,
                    updated_at TEXT NOT NULL
                );
                """;
            await prices.ExecuteNonQueryAsync();
        }

        var reader = _channel.Reader;
        var batch = new List<StoredWork>(_batchSize);
        while (true)
        {
            using var window = new CancellationTokenSource(_flushIntervalMs);
            while (batch.Count < _batchSize)
            {
                if (reader.TryRead(out var item))
                {
                    batch.Add(item);
                    continue;
                }

                if (reader.Completion.IsCompleted)
                    break;

                try
                {
                    if (!await reader.WaitToReadAsync(window.Token))
                        break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (batch.Count > 0)
                Flush(connection, batch);

            if (reader.Completion.IsCompleted)
            {
                while (reader.TryRead(out var rest))
                    batch.Add(rest);
                if (batch.Count > 0)
                    Flush(connection, batch);
                break;
            }
        }
    }

    public static List<Observation> ReadAll(string path)
    {
        var rows = new List<Observation>();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_at, connection_id, listener, direction, rule_id, length, fields_json, payload_hex
            FROM observations
            ORDER BY id;
            """;
        using var result = command.ExecuteReader();
        while (result.Read())
        {
            rows.Add(new Observation(
                DateTimeOffset.Parse(result.GetString(0), CultureInfo.InvariantCulture),
                result.GetString(1),
                result.GetString(2),
                result.GetString(3),
                result.GetString(4),
                result.GetInt32(5),
                result.GetString(6),
                result.GetString(7)));
        }

        return rows;
    }

    public static List<Overwatch.Market.PriceRow> ReadPrices(string path)
    {
        var rows = new List<Overwatch.Market.PriceRow>();
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT server_key, item_id, source, lot_quantity, total, unit_price, connection_id, updated_at
            FROM prices
            ORDER BY server_key, item_id, source, lot_quantity;
            """;
        using var result = command.ExecuteReader();
        while (result.Read())
        {
            rows.Add(new Overwatch.Market.PriceRow(
                result.GetString(0),
                result.GetInt64(1),
                result.GetString(2),
                result.GetInt32(3),
                result.IsDBNull(4) ? null : result.GetInt64(4),
                result.IsDBNull(5) ? null : result.GetInt64(5),
                result.GetString(6),
                DateTimeOffset.Parse(result.GetString(7), CultureInfo.InvariantCulture)));
        }

        return rows;
    }

    private void Flush(SqliteConnection connection, List<StoredWork> batch)
    {
        using var transaction = connection.BeginTransaction();
        using var observations = connection.CreateCommand();
        observations.Transaction = transaction;
        observations.CommandText = """
            INSERT INTO observations
                (observed_at, connection_id, listener, direction, rule_id, length, fields_json, payload_hex)
            VALUES
                ($at, $conn, $listener, $dir, $rule, $len, $fields, $payload);
            """;
        var at = observations.Parameters.Add("$at", SqliteType.Text);
        var conn = observations.Parameters.Add("$conn", SqliteType.Text);
        var listener = observations.Parameters.Add("$listener", SqliteType.Text);
        var direction = observations.Parameters.Add("$dir", SqliteType.Text);
        var rule = observations.Parameters.Add("$rule", SqliteType.Text);
        var length = observations.Parameters.Add("$len", SqliteType.Integer);
        var fields = observations.Parameters.Add("$fields", SqliteType.Text);
        var payload = observations.Parameters.Add("$payload", SqliteType.Text);

        using var prices = connection.CreateCommand();
        prices.Transaction = transaction;
        prices.CommandText = """
            INSERT INTO prices
                (server_key, item_id, source, lot_quantity, total, unit_price, connection_id, updated_at)
            VALUES
                ($server, $item, $source, $qty, $total, $unit, $conn, $at)
            ON CONFLICT(server_key, item_id, source, lot_quantity) DO UPDATE SET
                total = excluded.total,
                unit_price = excluded.unit_price,
                connection_id = excluded.connection_id,
                updated_at = excluded.updated_at;
            """;
        var priceServer = prices.Parameters.Add("$server", SqliteType.Text);
        var priceItem = prices.Parameters.Add("$item", SqliteType.Integer);
        var priceSource = prices.Parameters.Add("$source", SqliteType.Text);
        var priceQty = prices.Parameters.Add("$qty", SqliteType.Integer);
        var priceTotal = prices.Parameters.Add("$total", SqliteType.Integer);
        var priceUnit = prices.Parameters.Add("$unit", SqliteType.Integer);
        var priceConn = prices.Parameters.Add("$conn", SqliteType.Text);
        var priceAt = prices.Parameters.Add("$at", SqliteType.Text);

        using var sessions = connection.CreateCommand();
        sessions.Transaction = transaction;
        sessions.CommandText = """
            INSERT INTO sessions
                (connection_id, server_key, character_name, window_title, position, combat, updated_at)
            VALUES
                ($conn, $server, $character, $title, $position, $combat, $at)
            ON CONFLICT(connection_id) DO UPDATE SET
                server_key = excluded.server_key,
                character_name = excluded.character_name,
                window_title = excluded.window_title,
                position = excluded.position,
                combat = excluded.combat,
                updated_at = excluded.updated_at;
            """;
        var sessionConn = sessions.Parameters.Add("$conn", SqliteType.Text);
        var sessionServer = sessions.Parameters.Add("$server", SqliteType.Text);
        var sessionCharacter = sessions.Parameters.Add("$character", SqliteType.Text);
        var sessionTitle = sessions.Parameters.Add("$title", SqliteType.Text);
        var sessionPosition = sessions.Parameters.Add("$position", SqliteType.Text);
        var sessionCombat = sessions.Parameters.Add("$combat", SqliteType.Text);
        var sessionAt = sessions.Parameters.Add("$at", SqliteType.Text);

        var written = 0;
        foreach (var work in batch)
        {
            if (work.Observation is { } observation)
            {
                at.Value = observation.ObservedAt.ToString("O");
                conn.Value = observation.ConnectionId;
                listener.Value = observation.Listener;
                direction.Value = observation.Direction;
                rule.Value = observation.RuleId;
                length.Value = observation.Length;
                fields.Value = observation.FieldsJson;
                payload.Value = observation.PayloadHex;
                observations.ExecuteNonQuery();
                written++;
            }

            if (work.Prices is not null)
            {
                foreach (var price in work.Prices)
                {
                    priceServer.Value = price.ServerKey;
                    priceItem.Value = price.ItemId;
                    priceSource.Value = price.Source;
                    priceQty.Value = price.LotQuantity;
                    priceTotal.Value = (object?)price.Total ?? DBNull.Value;
                    priceUnit.Value = (object?)price.UnitPrice ?? DBNull.Value;
                    priceConn.Value = price.ConnectionId;
                    priceAt.Value = price.UpdatedAt.ToString("O");
                    prices.ExecuteNonQuery();
                    written++;
                }
            }

            if (work.Session is { } session)
            {
                sessionConn.Value = session.ConnectionId;
                sessionServer.Value = (object?)session.ServerKey ?? DBNull.Value;
                sessionCharacter.Value = (object?)session.CharacterName ?? DBNull.Value;
                sessionTitle.Value = (object?)session.WindowTitle ?? DBNull.Value;
                sessionPosition.Value = (object?)session.Position ?? DBNull.Value;
                sessionCombat.Value = (object?)session.Combat ?? DBNull.Value;
                sessionAt.Value = session.UpdatedAt.ToString("O");
                sessions.ExecuteNonQuery();
                written++;
            }
        }

        transaction.Commit();
        Interlocked.Add(ref _written, written);
        batch.Clear();
    }
}
