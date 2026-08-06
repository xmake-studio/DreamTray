using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DreamTray.Settings;

/// <summary>
/// Everything DreamTray persists, as one JSON document in
/// <c>%APPDATA%\DreamTray\settings.json</c>.
///
/// Widget and plugin settings are kept as free-form <see cref="JsonObject"/> bags
/// rather than typed properties: the app must be able to load a config containing
/// settings for a plugin that is not installed right now without dropping them.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a migration is needed; unread today, written for the future.</summary>
    public int Version { get; set; } = 1;

    public string Theme { get; set; } = "System";     // System | Light | Dark

    public TdpSettings Tdp { get; set; } = new();

    /// <summary>Widgets on the panel, in display order.</summary>
    public List<WidgetPlacement> Widgets { get; set; } = [];

    /// <summary>Per-plugin state, keyed by plugin id.</summary>
    public Dictionary<string, PluginEntry> Plugins { get; set; } = [];

    /// <summary>Set once the first run has seeded the default widget set.</summary>
    public bool Initialised { get; set; }
}

public sealed class TdpSettings
{
    /// <summary>Last value the user chose, in W. 0 = never set.</summary>
    public int LastWatts { get; set; }
    /// <summary>Re-assert the limit every N seconds (defeats OEM software). 0 = off.</summary>
    public int ReapplySeconds { get; set; } = 30;
    /// <summary>Apply <see cref="AcWatts"/>/<see cref="DcWatts"/> on charger connect/disconnect.</summary>
    public bool UsePowerSourceDefaults { get; set; }
    /// <summary>Charger / battery limits in W. 0 = derive from the detected range on first run.</summary>
    public int AcWatts { get; set; }
    public int DcWatts { get; set; }
    /// <summary>
    /// Slider bounds, in W. 0 means "not established yet" — the first run probes the
    /// installed silicon for them rather than assuming a particular chip.
    /// </summary>
    public int MinWatts { get; set; }
    public int MaxWatts { get; set; }
    /// <summary>
    /// While true the range keeps tracking what the firmware reports. Editing either
    /// bound by hand clears it, so a deliberate choice is never overwritten by a
    /// later probe.
    /// </summary>
    public bool RangeAutoDetected { get; set; } = true;
}

/// <summary>One widget placed on the panel.</summary>
public sealed class WidgetPlacement
{
    /// <summary>Unique per placement — two brightness widgets have different ids.</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>Which <see cref="IWidgetFactory.TypeId"/> to instantiate.</summary>
    public string TypeId { get; set; } = "";
    /// <summary>Opaque per-instance settings owned by the widget.</summary>
    [JsonConverter(typeof(JsonObjectConverter))]
    public JsonObject Settings { get; set; } = [];
}

public sealed class PluginEntry
{
    public bool Enabled { get; set; }
    [JsonConverter(typeof(JsonObjectConverter))]
    public JsonObject Settings { get; set; } = [];
}

/// <summary>
/// Keeps a <see cref="JsonObject"/> property round-tripping as a plain object.
/// Without it a null node in the file would deserialize to null and every widget
/// would need a null check before its first write.
/// </summary>
internal sealed class JsonObjectConverter : JsonConverter<JsonObject>
{
    public override JsonObject Read(ref System.Text.Json.Utf8JsonReader reader,
                                    Type type, System.Text.Json.JsonSerializerOptions options)
        => JsonNode.Parse(ref reader) as JsonObject ?? [];

    public override void Write(System.Text.Json.Utf8JsonWriter writer, JsonObject value,
                               System.Text.Json.JsonSerializerOptions options)
        => value.WriteTo(writer, options);
}
