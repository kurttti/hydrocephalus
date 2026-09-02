namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Событие аудита. Состав перечня задан docs/architecture/README.md:
/// импорт, результат QC, запуск/отмена анализа, версия модели, экспорт отчёта и ошибка.
/// </summary>
public enum AuditEventCode
{
    /// <summary>Код не задан.</summary>
    Unspecified = 0,

    /// <summary>Исследование импортировано, создана рабочая копия.</summary>
    StudyImported = 1,

    /// <summary>Завершён входной контроль качества.</summary>
    QualityControlCompleted = 2,

    /// <summary>Анализ запущен.</summary>
    AnalysisStarted = 3,

    /// <summary>Анализ завершён прогнозом.</summary>
    AnalysisCompleted = 4,

    /// <summary>Система отказалась от ответа.</summary>
    AnalysisRefused = 5,

    /// <summary>Анализ отменён пользователем.</summary>
    AnalysisCancelled = 6,

    /// <summary>Анализ завершился ошибкой.</summary>
    AnalysisFailed = 7,

    /// <summary>Отчёт сохранён.</summary>
    ReportStored = 8,
}

/// <summary>
/// Запись аудита. Содержит только псевдонимные идентификаторы и технические коды:
/// пиксельные данные и исходные DICOM-теги в журнал не попадают.
/// </summary>
public sealed record AuditEvent
{
    /// <summary>Код события.</summary>
    public required AuditEventCode Code { get; init; }

    /// <summary>Момент события.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Псевдонимный идентификатор исследования, если событие относится к нему.</summary>
    public string? PseudonymousStudyId { get; init; }

    /// <summary>Версия model package, если событие связано с анализом.</summary>
    public string? ModelVersion { get; init; }
}

/// <summary>
/// Журнал аудита. Защищается от незаметного редактирования на уровне реализации
/// (docs/security/README.md).
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Записывает событие аудита.
    /// </summary>
    /// <param name="auditEvent">Событие.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}
