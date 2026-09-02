using System.Reflection;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Место для synthetic end-to-end теста «импорт → QC → инференс → отчёт»
/// (M1 в docs/roadmap.md, уровень Integration в tests/README.md).
/// Сам сценарий появится вместе с доменными контрактами и портами инфраструктуры;
/// пока проверяется только то, что все три слоя собираются и грузятся вместе.
/// </summary>
public sealed class SyntheticPipelineTests
{
    [Fact]
    public void All_pipeline_layers_load()
    {
        foreach (var name in new[]
                 {
                     "Hydrocephalus.Domain",
                     "Hydrocephalus.Application",
                     "Hydrocephalus.Infrastructure",
                     "Hydrocephalus.Inference",
                 })
        {
            Assert.NotNull(Assembly.Load(name));
        }
    }
}
