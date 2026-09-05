// В WPF-проекте короткое Path разрешается в System.Windows.Shapes.Path,
// поэтому файловый путь называется через псевдоним.
using IoPath = System.IO.Path;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Расположение локальных каталогов приложения.
///
/// Всё лежит в профиле текущего пользователя, а не в общем каталоге машины:
/// соль и рабочие копии защищены DPAPI в области текущего пользователя (ADR 0006),
/// и класть их туда, где их прочитает другая учётная запись, бессмысленно.
///
/// Каталог локальный, а не перемещаемый: рабочие копии не должны уезжать
/// на сетевой профиль вместе с пользователем.
/// </summary>
public sealed record ApplicationPaths
{
    /// <summary>Имя каталога приложения внутри профиля пользователя.</summary>
    public const string ApplicationFolderName = "Hydrocephalus";

    /// <summary>Корень рабочих копий.</summary>
    public required string WorkingCopyRoot { get; init; }

    /// <summary>Корень сохранённых отчётов.</summary>
    public required string ReportRoot { get; init; }

    /// <summary>Файл журнала аудита.</summary>
    public required string AuditLogPath { get; init; }

    /// <summary>Файл с защищённой солью псевдонимизации.</summary>
    public required string PseudonymSaltPath { get; init; }

    /// <summary>
    /// Строит расположение в профиле текущего пользователя.
    /// </summary>
    /// <returns>Пути приложения.</returns>
    public static ApplicationPaths UnderLocalApplicationData()
    {
        var root = IoPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);

        return UnderRoot(root);
    }

    /// <summary>
    /// Строит расположение внутри заданного каталога.
    /// </summary>
    /// <param name="root">Корневой каталог.</param>
    /// <returns>Пути приложения.</returns>
    public static ApplicationPaths UnderRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        return new ApplicationPaths
        {
            WorkingCopyRoot = IoPath.Combine(root, "working-copies"),
            ReportRoot = IoPath.Combine(root, "reports"),
            AuditLogPath = IoPath.Combine(root, "audit", "audit.log"),
            PseudonymSaltPath = IoPath.Combine(root, "secrets", "pseudonym-salt.bin"),
        };
    }
}
