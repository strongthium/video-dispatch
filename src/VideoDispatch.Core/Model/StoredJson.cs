using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoDispatch.Core.Model;

/// <summary>The JSON format for jobs and presets saved on disk.</summary>
public static class StoredJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
