using System.Text.Json;

namespace Restaurant.Client.Core;

/// <summary>
/// Ionicons (MIT) by name ("receipt-outline", "cash", ...), rendered from the bundled font in
/// Restaurant.Client.UI/wwwroot/fonts/Ionicons.ttf (same approach as Tailor Book).
/// </summary>
public static class Icons
{
    private static readonly Lazy<Dictionary<string, int>> Glyphs = new(() =>
    {
        using var stream = typeof(Icons).Assembly.GetManifestResourceStream("Restaurant.ionicons-glyphs.json")
            ?? throw new InvalidOperationException("ionicons-glyphs.json resource missing");
        return JsonSerializer.Deserialize<Dictionary<string, int>>(stream) ?? [];
    });

    /// <summary>The character for an icon name; a question-mark glyph if the name is unknown.</summary>
    public static string Char(string name) =>
        char.ConvertFromUtf32(Glyphs.Value.TryGetValue(name, out var code) ? code : Glyphs.Value.GetValueOrDefault("help-circle-outline", '?'));
}
