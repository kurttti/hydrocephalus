using System.Reflection;

namespace Hydrocephalus.Infrastructure.Configuration;

/// <summary>
/// Сведения о сборке, попадающие в provenance отчёта.
///
/// Commit SHA берётся из атрибута, который SDK проставляет при сборке из
/// git-репозитория, а не из файла с версией и не из переменной окружения:
/// значение, которое можно задать вручную, перестаёт быть свидетельством того,
/// каким кодом получен результат.
/// </summary>
public static class BuildProvenance
{
    /// <summary>Длина полного SHA-1 коммита в шестнадцатеричных знаках.</summary>
    private const int CommitShaLength = 40;

    /// <summary>
    /// Возвращает commit SHA сборки.
    /// </summary>
    /// <param name="assembly">Сборка приложения.</param>
    /// <returns>Полный SHA коммита.</returns>
    /// <exception cref="InvalidOperationException">
    /// Если сборка собрана вне git-репозитория и SHA в ней нет.
    /// </exception>
    public static string CommitShaOf(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return TryGetCommitSha(assembly, out var sha)
            ? sha

            // Подставить «unknown» нельзя: отчёт заявляет воспроизводимость,
            // и поле, заполненное заглушкой, выглядит как заполненное. Сборка
            // без SHA — это сборка, результат которой невозможно связать
            // с кодом, и говорить об этом нужно на старте, а не в отчёте.
            : throw new InvalidOperationException(
                $"Assembly '{assembly.GetName().Name}' carries no commit SHA. "
                + "A report cannot claim provenance that the build does not have.");
    }

    /// <summary>
    /// Пытается прочитать commit SHA сборки.
    /// </summary>
    /// <param name="assembly">Сборка.</param>
    /// <param name="commitSha">Прочитанный SHA.</param>
    /// <returns><see langword="true"/>, если SHA найден.</returns>
    public static bool TryGetCommitSha(Assembly assembly, out string commitSha)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        commitSha = string.Empty;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informational))
        {
            return false;
        }

        // SDK дописывает SHA после «+» по правилам SemVer: «1.2.3+<sha>».
        var separator = informational.LastIndexOf('+');

        if (separator < 0 || separator == informational.Length - 1)
        {
            return false;
        }

        var candidate = informational[(separator + 1)..];

        if (candidate.Length != CommitShaLength || !IsHex(candidate))
        {
            return false;
        }

        commitSha = candidate;
        return true;
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
