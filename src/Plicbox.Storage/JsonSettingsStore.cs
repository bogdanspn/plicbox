using System.Text.Json;
using Plicbox.Core;

namespace Plicbox.Storage;

/// <summary>App settings in a small JSON file (human-readable, survives database resets).</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonSettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox", "settings.json");

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, _path, overwrite: true);
        Current = settings;
        Changed?.Invoke(settings);
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new() : new();
        }
        catch (JsonException)
        {
            return new AppSettings(); // unreadable file: fall back to defaults rather than refusing to start
        }
    }
}
