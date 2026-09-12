using System.IO;
using System.Text.RegularExpressions;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Текст исключения не выводится интерфейсом нигде, кроме как через
/// <c>ErrorReadout</c>.
///
/// Правило проверено тестами самой раскладки, но нарушается не в ней, а в новом
/// обработчике, написанном по привычке: <c>"не удалось: " + exception.Message</c>.
/// Поэтому исходники окна просматриваются целиком — так же, как граница слоёв
/// проверяется по графу проектов, а не на code review.
/// </summary>
public sealed partial class ExceptionTextSourceTests
{
    [Fact]
    public void The_desktop_never_shows_an_exception_message()
    {
        var sources = Directory.GetFiles(
            System.IO.Path.Combine(RepositoryRoot(), "src", "Hydrocephalus.Desktop"),
            "*.cs",
            SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var offenders = sources
            .SelectMany(path => File.ReadLines(path)
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Where(line => MessageAccess().IsMatch(line))
                .Select(line => System.IO.Path.GetFileName(path) + ": " + line.Trim()))
            .ToList();

        Assert.Empty(offenders);
    }

    // Шаблон узкий намеренно: ищутся Message, InnerException и ToString()
    // у переменных, под которыми в этом коде держат исключения. ToString()
    // утекает больше, чем Message: к тексту добавляются стек и, у
    // FileNotFoundException, имя файла. Если тест сработает на невинном члене
    // с таким же именем, переименовывается переменная, а не ослабляется шаблон:
    // ослабленный шаблон перестаёт находить что-либо.
    [GeneratedRegex(@"\b(exception|ex|error|cause|inner|e\.Exception)\.(Message\b|InnerException\b|ToString\s*\()")]
    private static partial Regex MessageAccess();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Hydrocephalus.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
