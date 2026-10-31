using VideoDispatch.Core.Templates;

namespace VideoDispatch.Core.Tests.Templates;

public class TemplateRendererTests
{
    private static readonly TemplateContext Context = new("My Trip", "trip_01", new DateOnly(2026, 10, 31));

    [Theory]
    [InlineData("{title}", "My Trip")]
    [InlineData("{filename}", "trip_01")]
    [InlineData("{date}", "2026-10-31")]
    [InlineData("{TITLE}", "My Trip")]
    [InlineData("{title} on {date}", "My Trip on 2026-10-31")]
    [InlineData("no variables", "no variables")]
    [InlineData("", "")]
    public void Fills_known_variables(string template, string expected)
    {
        var result = TemplateRenderer.Render(template, Context);

        Assert.Equal(expected, result.Text);
        Assert.Empty(result.UnknownVariables);
    }

    [Theory]
    [InlineData("{{title}}", "{title}")]
    [InlineData("{{", "{")]
    [InlineData("}}", "}")]
    [InlineData("{ title }", "{ title }")]
    [InlineData("{}", "{}")]
    [InlineData("{title", "{title")]
    [InlineData("title}", "title}")]
    [InlineData("{", "{")]
    [InlineData("{a-b}", "{a-b}")]
    public void Keeps_literal_braces(string template, string expected)
    {
        var result = TemplateRenderer.Render(template, Context);

        Assert.Equal(expected, result.Text);
        Assert.Empty(result.UnknownVariables);
    }

    [Fact]
    public void Reports_unknown_variables_and_leaves_them_visible()
    {
        var result = TemplateRenderer.Render("{titel} {title} {nope} {NOPE}", Context);

        Assert.Equal("{titel} My Trip {nope} {NOPE}", result.Text);
        Assert.Equal(["titel", "nope"], result.UnknownVariables);
    }

    [Fact]
    public void Does_not_expand_variables_inside_values()
    {
        var context = Context with { Title = "{date}" };

        Assert.Equal("{date}", TemplateRenderer.Render("{title}", context).Text);
    }

    [Fact]
    public void ForFile_uses_the_file_name_when_no_title_is_given()
    {
        var path = Path.Combine("videos", "clip.final.mp4");

        var context = TemplateContext.ForFile(path, new DateOnly(2026, 1, 2));

        Assert.Equal("clip.final", context.Title);
        Assert.Equal("clip.final", context.FileName);
    }

    [Fact]
    public void ForFile_prefers_the_given_title()
    {
        var context = TemplateContext.ForFile("clip.mp4", new DateOnly(2026, 1, 2), "Launch day");

        Assert.Equal("Launch day", context.Title);
        Assert.Equal("clip", context.FileName);
    }
}
