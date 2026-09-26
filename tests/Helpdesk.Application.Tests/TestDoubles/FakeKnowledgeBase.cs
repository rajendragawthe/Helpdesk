using Helpdesk.Core.Interfaces;
using Helpdesk.Core.Models;

namespace Helpdesk.Application.Tests.TestDoubles;

public class FakeKnowledgeBase : IKnowledgeBase
{
    public List<KbArticle> Articles { get; } = [];

    public IReadOnlyList<KbArticle> GetAll() => Articles;
}
