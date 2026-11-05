namespace SmartSearch.Web.Controllers;

/// <summary>Options for the _SearchBox partial.</summary>
public sealed record SearchBoxOptions
{
    public string Id { get; init; } = "main";
    public string Placeholder { get; init; } = "Ask anything, e.g. \"Get me latest visit for Vatsal\"";
    public string? ResultsUrl { get; init; }          // default: /smart-search/results
    public string? ClickUrl { get; init; }            // default: /smart-search/click
    public string? SpeechHubUrl { get; init; } = "/hubs/speech";   // null hides the mic
    public bool DemoMic { get; init; }                // preview only: fake transcription
}
