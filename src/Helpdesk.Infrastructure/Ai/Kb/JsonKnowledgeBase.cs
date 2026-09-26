using System.Text.Json;
using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Infrastructure.Ai.Kb;

/// <summary>
/// The hardcoded MVP knowledge base: an embedded kb.json, loaded and validated once on first use.
/// </summary>
public sealed class JsonKnowledgeBase : IKnowledgeBase
{
    private const string ResourceName = "Helpdesk.Infrastructure.Ai.Kb.kb.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly Lazy<IReadOnlyList<KbArticle>> articles = new(LoadEmbedded);

    public IReadOnlyList<KbArticle> GetAll() => articles.Value;

    internal static IReadOnlyList<KbArticle> Parse(string json)
    {
        List<KbArticle>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<KbArticle>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The knowledge base JSON is malformed.", ex);
        }

        if (parsed is null || parsed.Count == 0)
        {
            throw new InvalidOperationException("The knowledge base has no articles.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in parsed)
        {
            Validate(article);
            if (!ids.Add(article.Id))
            {
                throw new InvalidOperationException($"Duplicate knowledge base article id '{article.Id}'.");
            }
        }

        return parsed;
    }

    private static void Validate(KbArticle article)
    {
        if (string.IsNullOrWhiteSpace(article.Id)
            || string.IsNullOrWhiteSpace(article.Title)
            || string.IsNullOrWhiteSpace(article.Content))
        {
            throw new InvalidOperationException(
                $"Knowledge base article '{article.Id}' must have an id, title and content.");
        }

        if (!TicketCategories.All.Contains(article.Category))
        {
            throw new InvalidOperationException(
                $"Knowledge base article '{article.Id}' has unknown category '{article.Category}'.");
        }

        if (article.Keywords is null || !article.Keywords.Any(k => !string.IsNullOrWhiteSpace(k)))
        {
            throw new InvalidOperationException(
                $"Knowledge base article '{article.Id}' must have at least one non-blank keyword.");
        }
    }

    private static IReadOnlyList<KbArticle> LoadEmbedded()
    {
        using var stream = typeof(JsonKnowledgeBase).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
