using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Plicbox.Core;
using Plicbox.Mail;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace Plicbox.App.Services;

/// <summary>
/// Pictures for people: your own accounts' chosen pictures, and senders' pictures found online by <see cref="SenderPictures"/>.
/// The coloured letter stays the default whenever there is nothing (or nothing trustworthy) to show.
/// </summary>
public sealed class Pictures(SenderPictures senders, ISettingsStore settings, string accountPictureDir)
{
    private const int AccountPictureSize = 160;
    private readonly ConcurrentDictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    // Pictures in the order they were loaded: past MaxCached, the oldest are dropped (they reload from disk when needed),
    // so a long session browsing thousands of senders doesn't keep every decoded picture.
    private readonly ConcurrentQueue<string> _loadOrder = new();
    private const int MaxCached = 600;

    public string AccountPicturePath(long accountId) => Path.Combine(accountPictureDir, $"account-{accountId}.png");

    /// <summary>The picture chosen for one of your accounts, or null.</summary>
    public ImageSource? ForAccount(long accountId)
    {
        var path = AccountPicturePath(accountId);
        return File.Exists(path) ? Load(path) : null;
    }

    /// <summary>Stores a picture for an account: cropped to a centred square and scaled down, so any photo will do.</summary>
    public void SetAccountPicture(long accountId, string sourceFile)
    {
        var source = new BitmapImage();
        source.BeginInit();
        source.UriSource = new Uri(sourceFile);
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.EndInit();
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var scale = Math.Min(1.0, AccountPictureSize / (double)side);
        var scaled = new TransformedBitmap(square, new ScaleTransform(scale, scale));

        Directory.CreateDirectory(accountPictureDir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));
        var path = AccountPicturePath(accountId);
        using (var file = File.Create(path)) encoder.Save(file);
        _byPath.TryRemove(path, out _);
    }

    public void RemoveAccountPicture(long accountId)
    {
        var path = AccountPicturePath(accountId);
        if (File.Exists(path)) File.Delete(path);
        _byPath.TryRemove(path, out _);
    }

    /// <summary>
    /// A sender's picture: your account's own picture for your addresses; otherwise one found online, when enabled in Settings.
    /// </summary>
    /// <param name="ownAccounts">Your addresses -> account id.</param>
    /// <param name="mayLookUp">False for mail in Junk: never contact a spammer's servers.</param>
    public async Task<ImageSource?> ForSenderAsync(string address, IReadOnlyDictionary<string, long> ownAccounts, bool mayLookUp)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (ownAccounts.TryGetValue(address.Trim(), out var accountId)) return ForAccount(accountId);
        if (!settings.Current.SenderPictures || !mayLookUp) return null;
        // The cache check touches the disk: off the UI thread (a folder switch looks up every sender in the list).
        var path = await Task.Run(() => senders.FindAsync(address));
        return path is null ? null : Load(path);
    }

    /// <summary>Dev aid (--dev-perf): pictures decoded so far, and the time it took.</summary>
    internal static int Decoded;
    internal static readonly System.Diagnostics.Stopwatch DecodeTime = new();

    private ImageSource? Load(string path)
    {
        if (_byPath.TryGetValue(path, out var cached)) return cached;
        Decoded++;
        DecodeTime.Start();
        var image = Decode(path);
        DecodeTime.Stop();
        if (_byPath.TryAdd(path, image)) _loadOrder.Enqueue(path);
        while (_byPath.Count > MaxCached && _loadOrder.TryDequeue(out var oldest)) _byPath.TryRemove(oldest, out _);
        return image;
    }

    private static ImageSource? Decode(string p)
    {
        try
        {
            if (p.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                if (!SenderPictures.IsSafeSvg(File.ReadAllText(p))) return null;
                var drawing = new FileSvgReader(new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true }).Read(p);
                if (drawing is null) return null;
                var svg = new DrawingImage(drawing);
                svg.Freeze();
                return svg;
            }
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(p);
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file locked
            image.DecodePixelWidth = 96;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException
                                       or System.Xml.XmlException or UnauthorizedAccessException)
        {
            Serilog.Log.Debug(ex, "Picture {Path} couldn't be loaded", p);
            return null;
        }
    }
}
