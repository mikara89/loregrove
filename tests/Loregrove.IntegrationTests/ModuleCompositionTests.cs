using Loregrove.Infrastructure.AI;
using Loregrove.Infrastructure.Docling;
using Loregrove.Infrastructure.LocalFiles;
using Loregrove.Infrastructure.Search;
using Loregrove.Infrastructure.Sqlite;

namespace Loregrove.IntegrationTests;

public sealed class ModuleCompositionTests
{
    [Fact]
    public void DeferredInfrastructureModulesRemainLoadableWithoutProductInfrastructure()
    {
        var assemblies = new[]
        {
            typeof(AiModule).Assembly,
            typeof(DoclingModule).Assembly,
            typeof(LocalFilesModule).Assembly,
            typeof(SearchModule).Assembly,
            typeof(SqliteModule).Assembly,
        };

        Assert.All(
            assemblies,
            assembly => Assert.StartsWith(
                "Loregrove.Infrastructure.",
                assembly.GetName().Name ?? string.Empty,
                StringComparison.Ordinal));
    }
}
