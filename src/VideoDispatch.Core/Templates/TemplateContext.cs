using System.Globalization;

namespace VideoDispatch.Core.Templates;

/// <summary>The values a template can use: <c>{title}</c>, <c>{filename}</c> and <c>{date}</c>.</summary>
public sealed record TemplateContext(string Title, string FileName, DateOnly Date)
{
    public static TemplateContext ForFile(string path, DateOnly date, string? title = null)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return new TemplateContext(string.IsNullOrWhiteSpace(title) ? fileName : title, fileName, date);
    }

    internal Dictionary<string, string> ToVariables() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = Title,
        ["filename"] = FileName,
        ["date"] = Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };
}
