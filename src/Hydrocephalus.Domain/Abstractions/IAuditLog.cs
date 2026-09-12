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

    /// <summary>
    /// Отчёт экспортирован. Вариант записывается отдельным полем: клинический
    /// и обезличенный различаются составом, и «экспорт был» без указания
    /// варианта не отвечает на главный вопрос — ушли ли наружу идентификаторы.
    /// </summary>
    ReportExported = 11,

    /// <summary>
    /// Выполнена уборка рабочих копий.
    ///
    /// ADR 0006 требует, чтобы удаление по сроку оставляло след в журнале.
    /// Без записи политика хранения недоказуема: по диску видно только, что
    /// каталогов нет, а это выглядит одинаково и при исправной уборке,
    /// и при том, что её никогда не было.
    /// </summary>
    WorkingCopiesSwept = 12,
}

/// <summary>
/// Итог уборки рабочих копий.
///
/// Причины удаления разделены, потому что ADR 0006 разделяет их как разные
/// требования: срок жизни и уборка за прерванным сеансом. Одно число на двоих
/// не отвечает ни на один из двух вопросов.
///
/// Неудача — тоже факт для журнала: «данные пациента должны были исчезнуть
/// и не исчезли» — ровно то, ради чего журнал ведётся. Ключ к этому моменту
/// уже удалён, поэтому содержимое нечитаемо, но каталог остался.
/// </summary>
/// <param name="Expired">Сеансов удалено по истечении срока жизни.</param>
/// <param name="Orphaned">Сеансов удалено как осиротевшие: без ключа или без отметки времени.</param>
/// <param name="Failed">Сеансов, которые не удалось удалить.</param>
/// <param name="TimeToLiveHours">Срок жизни, по которому выполнялась уборка.</param>
public readonly record struct WorkingCopyRetentionOutcome(
    int Expired,
    int Orphaned,
    int Failed,
    double TimeToLiveHours);

/// <summary>
/// Уборка рабочих копий по политике хранения (ADR 0006).
///
/// Порт, а не статический вызов: уборка удаляет данные пациента, и событие
/// об этом обязано попасть в журнал — а журнал ведут сценарии, а не файловый код.
/// </summary>
public interface IWorkingCopyRetention
{
    /// <summary>
    /// Убирает просроченные и осиротевшие рабочие копии.
    /// </summary>
    /// <param name="now">Текущий момент.</param>
    /// <returns>Что было убрано и по какому сроку.</returns>
    WorkingCopyRetentionOutcome Sweep(DateTimeOffset now);
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

    /// <summary>
    /// Вариант экспорта отчёта, если событие относится к экспорту.
    /// </summary>
    public Reporting.ReportExportVariant? ReportExportVariant { get; init; }

    /// <summary>
    /// Итог уборки рабочих копий, если событие относится к ней.
    ///
    /// Уборка не относится ни к исследованию, ни к человеку: каталог сеанса
    /// назван случайным идентификатором именно для того, чтобы не связывать
    /// копию с исследованием, а запускается уборка при старте, а не по чьей-то
    /// команде. Поэтому остальные поля у такого события пусты, и без этого
    /// в журнале осталась бы запись «что-то убрали».
    /// </summary>
    public WorkingCopyRetentionOutcome? Retention { get; init; }
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
