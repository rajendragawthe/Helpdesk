using Helpdesk.Application.DraftReply;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.DraftReply;

public class KbMatcherTests
{
    private static KbArticle Article(string id, string category, params string[] keywords) =>
        new(id, $"Title {id}", category, keywords, $"Content {id}");

    private static string[] Ids(IReadOnlyList<KbArticle> articles) => articles.Select(a => a.Id).ToArray();

    [Fact]
    public void Match_OrdersByNumberOfDistinctKeywordHits()
    {
        var one = Article("one", "Billing", "refund");
        var two = Article("two", "Billing", "refund", "invoice");

        var result = KbMatcher.Match("Refund", "please send the invoice", null, [one, two]);

        Assert.Equal(["two", "one"], Ids(result));
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var result = KbMatcher.Match("REFUND ME", "", null, [Article("a", "Billing", "refund")]);

        Assert.Equal(["a"], Ids(result));
    }

    [Fact]
    public void Match_MatchesWholeWordsOnly()
    {
        var article = Article("a", "Billing", "refund");

        Assert.Empty(KbMatcher.Match("s", "refunded and prefund", null, [article]));
        Assert.Single(KbMatcher.Match("s", "a refund, please.", null, [article]));
    }

    [Fact]
    public void Match_MatchesMultiWordPhrase()
    {
        var article = Article("a", "Billing", "charged twice");

        Assert.Single(KbMatcher.Match("I was charged twice", "", null, [article]));
        Assert.Empty(KbMatcher.Match("I was charged", "twice", null, [article]));
    }

    [Fact]
    public void Match_SearchesSubjectAndBody()
    {
        var subjectOnly = Article("s", "Billing", "refund");
        var bodyOnly = Article("b", "Billing", "invoice");

        var result = KbMatcher.Match("refund", "need my invoice", null, [subjectOnly, bodyOnly]);

        Assert.Equal(["s", "b"], Ids(result));
    }

    [Fact]
    public void Match_CategoryBoostBreaksTies()
    {
        var first = Article("first", "Technical Issue", "refund");
        var second = Article("second", "Billing", "refund");

        var result = KbMatcher.Match("refund", "", "Billing", [first, second]);

        Assert.Equal(["second", "first"], Ids(result));
    }

    [Fact]
    public void Match_CategoryAloneDoesNotSelectAnArticle()
    {
        var result = KbMatcher.Match("hello", "nothing relevant", "Billing", [Article("a", "Billing", "refund")]);

        Assert.Empty(result);
    }

    [Fact]
    public void Match_ReturnsAtMostThreeArticles()
    {
        var articles = Enumerable.Range(1, 5).Select(i => Article($"a{i}", "Billing", "refund")).ToList();

        var result = KbMatcher.Match("refund", "", null, articles);

        Assert.Equal(KbMatcher.MaxArticles, result.Count);
    }

    [Fact]
    public void Match_TiesKeepFileOrder()
    {
        var result = KbMatcher.Match("refund", "", null,
            [Article("a", "Billing", "refund"), Article("b", "Billing", "refund"), Article("c", "Billing", "refund")]);

        Assert.Equal(["a", "b", "c"], Ids(result));
    }

    [Fact]
    public void Match_NoKeywordHit_ReturnsEmpty()
    {
        Assert.Empty(KbMatcher.Match("hello", "world", null, [Article("a", "Billing", "refund")]));
    }

    [Fact]
    public void Match_DuplicateKeywordsCountOnce()
    {
        var duplicated = Article("dup", "Billing", "refund", "REFUND");
        var real = Article("real", "Billing", "refund", "invoice");

        var result = KbMatcher.Match("refund invoice", "", null, [duplicated, real]);

        Assert.Equal(["real", "dup"], Ids(result));
    }

    [Fact]
    public void Match_BlankKeywordsAreIgnored()
    {
        Assert.Empty(KbMatcher.Match("anything", "at all", null, [Article("a", "Billing", "", "   ")]));
    }

    [Fact]
    public void Match_RegexMetacharactersInKeywordsMatchLiterally()
    {
        var dot = Article("dot", "Other", "a.b");
        var plus = Article("plus", "Other", "c++");
        var bracket = Article("bracket", "Other", "[urgent]");

        Assert.Empty(KbMatcher.Match("axb", "", null, [dot]));
        Assert.Single(KbMatcher.Match("a.b", "", null, [dot]));
        Assert.Single(KbMatcher.Match("I use c++ daily", "", null, [plus]));
        Assert.Single(KbMatcher.Match("[urgent] help", "", null, [bracket]));
    }
}
