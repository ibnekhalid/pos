using System.Globalization;

namespace Restaurant.Client.Core;

/// <summary>Display formatting shared by every screen ("Rs 1,625", "28 Oct 2026").</summary>
public static class Format
{
    private static readonly Dictionary<string, string> CurrencySymbols = new()
    {
        ["PKR"] = "Rs",
        ["USD"] = "$",
        ["GBP"] = "£",
        ["EUR"] = "€",
        ["AED"] = "AED",
        ["SAR"] = "SAR",
    };

    public static string Money(decimal amount, string? currencyCode = "PKR")
    {
        var code = string.IsNullOrWhiteSpace(currencyCode) ? "PKR" : currencyCode;
        var symbol = CurrencySymbols.GetValueOrDefault(code, code);
        var number = Math.Abs(amount).ToString(amount % 1 == 0 ? "#,0" : "#,0.00", CultureInfo.InvariantCulture);
        return amount < 0 ? $"-{symbol} {number}" : $"{symbol} {number}";
    }

    public static string Date(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.ToLocalTime().ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    public static string DateTime(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.ToLocalTime().ToString("dd MMM yyyy, h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>"16", "7.5" — up to 3 decimals, no trailing zeros.</summary>
    public static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Parses user input leniently ("1,500" → 1500); null when not a number.</summary>
    public static decimal? ParseNumber(string? text) =>
        decimal.TryParse(text?.Replace(",", string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static string Initials(string? name)
    {
        var parts = (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    /// <summary>Colour with transparency, for tinted backgrounds (#RRGGBB → #RRGGBBAA; otherwise color-mix).</summary>
    public static string WithAlpha(string? color, double alpha)
    {
        if (string.IsNullOrEmpty(color)) return "transparent";
        if (color.StartsWith('#') && color.Length == 7) return $"{color}{(int)Math.Round(alpha * 255):X2}";
        return $"color-mix(in srgb, {color} {Math.Round(alpha * 100)}%, transparent)";
    }
}
