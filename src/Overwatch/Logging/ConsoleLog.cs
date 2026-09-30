namespace Overwatch.Logging;

public static class ConsoleLog
{
    private const int Capacity = 200;
    private static readonly Queue<LogEntry> Recent = new();
    private static long _nextId;

    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("attention", message);

    public static void Error(string message) => Write("erreur", message);

    public static LogEntry[] Since(long after)
    {
        lock (Recent)
            return Recent.Where(entry => entry.Id > after).ToArray();
    }

    private static void Write(string level, string message)
    {
        var entry = new LogEntry(Interlocked.Increment(ref _nextId), DateTimeOffset.Now, level, message);
        lock (Recent)
        {
            Recent.Enqueue(entry);
            while (Recent.Count > Capacity)
                Recent.Dequeue();
        }

        Console.WriteLine($"{entry.At:HH:mm:ss.fff}  {level,-10} {message}");
    }
}

public readonly record struct LogEntry(long Id, DateTimeOffset At, string Level, string Message);
