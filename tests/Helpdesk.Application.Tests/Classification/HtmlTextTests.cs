using Helpdesk.Application.Classification;

namespace Helpdesk.Application.Tests.Classification;

public class HtmlTextTests
{
    [Fact]
    public void ToPlainText_StripsTagsAndDecodesEntities()
    {
        var result = HtmlText.ToPlainText("<html><body><p>Hello&nbsp;<b>world</b> &amp; friends</p></body></html>");

        Assert.Equal("Hello world & friends", result);
    }

    [Fact]
    public void ToPlainText_RemovesScriptAndStyleContent()
    {
        var result = HtmlText.ToPlainText("<style>p{color:red}</style><p>Visible</p><script>alert(1)</script>");

        Assert.Equal("Visible", result);
    }

    [Fact]
    public void ToPlainText_CollapsesWhitespace()
    {
        var result = HtmlText.ToPlainText("<p>one</p>\n\n   <p>two</p>");

        Assert.Equal("one two", result);
    }

    [Fact]
    public void ToPlainText_TruncatesToMaxLength()
    {
        var result = HtmlText.ToPlainText("<p>" + new string('a', 100) + "</p>", maxLength: 10);

        Assert.Equal(10, result.Length);
    }
}
