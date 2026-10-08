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
    /// Корень экспортированных отчётов.
    ///
    /// Отдельно от канонических отчётов: те остаются внутри приложения,
    /// а экспорт делается ради передачи наружу. Один каталог на оба
    /// позволил бы передать не то, что собирались.
    /// </summary>
    public required string ReportExportRoot { get; init; }

    /// <summary>
    /// Корень манифестов датасета.
    ///
    /// Отдельно от отчётов: отчёт остаётся у врача, а манифест уходит
    /// в исследовательский контур. Разные направления — разные каталоги.
    /// </summary>
    public required string DatasetManifestRoot { get; init; }

    /// <summary>
    /// Корень замороженного обезличенного набора выборки.
    ///
    /// Отдельно от манифестов датасета: там лежат выгруженные описания выборки,
    /// здесь — сами снимки набора, по которым ведётся рабочий список измерений.
    /// Уборкой рабочих копий не затрагивается (docs/data/README.md).
    /// </summary>
    public required string DatasetRoot { get; init; }

    /// <summary>
    /// Корень установленных пакетов модели.
    ///
    /// Версии лежат рядом, одна помечена действующей (ADR 0008): откат в клинике
    /// должен быть переключением метки, а не переустановкой файла, которого у
    /// администратора может уже не быть.
    /// </summary>
    public required string ModelRoot { get; init; }

    /// <summary>
    /// Файл с доверенным открытым ключом подписи пакетов модели.
    ///
    /// Лежит рядом с солью, в том же каталоге секретов. **Для выпуска этого
    /// недостаточно:** каталог профиля доступен пользователю на запись, и
    /// подменивший этот файл подменит и доверие к пакету, то есть защита от
    /// подмены модели (`docs/security/README.md`) сводится к защите этого файла.
    /// В выпускаемой сборке ключ обязан быть встроен в подписанный
    /// исполняемый файл; это условие записано в ADR 0008 и требует конвейера
    /// выпуска. Пока его нет, такое расположение — осознанное решение
    /// исследовательской сборки, а не упущение.
    ///
    /// Нет файла — нет и доверенного ключа: ни один пакет не пройдёт проверку,
    /// и приложение останется на пороговом пути. Это верный исход по умолчанию.
    /// </summary>
    public required string ModelTrustKeyPath { get; init; }

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
            ReportExportRoot = IoPath.Combine(root, "report-exports"),
            AuditLogPath = IoPath.Combine(root, "audit", "audit.log"),
            PseudonymSaltPath = IoPath.Combine(root, "secrets", "pseudonym-salt.bin"),
            DatasetManifestRoot = IoPath.Combine(root, "dataset-manifests"),
            DatasetRoot = IoPath.Combine(root, "dataset"),
            ModelRoot = IoPath.Combine(root, "models"),
            ModelTrustKeyPath = IoPath.Combine(root, "secrets", "model-trust.pub.pem"),
        };
    }
}
