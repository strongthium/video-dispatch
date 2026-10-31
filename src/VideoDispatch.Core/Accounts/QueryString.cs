namespace VideoDispatch.Core.Accounts;

internal static class QueryString
{
    public static Uri AppendTo(string baseUrl, IEnumerable<KeyValuePair<string, string>> values)
    {
        var query = string.Join('&', values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri($"{baseUrl}?{query}");
    }
}
