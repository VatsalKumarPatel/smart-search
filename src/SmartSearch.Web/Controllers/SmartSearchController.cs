using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Mvc;
using SmartSearch.Contracts.ViewModels;
using SmartSearch.Web.Preview;

namespace SmartSearch.Web.Controllers;

[Authorize]
[Route("smart-search")]
public sealed class SmartSearchController(
    ISmartSearchPipeline pipeline,
    IClickTracker clicks,
    IWebHostEnvironment env) : Controller
{
    /// <summary>
    /// Returns the results partial (HTML). Called by smart-search.js.
    /// New search:      ?q=Get me latest visit for vatsal
    /// Change layout:   ?queryId=...&amp;layout=List         (re-runs stored intent, no LLM call)
    /// Did-you-mean:    ?queryId=...&amp;clientId=1042       (re-runs pinned to that client)
    /// Paging:          ?queryId=...&amp;page=2
    /// </summary>
    [HttpGet("results")]
    public async Task<IActionResult> Results([FromQuery] SearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Q) && request.QueryId is null)
            return BadRequest();

        var vm = await pipeline.RunAsync(request, User, ct);

        Response.Headers.CacheControl = "no-store";   // results contain PHI
        return PartialView(SmartSearchViews.Results, vm);
    }

    /// <summary>Click tracking, sent with navigator.sendBeacon.</summary>
    [HttpPost("click")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Click([FromForm] ClickRequest click, CancellationToken ct)
    {
        await clicks.RecordAsync(click, User, ct);
        return NoContent();
    }

    // ------------------------------------------------------------------
    // Design preview (Development only). Lets designers and QA see every
    // state with mock data, no Bedrock / database needed.
    //   /smart-search/preview
    // ------------------------------------------------------------------

    [AllowAnonymous]
    [HttpGet("preview")]
    public IActionResult Preview()
    {
        if (!env.IsDevelopment()) return NotFound();
        return View(SmartSearchViews.Preview, PreviewScenarios.All);
    }

    [AllowAnonymous]
    [HttpGet("preview/results")]
    public IActionResult PreviewResults([FromQuery] SearchRequest request, [FromQuery] string? scenario)
    {
        if (!env.IsDevelopment()) return NotFound();
        var vm = PreviewData.Build(scenario, request);
        return PartialView(SmartSearchViews.Results, vm);
    }
}

public static class SmartSearchViews
{
    public const string Root = "~/Views/SmartSearch/";
    public const string Results = Root + "_Results.cshtml";
    public const string SearchBox = Root + "_SearchBox.cshtml";
    public const string Preview = Root + "Preview.cshtml";
}

public sealed record SearchRequest
{
    public string? Q { get; init; }
    public Guid? QueryId { get; init; }
    public long? ClientId { get; init; }
    public ResultLayout? Layout { get; init; }
    public int Page { get; init; } = 1;
    public string Source { get; init; } = "Text";   // Text | Voice
}

public sealed record ClickRequest
{
    public Guid QueryId { get; init; }
    public string ClickType { get; init; } = "Link";   // Link | Suggestion | FollowUp | Layout
    public string? EntityType { get; init; }
    public long? EntityId { get; init; }
    public long? ClientId { get; init; }
    public string? LinkKey { get; init; }
    public short? Rank { get; init; }
}

/// <summary>Implemented in SmartSearch.Application (the 8-stage pipeline).</summary>
public interface ISmartSearchPipeline
{
    Task<SmartSearchResponse> RunAsync(SearchRequest request, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct);
}

/// <summary>Writes ss.QueryClick.</summary>
public interface IClickTracker
{
    Task RecordAsync(ClickRequest click, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct);
}
