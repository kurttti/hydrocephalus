using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Application;

/// <summary>
/// Записывает в отчёт измерение, выполненное врачом вручную.
///
/// Записывается новая версия отчёта, а не правка прежней. Так устроено
/// хранилище: сохранённый отчёт неизменен, и положить по тому же пути другое
/// содержимое оно не даёт (ADR 0005). Это не обход ограничения, а то, ради чего
/// оно сделано — отчёт мог уже уйти врачу, и переписать его задним числом
/// нельзя. Прежние версии остаются на диске, и история измерений складывается
/// сама.
///
/// Отсюда правило чтения: **последнее ручное измерение исследования ищется
/// перебором всех его версий, а не в последней.** Повторное открытие
/// исследования заново запускает анализ и сохраняет отчёт без ручной отметки,
/// поэтому самая свежая версия её может не содержать. То же правило поглощает
/// и случайное двойное нажатие: две версии с одинаковым значением дают одно
/// измерение.
/// </summary>
public sealed class RecordManualMeasurementUseCase
{
    private readonly IReportStore reportStore;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="reportStore">Хранилище отчётов.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public RecordManualMeasurementUseCase(
        IReportStore reportStore,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(reportStore);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.reportStore = reportStore;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Дописывает измерение к отчёту и сохраняет новую версию.
    /// </summary>
    /// <param name="report">Отчёт открытого исследования.</param>
    /// <param name="measurements">
    /// Признаки одного измерения. Их несколько: вместе с индексом Эванса
    /// записывается поворот головы, выведенный из заданной врачом оси. Пишутся
    /// они одной версией отчёта, потому что получены одним действием — раздельная
    /// запись дала бы две версии и два следа в журнале на одно измерение.
    /// </param>
    /// <param name="requestedBy">Кто записывает.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Новая версия отчёта.</returns>
    /// <exception cref="AccessDeniedException">Если у роли нет права на запись измерения.</exception>
    public async Task<AnalysisReport> ExecuteAsync(
        AnalysisReport report,
        IReadOnlyList<Biomarker> measurements,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(measurements);
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (measurements.Count == 0)
        {
            throw new ArgumentException("There is nothing to record.", nameof(measurements));
        }

        // Неправдоподобное значение не записывается. Проверка стоит здесь, а не
        // только в окне, потому что хранилище неизменно: записанное останется
        // навсегда, а окно — не единственный возможный вызывающий. Обнаружено
        // на прогоне, где точки пришли в другом порядке: индекс вышел 3,02
        // вместо 0,33 и лёг на диск, потому что интерфейс был обойдён.
        //
        // Автоматический путь такое значение записывает с пометкой, и это не
        // противоречие: там исправить его некому, а здесь врач ставит точки
        // заново.
        var implausible = measurements.FirstOrDefault(item => item.IsOutOfRange);

        if (implausible is not null)
        {
            throw new DomainRuleViolationException(
                $"Measurement '{implausible.Method.Code}' lies outside its plausible range "
                + "and is not recorded; the points have to be placed again.");
        }

        await this.RequireAsync(requestedBy, cancellationToken).ConfigureAwait(false);

        // Измерение того же метода заменяется, а не добавляется вторым: две
        // записи одного кода в одном отчёте не сказали бы, какая из них верна.
        // Прежнее значение при этом не теряется — оно осталось в своей версии.
        var replaced = measurements
            .Select(item => item.Method.Code)
            .ToHashSet(StringComparer.Ordinal);

        var biomarkers = report.Biomarkers
            .Where(existing => !replaced.Contains(existing.Method.Code))
            .Concat(measurements)
            .ToList();

        var recorded = report with
        {
            CreatedAt = this.timeProvider.GetUtcNow(),
            Biomarkers = biomarkers,
        };

        await this.reportStore.StoreAsync(recorded, cancellationToken).ConfigureAwait(false);

        await this.RecordAsync(
                AuditEventCode.MeasurementRecordedByClinician,
                recorded,
                requestedBy,
                cancellationToken)
            .ConfigureAwait(false);

        // Событие сохранения записывается тоже: иначе число сохранённых отчётов
        // в журнале расходилось бы с числом файлов на диске.
        await this.RecordAsync(AuditEventCode.ReportStored, recorded, requestedBy, cancellationToken)
            .ConfigureAwait(false);

        return recorded;
    }

    /// <summary>
    /// Требует право на запись измерения и записывает отказ в журнал.
    ///
    /// След от неудавшейся попытки нужен именно потому, что она не удалась:
    /// иначе выход за пределы своих прав не оставляет в системе ничего.
    /// </summary>
    private async Task RequireAsync(Actor requestedBy, CancellationToken cancellationToken)
    {
        if (requestedBy.Can(Capability.RecordMeasurement))
        {
            return;
        }

        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.AccessDenied,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousActorId = requestedBy.PseudonymousUserId,
            },
            cancellationToken).ConfigureAwait(false);

        throw AccessDeniedException.For(requestedBy.Role, Capability.RecordMeasurement);
    }

    private Task RecordAsync(
        AuditEventCode code,
        AnalysisReport report,
        Actor requestedBy,
        CancellationToken cancellationToken) =>
        this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = code,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousStudyId = report.PseudonymousStudyId,
                PseudonymousActorId = requestedBy.PseudonymousUserId,
            },
            cancellationToken);
}
