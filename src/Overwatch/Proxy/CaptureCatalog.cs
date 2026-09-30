using System.Buffers.Binary;
using Overwatch.Decode;

namespace Overwatch.Proxy;

public sealed record CaptureDirectionView(
    string Direction,
    long Bytes,
    int Segments,
    int LargestSegment,
    string PrefixHex,
    bool LooksLikeTls);

public sealed record CaptureSessionView(string ConnectionId, string Listener, IReadOnlyList<CaptureDirectionView> Directions);

public static class CaptureCatalog
{
    public static IReadOnlyList<CaptureSessionView> List(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<CaptureSessionView>();

        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*.bin"))
        {
            var name = Path.GetFileName(path);
            var id = ConnectionIdOf(name);
            if (id is not null)
                ids.Add(id);
        }

        var sessions = new List<CaptureSessionView>();
        foreach (var id in ids)
        {
            var listenerPath = Path.Combine(directory, id + ".listener");
            var listener = File.Exists(listenerPath) ? File.ReadAllText(listenerPath).Trim() : "";
            var directions = new List<CaptureDirectionView>
            {
                Read(directory, id, Direction.ClientToServer),
                Read(directory, id, Direction.ServerToClient)
            };
            sessions.Add(new CaptureSessionView(id, listener, directions));
        }

        sessions.Reverse();
        return sessions;
    }

    public static string? Resolve(string directory, string connectionId, string direction)
    {
        if (connectionId.Length != 12 || connectionId.Any(character => character is < '0' or > '9' and < 'a' or > 'f'))
            return null;
        var suffix = direction switch
        {
            "c2s" => ".c2s.bin",
            "s2c" => ".s2c.bin",
            _ => null
        };
        if (suffix is null)
            return null;
        var path = Path.GetFullPath(Path.Combine(directory, connectionId + suffix));
        var root = Path.GetFullPath(directory);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return File.Exists(path) ? path : null;
    }

    private static CaptureDirectionView Read(string directory, string connectionId, Direction direction)
    {
        var label = direction == Direction.ClientToServer ? "c2s" : "s2c";
        var path = Path.Combine(directory, CaptureRecorder.FileName(connectionId, direction));
        if (!File.Exists(path))
            return new CaptureDirectionView(label, 0, 0, 0, "", false);

        byte[]? prefix = null;
        if (new FileInfo(path).Length > 0)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            prefix = new byte[Math.Min(24, stream.Length)];
            var read = stream.Read(prefix);
            if (read < prefix.Length)
                prefix = prefix[..read];
        }

        long bytes = 0;
        var segments = 0;
        var largest = 0;
        var indexPath = path + ".idx";
        if (File.Exists(indexPath))
        {
            using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = new byte[4];
            while (index.Read(length) == 4)
            {
                var size = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (size < 0)
                    break;
                bytes += size;
                segments++;
                if (size > largest)
                    largest = size;
            }
        }
        else
        {
            bytes = new FileInfo(path).Length;
            segments = bytes > 0 ? 1 : 0;
            largest = bytes > int.MaxValue ? int.MaxValue : (int)bytes;
        }

        return new CaptureDirectionView(
            label,
            bytes,
            segments,
            largest,
            prefix is null ? "" : Convert.ToHexString(prefix),
            LooksLikeTls(prefix));
    }

    public static bool LooksLikeTls(ReadOnlySpan<byte> prefix)
    {
        return prefix.Length >= 3
            && prefix[0] is >= 0x14 and <= 0x17
            && prefix[1] == 0x03
            && prefix[2] <= 0x04;
    }

    private static string? ConnectionIdOf(string fileName)
    {
        const string client = ".c2s.bin";
        const string server = ".s2c.bin";
        var suffix = fileName.EndsWith(client, StringComparison.Ordinal) ? client
            : fileName.EndsWith(server, StringComparison.Ordinal) ? server
            : null;
        if (suffix is null)
            return null;
        var id = fileName[..^suffix.Length];
        return id.Length == 12 ? id : null;
    }
}
