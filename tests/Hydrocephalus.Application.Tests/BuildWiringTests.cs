using System.Reflection;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Дымовые проверки того, что каркас решения собран и связан.
/// Сценарии уровня Application (ImportStudy, RunQualityControl, AnalyzeStudy и остальные
/// из src/Hydrocephalus.Application/README.md) ещё не реализованы — они относятся
/// к следующим задачам M1.
/// </summary>
public sealed class BuildWiringTests
{
    [Fact]
    public void Application_assembly_loads_together_with_domain()
    {
        var application = Assembly.Load("Hydrocephalus.Application");
        var domain = Assembly.Load("Hydrocephalus.Domain");

        Assert.NotNull(application);
        Assert.NotNull(domain);
    }
}
