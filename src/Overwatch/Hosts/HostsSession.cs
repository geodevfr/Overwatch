namespace Overwatch.Hosts;

public sealed class HostsSession : IDisposable
{
    private readonly HostsFileManager _manager;
    private int _disposed;

    public HostsSession(HostsFileManager manager)
    {
        _manager = manager;
    }

    public HostsAttempt Release()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return HostsAttempt.Success(HostsChange.Unchanged, "Bloc hosts déjà retiré.");
        return _manager.Remove();
    }

    public void Dispose() => Release();

    public static string Describe(HostsChange change) => change switch
    {
        HostsChange.Installed => "entrées installées",
        HostsChange.Removed => "bloc retiré",
        _ => "inchangé"
    };
}
