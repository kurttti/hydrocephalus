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
}
