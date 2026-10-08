using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Administration;

/// <summary>
/// Состояние установки для экрана администрирования.
///
/// Собирается на composition root: только там известно и где лежат файлы,
/// и что из необязательного подключено. Экран получает готовое состояние
/// и не ходит за ним по слоям (docs/architecture/README.md).
///
/// Пути показываются, потому что обслуживание установки — это работа с ними:
/// где искать рабочие копии, куда уходят экспортированные отчёты и где лежит
/// журнал. Содержимого файлов на экране нет, только расположение.
/// </summary>
public sealed record AdministrationSnapshot
{
    /// <summary>Журнал аудита вместе с проверкой цепочки.</summary>
    public required AuditJournal Journal { get; init; }

    /// <summary>Файл журнала аудита.</summary>
    public required string AuditLogPath { get; init; }

    /// <summary>Корень рабочих копий.</summary>
    public required string WorkingCopyRoot { get; init; }

    /// <summary>Корень сохранённых отчётов.</summary>
    public required string ReportRoot { get; init; }

    /// <summary>Корень экспортированных отчётов.</summary>
    public required string ReportExportRoot { get; init; }

    /// <summary>Корень манифестов датасета.</summary>
    public required string DatasetManifestRoot { get; init; }

    /// <summary>Действующая политика хранения рабочих копий.</summary>
    public required WorkingCopyRetentionPolicy Retention { get; init; }

    /// <summary>Подключён ли внешний реестр идентификаторов пациента.</summary>
    public required bool PatientRegistryConfigured { get; init; }

    /// <summary>Версии конвейера, попадающие в отчёт.</summary>
    public required PipelineIdentity Pipeline { get; init; }

    /// <summary>Состояние установленных пакетов модели.</summary>
    public required ModelInstallationState Models { get; init; }
}

/// <summary>
/// Состояние установленных пакетов модели для экрана администрирования.
///
/// Разделены два вопроса, которые на экране легко слить в один: чем приложение
/// измеряет **сейчас** и какая версия помечена действующей. Способ разметки
/// выбирается один раз при запуске, поэтому новая метка начинает действовать
/// только со следующего запуска (см. ADR 0008). Экран, показывающий одну
/// строку, заставил бы администратора принять неизменившееся значение за
/// неудачу переключения.
/// </summary>
public sealed record ModelInstallationState
{
    /// <summary>Установленные версии; пусто, если не установлено ничего.</summary>
    public required IReadOnlyList<string> InstalledVersions { get; init; }

    /// <summary>Версия, помеченная действующей; <see langword="null"/>, если её нет.</summary>
    public required string? ActiveVersion { get; init; }

    /// <summary>
    /// Чем размечаются желудочки в этом запуске — строка происхождения,
    /// та же, что попадает в отчёт.
    /// </summary>
    public required string MeasuringNow { get; init; }

    /// <summary>Объявление действующего пакета; <see langword="null"/>, если не прочитано.</summary>
    public required ModelPackageManifest? ActiveManifest { get; init; }

    /// <summary>Состояние подписи действующего пакета на момент открытия экрана.</summary>
    public required ModelPackageSignature ActiveSignature { get; init; }

    /// <summary>Причина отказа действующего пакета; <see langword="null"/>, если принят.</summary>
    public required ModelPackageRejection? ActiveRejection { get; init; }

    /// <summary>Уточнение к причине отказа. Не содержит PHI.</summary>
    public required string ActiveDetail { get; init; }

    /// <summary>
    /// Есть ли чем проверять пакеты.
    ///
    /// Без доверенного ключа установка невозможна вовсе, и это не поломка,
    /// а незавершённая настройка установки: подпись проверяется ключом,
    /// который приносит не пакет (ADR 0004).
    /// </summary>
    public required bool TrustKeyConfigured { get; init; }

    /// <summary>Каталог, в котором лежат пакеты.</summary>
    public required string ModelRoot { get; init; }

    /// <summary>
    /// Открыто ли исследование.
    ///
    /// ADR 0008 запрещает переключение версии во время выполняющегося анализа.
    /// Пока исследование открыто, установка и переключение недоступны.
    /// </summary>
    public required bool StudyOpen { get; init; }
}
