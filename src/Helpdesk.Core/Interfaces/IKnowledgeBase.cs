using Helpdesk.Core.Models;

namespace Helpdesk.Core.Interfaces;

public interface IKnowledgeBase
{
    IReadOnlyList<KbArticle> GetAll();
}
