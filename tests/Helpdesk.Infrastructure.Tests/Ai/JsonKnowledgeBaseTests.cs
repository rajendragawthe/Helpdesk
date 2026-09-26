using Helpdesk.Core.Models;
using Helpdesk.Infrastructure.Ai.Kb;

namespace Helpdesk.Infrastructure.Tests.Ai;

public class JsonKnowledgeBaseTests
{
    private const string ValidArticle =
        """{"id":"a","title":"T","category":"Billing","keywords":["refund"],"content":"C"}""";

    [Fact]
    public void GetAll_LoadsEmbeddedKbWithValidArticles()
    {
        var articles = new JsonKnowledgeBase().GetAll();

        Assert.True(articles.Count >= 8);
        Assert.Equal(articles.Count, articles.Select(a => a.Id).Distinct().Count());
        Assert.All(articles, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.False(string.IsNullOrWhiteSpace(a.Content));
            Assert.Contains(a.Category, TicketCategories.All);
            Assert.NotEmpty(a.Keywords);
            Assert.All(a.Keywords, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        });
    }

    [Fact]
    public void GetAll_CoversEveryCategoryExceptOther()
    {
        var categories = new JsonKnowledgeBase().GetAll().Select(a => a.Category).ToHashSet();

        foreach (var category in TicketCategories.All.Where(c => c != TicketCategories.Other))
        {
            Assert.Contains(category, categories);
        }
    }

    [Fact]
    public void GetAll_ReturnsTheSameListEachCall()
    {
        var kb = new JsonKnowledgeBase();

        Assert.Same(kb.GetAll(), kb.GetAll());
    }

    [Fact]
    public void Parse_ValidJson_ReturnsArticles()
    {
        var article = Assert.Single(JsonKnowledgeBase.Parse($"[{ValidArticle}]"));

        Assert.Equal("a", article.Id);
        Assert.Equal(["refund"], article.Keywords);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""[{"id":"a","title":"T","category":"Nope","keywords":["k"],"content":"C"}]""")]
    [InlineData("""[{"id":"a","title":"T","category":"Billing","keywords":[],"content":"C"}]""")]
    [InlineData("""[{"id":"a","title":"T","category":"Billing","keywords":["  "],"content":"C"}]""")]
    [InlineData("""[{"id":"a","title":"T","category":"Billing","content":"C"}]""")]
    [InlineData("""[{"id":"a","title":"T","category":"Billing","keywords":["k"],"content":" "}]""")]
    [InlineData("""[{"id":"","title":"T","category":"Billing","keywords":["k"],"content":"C"}]""")]
    [InlineData("""[{"id":"a","category":"Billing","keywords":["k"],"content":"C"}]""")]
    public void Parse_InvalidContent_Throws(string json)
    {
        Assert.Throws<InvalidOperationException>(() => JsonKnowledgeBase.Parse(json));
    }

    [Fact]
    public void Parse_DuplicateIds_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => JsonKnowledgeBase.Parse($"[{ValidArticle},{ValidArticle}]"));
    }
}
