namespace GameDiag.Logging;

public static class ConsoleLog
{
    public static void Info(string message) => Write("info", message);

    public static void Warn(string message) => Write("attention", message);

    public static void Error(string message) => Write("erreur", message);

    private static void Write(string level, string message)
    {
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff}  {level,-10} {message}");
    }
}
