using Helpdesk.Core.Interfaces;
using Helpdesk.Infrastructure.Ai;
using Helpdesk.Infrastructure.Ai.Kb;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Infrastructure.Tests.Ai;

public class KnowledgeBaseDependencyInjectionTests
{
    [Fact]
    public void AddKnowledgeBase_RegistersSingletonJsonKnowledgeBase_WithoutAnyConfiguration()
    {
        var services = new ServiceCollection();
        services.AddKnowledgeBase();

        using var provider = services.BuildServiceProvider();

        var kb = provider.GetRequiredService<IKnowledgeBase>();
        Assert.IsType<JsonKnowledgeBase>(kb);
        Assert.Same(kb, provider.GetRequiredService<IKnowledgeBase>());
    }
}
