using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace GameDiag.Persist;

/// <summary>
/// Insertions groupées sur un thread dédié. TryEnqueue ne bloque pas :
/// une file pleine abandonne l'observation.
/// </summary>
public sealed class SqliteSink
{
    private readonly string _path;
    private readonly int _flushIntervalMs;
    private readonly int _batchSize;
    private readonly Channel<Observation> _channel;
    private long _dropped;
    private long _written;

    public SqliteSink(string path, int flushIntervalMs, int batchSize, int capacity = 8192)
    {
        _path = path;
        _flushIntervalMs = flushIntervalMs;
        _batchSize = batchSize;
        _channel = Channel.CreateBounded<Observation>(new BoundedChannelOptions(capacity)
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

    public bool TryEnqueue(Observation observation)
    {
        if (_channel.Writer.TryWrite(observation))
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

        var reader = _channel.Reader;
        var batch = new List<Observation>(_batchSize);
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

    private void Flush(SqliteConnection connection, List<Observation> batch)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observations
                (observed_at, connection_id, listener, direction, rule_id, length, fields_json, payload_hex)
            VALUES
                ($at, $conn, $listener, $dir, $rule, $len, $fields, $payload);
            """;
        var at = command.Parameters.Add("$at", SqliteType.Text);
        var conn = command.Parameters.Add("$conn", SqliteType.Text);
        var listener = command.Parameters.Add("$listener", SqliteType.Text);
        var direction = command.Parameters.Add("$dir", SqliteType.Text);
        var rule = command.Parameters.Add("$rule", SqliteType.Text);
        var length = command.Parameters.Add("$len", SqliteType.Integer);
        var fields = command.Parameters.Add("$fields", SqliteType.Text);
        var payload = command.Parameters.Add("$payload", SqliteType.Text);

        foreach (var observation in batch)
        {
            at.Value = observation.ObservedAt.ToString("O");
            conn.Value = observation.ConnectionId;
            listener.Value = observation.Listener;
            direction.Value = observation.Direction;
            rule.Value = observation.RuleId;
            length.Value = observation.Length;
            fields.Value = observation.FieldsJson;
            payload.Value = observation.PayloadHex;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        Interlocked.Add(ref _written, batch.Count);
        batch.Clear();
    }
}
