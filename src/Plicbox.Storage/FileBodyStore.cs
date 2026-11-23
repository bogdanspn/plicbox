using Plicbox.Core;

namespace Plicbox.Storage;

public sealed class FileBodyStore(string root) : IBodyStore
{
    /// <summary>The folder the messages are kept in.</summary>
    public string Root => root;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox", "bodies");

    public async Task SaveAsync(long messageId, Stream eml, CancellationToken ct = default)
    {
        var path = PathFor(messageId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var file = File.Create(tmp))
        {
            await eml.CopyToAsync(file, ct);
            file.Flush(flushToDisk: true); // else a power cut can keep the rename but lose the contents
        }
        File.Move(tmp, path, overwrite: true);
    }

    public Stream? Open(long messageId)
    {
        var path = PathFor(messageId);
        if (!File.Exists(path)) return null;
        var stream = File.OpenRead(path);
        if (stream.ReadByte() > 0) { stream.Position = 0; return stream; }
        // Empty, or full size but all zeros (what a crash before the disk caught up leaves): no real message starts
        // with a zero byte. Dropped so it counts as not downloaded and is fetched again.
        stream.Dispose();
        try { File.Delete(path); } catch (IOException) { } // still open elsewhere: the next Open tries again
        return null;
    }

    public void Delete(long messageId) => File.Delete(PathFor(messageId));

    // Bucket by thousands so no directory grows unbounded.
    private string PathFor(long id) => Path.Combine(root, (id / 1000).ToString(), $"{id}.eml");
}
