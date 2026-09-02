using System.Xml.Linq;

namespace Hydrocephalus.Domain.Tests;

/// <summary>
/// Проверяет направление зависимостей между проектами по графу ProjectReference.
/// Границы слоёв заданы в docs/architecture/README.md; нарушение должно ломать сборку,
/// а не обнаруживаться на code review.
/// </summary>
public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Domain_has_no_project_references()
    {
        // Доменный слой не зависит ни от чего внутри решения:
        // "Domain: сущности, статусы, контракты и инварианты; не отвечает за ввод-вывод и фреймворки".
        Assert.Empty(ProjectReferencesOf("src/Hydrocephalus.Domain"));
    }

    [Theory]
    [InlineData("src/Hydrocephalus.Application")]
    [InlineData("src/Hydrocephalus.Infrastructure")]
    [InlineData("src/Hydrocephalus.Inference")]
    public void Layers_depend_only_on_domain(string projectDirectory)
    {
        // Application, Infrastructure и Inference общаются между собой только через
        // контракты доменного слоя, а не напрямую.
        Assert.Equal(["Hydrocephalus.Domain"], ProjectReferencesOf(projectDirectory));
    }

    [Fact]
    public void Desktop_is_the_only_composition_root()
    {
        // Desktop собирает реализации вместе через DI, поэтому ему разрешено ссылаться
        // на все слои. Никакой другой проект так делать не должен.
        var desktop = ProjectReferencesOf("src/Hydrocephalus.Desktop");

        Assert.Contains("Hydrocephalus.Application", desktop);
        Assert.Contains("Hydrocephalus.Infrastructure", desktop);
        Assert.Contains("Hydrocephalus.Inference", desktop);

        foreach (var layer in new[] { "Application", "Infrastructure", "Inference" })
        {
            var references = ProjectReferencesOf($"src/Hydrocephalus.{layer}");
            Assert.DoesNotContain("Hydrocephalus.Desktop", references);
        }
    }

    private static string[] ProjectReferencesOf(string projectDirectory)
    {
        var directory = Path.Combine(RepositoryRoot(), projectDirectory);
        var projectFile = Directory.GetFiles(directory, "*.csproj").Single();

        return XDocument.Load(projectFile)
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(
                reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hydrocephalus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
