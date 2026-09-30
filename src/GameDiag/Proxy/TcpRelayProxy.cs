using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using GameDiag.Config;
using GameDiag.Decode;
using GameDiag.Logging;

namespace GameDiag.Proxy;

/// <summary>
/// Relais TCP de lecture seule. Chaque octet lu est écrit sur l'autre socket
/// avant d'être copié vers le décodeur. Aucun octet n'est ajouté, retiré ou réécrit.
/// </summary>
public sealed class TcpRelayProxy
{
    private readonly AppConfig _config;
    private readonly ObservationTap _tap;
    private readonly ConcurrentDictionary<string, int> _boundPorts = new();
    private readonly ConcurrentDictionary<Task, byte> _bridges = new();
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytesClientToServer;
    private long _bytesServerToClient;
    private int _activeConnections;

    public TcpRelayProxy(AppConfig config, ObservationTap tap)
    {
        _config = config;
        _tap = tap;
    }

    public Task Listening => _listening.Task;

    public long BytesClientToServer => Interlocked.Read(ref _bytesClientToServer);

    public long BytesServerToClient => Interlocked.Read(ref _bytesServerToClient);

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public int GetBoundPort(string listenerName)
    {
        if (_boundPorts.TryGetValue(listenerName, out var port))
            return port;
        throw new InvalidOperationException($"Le listener {listenerName} n'est pas en écoute.");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(_config.ListenAddress, out var address) || !IPAddress.IsLoopback(address))
        {
            var error = new ConfigException("L'écoute n'est autorisée que sur une adresse de bouclage.");
            _listening.TrySetException(error);
            throw error;
        }

        var listeners = new List<TcpListener>();
        try
        {
            foreach (var spec in _config.Listeners)
            {
                var listener = new TcpListener(address, spec.ListenPort);
                try
                {
                    listener.Start();
                }
                catch (SocketException exception)
                {
                    ConsoleLog.Error($"Impossible d'écouter sur {address}:{spec.ListenPort} ({exception.SocketErrorCode}).");
                    if (spec.ListenPort is 443 or 5555)
                        ConsoleLog.Error("Le client bascule entre 5555 et 443. Sans les deux, une partie des réseaux ne passe plus par le relay.");
                    else if (spec.ListenPort < 1024)
                        ConsoleLog.Error("Un port inférieur à 1024 demande des droits administrateur. Les autres listeners continuent.");
                    continue;
                }

                var bound = ((IPEndPoint)listener.LocalEndpoint).Port;
                _boundPorts[spec.Name] = bound;
                listeners.Add(listener);
                ConsoleLog.Info($"Écoute {address}:{bound} ({spec.Name}) → {spec.UpstreamHost}:{spec.UpstreamPort}");
                _ = AcceptLoopAsync(listener, spec, cancellationToken);
            }

            if (listeners.Count == 0)
            {
                var error = new ConfigException("Aucun port d'écoute n'a pu être ouvert.");
                _listening.TrySetException(error);
                throw error;
            }

            _listening.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            foreach (var listener in listeners)
                listener.Stop();

            var bridges = _bridges.Keys.ToArray();
            if (bridges.Length > 0)
            {
                try
                {
                    await Task.WhenAll(bridges).WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, ListenerConfig spec, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    break;
                }

                var bridge = BridgeAsync(client, spec, cancellationToken);
                _bridges.TryAdd(bridge, 0);
                _ = bridge.ContinueWith(
                    finished =>
                    {
                        _bridges.TryRemove(finished, out _);
                        if (finished.IsFaulted)
                            ConsoleLog.Error(finished.Exception?.GetBaseException().Message ?? "Session en erreur.");
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task BridgeAsync(TcpClient client, ListenerConfig spec, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activeConnections);
        using (client)
        {
            TcpClient? upstream = null;
            try
            {
                SocketSetup.Configure(client.Client);
                upstream = new TcpClient();
                SocketSetup.Configure(upstream.Client);
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await upstream.ConnectAsync(spec.UpstreamHost, spec.UpstreamPort, connectCts.Token);
                }
                catch (Exception exception) when (exception is SocketException or OperationCanceledException or TimeoutException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    ConsoleLog.Warn(
                        $"Upstream {spec.UpstreamHost}:{spec.UpstreamPort} injoignable ({exception.Message}). Fermeture sans écrire au client.");
                    return;
                }

                SocketSetup.Configure(upstream.Client);
                var connectionId = Guid.NewGuid().ToString("N")[..12];
                ConsoleLog.Info($"Session {connectionId} ({spec.Name}) vers {spec.UpstreamHost}:{spec.UpstreamPort}");

                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var clientStream = client.GetStream();
                var upstreamStream = upstream.GetStream();
                var clientToServer = PumpAsync(
                    clientStream,
                    upstreamStream,
                    Direction.ClientToServer,
                    connectionId,
                    spec.Name,
                    session);
                var serverToClient = PumpAsync(
                    upstreamStream,
                    clientStream,
                    Direction.ServerToClient,
                    connectionId,
                    spec.Name,
                    session);
                await Task.WhenAll(clientToServer, serverToClient);
                ConsoleLog.Info($"Session {connectionId} terminée");
            }
            finally
            {
                upstream?.Dispose();
                Interlocked.Decrement(ref _activeConnections);
            }
        }
    }

    private async Task PumpAsync(
        NetworkStream source,
        NetworkStream destination,
        Direction direction,
        string connectionId,
        string listenerName,
        CancellationTokenSource session)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        long sequence = 0;
        try
        {
            while (!session.IsCancellationRequested)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), session.Token);
                if (read == 0)
                {
                    try
                    {
                        destination.Socket.Shutdown(SocketShutdown.Send);
                    }
                    catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                    {
                    }

                    break;
                }

                // Écriture intégrale des octets reçus, sans rien y ajouter.
                // L'attente éventuelle est celle du tampon TCP du pair, pas du décodeur.
                await destination.WriteAsync(buffer.AsMemory(0, read), session.Token);

                if (direction == Direction.ClientToServer)
                    Interlocked.Add(ref _bytesClientToServer, read);
                else
                    Interlocked.Add(ref _bytesServerToClient, read);

                sequence++;
                _tap.Publish(connectionId, listenerName, direction, sequence, buffer.AsSpan(0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            session.Cancel();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
