using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Plicbox.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>Copy Image and Save Image As… for a picture in a shown message.</summary>
public static class MessagePictures
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The picture's bytes and type: decoded from the message for embedded pictures (data:), otherwise the copy the
    /// message view already downloaded, fetched again only if the view no longer has it.
    /// </summary>
    public static async Task<(byte[] Bytes, string Type)?> GetAsync(CoreWebView2 view, string source)
    {
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return FromDataUri(source);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            using var tree = JsonDocument.Parse(await view.CallDevToolsProtocolMethodAsync("Page.getResourceTree", "{}"));
            var frame = tree.RootElement.GetProperty("frameTree");
            var frameId = frame.GetProperty("frame").GetProperty("id").GetString();
            var type = frame.TryGetProperty("resources", out var resources)
                ? resources.EnumerateArray().Where(r => r.GetProperty("url").GetString() == source)
                    .Select(r => r.GetProperty("mimeType").GetString()).FirstOrDefault()
                : null;
            using var content = JsonDocument.Parse(await view.CallDevToolsProtocolMethodAsync("Page.getResourceContent",
                JsonSerializer.Serialize(new { frameId, url = source })));
            var text = content.RootElement.GetProperty("content").GetString() ?? "";
            var bytes = content.RootElement.GetProperty("base64Encoded").GetBoolean() ? Convert.FromBase64String(text) : System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 0) return (bytes, type ?? TypeFromName(uri.AbsolutePath));
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "The message view didn't have {Source}; downloading it", source);
        }
        using var response = await Http.GetAsync(uri);
        if (!response.IsSuccessStatusCode) return null;
        return (await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentType?.MediaType ?? TypeFromName(uri.AbsolutePath));
    }

    /// <summary>Puts the picture on the clipboard (PNG too, so transparency survives where apps take it). False if it can't be read.</summary>
    public static bool Copy(byte[] bytes)
    {
        if (Decode(bytes) is not { } bitmap) return false;
        var data = new DataObject();
        data.SetImage(bitmap);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        var stream = new MemoryStream();
        png.Save(stream);
        data.SetData("PNG", stream);
        Clipboard.SetDataObject(data, copy: true);
        return true;
    }

    /// <summary>Save Image As…: named after the picture's own file name when it has one.</summary>
    public static void SaveAs(byte[] bytes, string type, string source, Window? owner)
    {
        var extension = ExtensionFor(type);
        var name = source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? ""
            : Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        name = Dialogs.SafeFileName(string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(name)) ? L.T("picture") : name);
        if (!Path.HasExtension(name)) name += extension;
        var dialog = new SaveFileDialog
        {
            FileName = name,
            Filter = $"{L.T("Picture")} (*{Path.GetExtension(name)})|*{Path.GetExtension(name)}|{L.T("All files")}|*.*",
        };
        if (dialog.ShowDialog(owner) == true) File.WriteAllBytes(dialog.FileName, bytes);
    }

    private static BitmapSource? Decode(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            return null; // e.g. SVG, which WPF can't decode
        }
    }

    private static (byte[], string)? FromDataUri(string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0) return null;
        var header = uri[5..comma];
        var type = header.Split(';')[0] is { Length: > 0 } t ? t : "image/png";
        var payload = uri[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? (Convert.FromBase64String(payload), type)
                : (System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload)), type);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string TypeFromName(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };

    private static string ExtensionFor(string type) => type.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/bmp" => ".bmp",
        _ => ".png",
    };
}
