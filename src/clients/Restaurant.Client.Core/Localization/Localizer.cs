using System.Text.Json;
using Restaurant.Client.Core.Platform;

namespace Restaurant.Client.Core.Localization;

/// <summary>
/// English / Urdu strings ("auth.signIn", "staff.title", ...) from Resources/translations.json.
/// Missing Urdu keys fall back to English, and an unknown key shows itself. Urdu switches to RTL.
/// </summary>
public class Localizer(IKeyValueStore store)
{
    public static readonly string[] Supported = ["en", "ur"];
    private const string StorageKey = "rp.language";

    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> Strings = new(Load);

    public string Language { get; private set; } = "en";
    public bool IsRtl => Language == "ur";
    public string Dir => IsRtl ? "rtl" : "ltr";

    public event Action? Changed;

    public string this[string key] => T(key);

    public string T(string key) =>
        Strings.Value.TryGetValue(Language, out var lang) && lang.TryGetValue(key, out var value) ? value
        : Strings.Value["en"].TryGetValue(key, out var fallback) ? fallback
        : key;

    /// <summary>Replaces {name} placeholders: T("staff.inviteFor", ("name", "Ali")).</summary>
    public string T(string key, params (string Name, string Value)[] args)
    {
        var text = T(key);
        foreach (var (name, value) in args) text = text.Replace($"{{{name}}}", value);
        return text;
    }

    public async Task InitializeAsync()
    {
        string? saved = null;
        try { saved = await store.GetAsync(StorageKey); } catch { /* storage unavailable */ }
        Language = Supported.Contains(saved) ? saved! : "en";
    }

    public async Task SetLanguageAsync(string language)
    {
        if (!Supported.Contains(language) || language == Language) return;
        Language = language;
        try { await store.SetAsync(StorageKey, language); } catch { /* not remembered */ }
        Changed?.Invoke();
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("Restaurant.translations.json")
            ?? throw new InvalidOperationException("translations.json resource missing");
        using var doc = JsonDocument.Parse(stream);

        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var language in doc.RootElement.EnumerateObject())
        {
            var flat = new Dictionary<string, string>();
            Flatten(language.Value, "", flat);
            result[language.Name] = flat;
        }
        return result;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
    {
        foreach (var property in element.EnumerateObject())
        {
            var key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.Object) Flatten(property.Value, key, into);
            else into[key] = property.Value.GetString() ?? string.Empty;
        }
    }
}
