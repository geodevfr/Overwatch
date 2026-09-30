namespace Overwatch.Config;

public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message)
    {
    }
}
