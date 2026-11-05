using System.Text.Json.Serialization;

namespace SmartSearch.Application.Views;

/// <summary>
/// Deserialized form of ss.ViewTemplate.FieldMapJson.
/// Decides WHAT is shown. The Razor views decide HOW it looks.
/// </summary>
public sealed class FieldMap
{
    [JsonPropertyName("title")]          public string Title { get; init; } = "";
    [JsonPropertyName("subtitle")]       public string? Subtitle { get; init; }
    [JsonPropertyName("fields")]         public List<FieldDef> Fields { get; init; } = [];
    [JsonPropertyName("badges")]         public List<BadgeDef> Badges { get; init; } = [];
    [JsonPropertyName("primaryLink")]    public string? PrimaryLink { get; init; }
    [JsonPropertyName("secondaryLinks")] public List<string> SecondaryLinks { get; init; } = [];
}

public sealed class FieldDef
{
    [JsonPropertyName("key")]       public string Key { get; init; } = "";
    [JsonPropertyName("label")]     public string Label { get; init; } = "";
    [JsonPropertyName("type")]      public string Type { get; init; } = "text";
    [JsonPropertyName("panelOnly")] public bool PanelOnly { get; init; }
    [JsonPropertyName("listOnly")]  public bool ListOnly { get; init; }
    [JsonPropertyName("sortable")]  public bool Sortable { get; init; } = true;
}

public sealed class BadgeDef
{
    [JsonPropertyName("key")]     public string Key { get; init; } = "";
    [JsonPropertyName("toneMap")] public Dictionary<string, string> ToneMap { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
