using System.Text.Json;
using System.Text.Json.Serialization;

namespace Restaurant.Client.Core;

public static class Json
{
    /// <summary>Matches the API: camelCase, enums as strings.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
