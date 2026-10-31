using System.Text;

namespace VideoDispatch.Core.Templates;

public sealed record TemplateResult(string Text, IReadOnlyList<string> UnknownVariables);

/// <summary>
/// Fills <c>{name}</c> variables, ignoring case. <c>{{</c> and <c>}}</c> give literal braces,
/// and braces that don't enclose a variable name stay as written. Unknown variables stay in
/// the text and are reported, so a typo can be caught before it reaches a public post.
/// </summary>
public static class TemplateRenderer
{
    public static TemplateResult Render(string template, TemplateContext context)
    {
        var values = context.ToVariables();
        var text = new StringBuilder(template.Length);
        var unknown = new List<string>();

        var i = 0;
        while (i < template.Length)
        {
            var c = template[i];

            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                text.Append(c);
                i += 2;
                continue;
            }

            if (c == '{' && TryReadName(template, i + 1, out var name))
            {
                if (values.TryGetValue(name, out var value))
                {
                    text.Append(value);
                }
                else
                {
                    text.Append('{').Append(name).Append('}');
                    if (!unknown.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        unknown.Add(name);
                    }
                }

                i += name.Length + 2;
                continue;
            }

            text.Append(c);
            i++;
        }

        return new TemplateResult(text.ToString(), unknown);
    }

    private static bool TryReadName(string template, int start, out string name)
    {
        var end = start;
        while (end < template.Length && (char.IsAsciiLetterOrDigit(template[end]) || template[end] == '_'))
        {
            end++;
        }

        if (end > start && end < template.Length && template[end] == '}')
        {
            name = template[start..end];
            return true;
        }

        name = "";
        return false;
    }
}
