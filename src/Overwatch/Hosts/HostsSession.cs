using Overwatch.Logging;

namespace Overwatch.Hosts;

public sealed class HostsSession : IDisposable
{
    private readonly HostsFileManager _manager;
    private int _disposed;

    public HostsSession(HostsFileManager manager)
    {
        _manager = manager;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            var change = _manager.Remove();
            ConsoleLog.Info($"Fichier hosts nettoyé ({Describe(change)}).");
        }
        catch (Exception exception)
        {
            ConsoleLog.Error($"Nettoyage du fichier hosts impossible : {exception.Message}");
            ConsoleLog.Error("Relancez avec les droits administrateur : overwatch --remove-hosts");
        }
    }

    public static string Describe(HostsChange change) => change switch
    {
        HostsChange.Installed => "entrées installées",
        HostsChange.Removed => "bloc retiré",
        _ => "inchangé"
    };
}
