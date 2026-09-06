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

    /// <summary>
    /// Экспортирован манифест датасета в исследовательский контур.
    ///
    /// Отдельное событие, а не разновидность экспорта отчёта: манифест уходит
    /// наружу и описывает состав выборки. Кто и когда его запросил, должно быть
    /// видно в журнале так же ясно, как факт анализа.
    /// </summary>
    DatasetManifestExported = 9,

    /// <summary>
    /// Операция отклонена по правам доступа.
    ///
    /// Отдельный код, а не отсутствие записи: несанкционированный экспорт
    /// назван отдельной угрозой (docs/security/README.md), и попытка,
    /// не оставившая следа, ничем не отличается от её отсутствия. Спутать
    /// с состоявшимся экспортом такую запись нельзя — у неё другой код.
    /// </summary>
    AccessDenied = 10,
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

    /// <summary>
    /// Псевдонимный идентификатор инициатора, если событие вызвано человеком.
    ///
    /// Псевдоним, а не имя: журнал не должен нести персональных данных
    /// ни о пациенте, ни о сотруднике (docs/security/README.md).
    /// </summary>
    public string? PseudonymousActorId { get; init; }
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
