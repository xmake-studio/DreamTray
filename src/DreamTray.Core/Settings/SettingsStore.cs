using System.Text.Json;
using System.Text.Json.Nodes;

namespace DreamTray.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. Saves are debounced and written
/// through a temp file, so dragging a slider does not hammer the disk and a crash
/// mid-write cannot leave a truncated config behind.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly System.Threading.Timer _debounce;
    private readonly Action<string> _log;
    private bool _dirty;

    public AppSettings Current { get; private set; } = new();

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamTray");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public SettingsStore(Action<string> log)
    {
        _log = log;
        Load();
        _debounce = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options)
                          ?? new AppSettings();
                return;
            }
        }
        catch (Exception ex)
        {
            // A corrupt file should not wipe the user's setup silently: keep a copy.
            _log($"settings unreadable ({ex.Message}); starting fresh");
            try { File.Move(FilePath, FilePath + ".bad", overwrite: true); } catch { }
        }
        Current = new AppSettings();
    }

    /// <summary>Mark dirty; the file is written ~1 s later on a timer thread.</summary>
    public void Save()
    {
        lock (_gate) _dirty = true;
        _debounce.Change(1000, Timeout.Infinite);
    }

    /// <summary>Write immediately (shutdown path).</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            try
            {
                Directory.CreateDirectory(Folder);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { _log($"settings save failed: {ex.Message}"); }
        }
    }

    /// <summary>A storage view over one JSON bag inside the document.</summary>
    public IStorage Scope(JsonObject bag) => new JsonStorage(bag, this);

    public void Dispose()
    {
        _debounce.Dispose();
        Flush();
    }
}

/// <summary>
/// <see cref="IStorage"/> over a <see cref="JsonObject"/> owned by the settings
/// document. Reads are typed and forgiving: a missing key, a null, or a value of
/// the wrong shape all yield the caller's fallback rather than throwing, so a
/// hand-edited config can never crash a widget.
/// </summary>
internal sealed class JsonStorage(JsonObject bag, SettingsStore store) : IStorage
{
    public T Get<T>(string key, T fallback)
    {
        try
        {
            if (!bag.TryGetPropertyValue(key, out var node) || node is null) return fallback;
            return node.Deserialize<T>() ?? fallback;
        }
        catch { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        try { bag[key] = value is null ? null : JsonSerializer.SerializeToNode(value); }
        catch { return; }
        store.Save();
    }

    public void Save() => store.Flush();
}
