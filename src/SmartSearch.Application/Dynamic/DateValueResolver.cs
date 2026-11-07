using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartSearch.Application.Dynamic;

/// <summary>
/// Turns plan date values into real dates, using "today" in the tenant's time zone.
///   "2024-03-01"                         absolute
///   "today" | "now"
///   { "relative": "-90d" }               d, w, m, y  (negative = past)
///   { "startOf": "month", "offset": -1 } day | week | month | quarter | year; offset in those units
/// Keeping relative dates symbolic in the plan means a saved plan stays correct tomorrow.
/// </summary>
public static partial class DateValueResolver
{
    public static bool TryResolve(JsonElement value, DateTime today, out DateTime result, out string? error)
    {
        result = default;
        error = null;
        today = today.Date;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                var s = value.GetString()!.Trim();
                if (s.Equals("today", StringComparison.OrdinalIgnoreCase) || s.Equals("now", StringComparison.OrdinalIgnoreCase))
                {
                    result = today;
                    return true;
                }
                if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out result))
                    return true;
                error = $"'{s}' is not a date";
                return false;
            }

            case JsonValueKind.Object when value.TryGetProperty("relative", out var rel) && rel.ValueKind == JsonValueKind.String:
            {
                var m = Relative().Match(rel.GetString()!.Trim());
                if (!m.Success) { error = "relative must look like -90d, -6m, +1w, -2y"; return false; }
                var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                if (Math.Abs(n) > 400) { error = "relative value too large"; return false; }
                result = m.Groups["u"].Value switch
                {
                    "d" => today.AddDays(n),
                    "w" => today.AddDays(n * 7),
                    "m" => today.AddMonths(n),
                    _   => today.AddYears(n)
                };
                return true;
            }

            case JsonValueKind.Object when value.TryGetProperty("startOf", out var unitEl) && unitEl.ValueKind == JsonValueKind.String:
            {
                var offset = value.TryGetProperty("offset", out var o) && o.TryGetInt32(out var off) ? off : 0;
                if (Math.Abs(offset) > 400) { error = "offset too large"; return false; }
                switch (unitEl.GetString())
                {
                    case "day":     result = today.AddDays(offset); return true;
                    case "week":    result = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)).AddDays(offset * 7); return true; // Monday
                    case "month":   result = new DateTime(today.Year, today.Month, 1).AddMonths(offset); return true;
                    case "quarter": result = new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1).AddMonths(offset * 3); return true;
                    case "year":    result = new DateTime(today.Year, 1, 1).AddYears(offset); return true;
                    default: error = "startOf must be day, week, month, quarter or year"; return false;
                }
            }

            default:
                error = "unsupported date value";
                return false;
        }
    }

    [GeneratedRegex(@"^(?<n>[+-]?\d+)(?<u>[dwmy])$")]
    private static partial Regex Relative();
}
