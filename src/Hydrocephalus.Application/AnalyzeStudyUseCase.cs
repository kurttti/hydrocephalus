using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Application;

/// <summary>
/// Сценарий анализа исследования: импорт, выбор серии, контроль качества, инференс,
/// формирование и сохранение отчёта, запись аудита.
///
/// Слой отвечает за последовательность и границы операции; медицинские вычисления
/// делегируются <see cref="IInferenceEngine"/>, ввод-вывод — портам инфраструктуры.
/// </summary>
public sealed class AnalyzeStudyUseCase
{
    private readonly IStudyImporter importer;
    private readonly IWorkingCopyLifetime workingCopyLifetime;
    private readonly IInferenceEngine engine;
    private readonly IReportStore reportStore;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="importer">Импорт и создание рабочей копии.</param>
    /// <param name="workingCopyLifetime">Освобождение рабочей копии.</param>
    /// <param name="engine">Конвейер анализа.</param>
    /// <param name="reportStore">Хранилище отчётов.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public AnalyzeStudyUseCase(
        IStudyImporter importer,
        IWorkingCopyLifetime workingCopyLifetime,
        IInferenceEngine engine,
        IReportStore reportStore,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(workingCopyLifetime);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(reportStore);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.importer = importer;
        this.workingCopyLifetime = workingCopyLifetime;
        this.engine = engine;
        this.reportStore = reportStore;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Выполняет сценарий целиком.
    /// </summary>
    /// <param name="sourceReference">Ссылка на источник исследования.</param>
    /// <param name="requestedBy">Тот, от чьего имени выполняется анализ.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сохранённый отчёт с прогнозом либо отказом.</returns>
    /// <exception cref="OperationCanceledException">Если операция отменена.</exception>
    /// <exception cref="AccessDeniedException">
    /// Если у инициатора нет права на анализ.
    /// </exception>
    public async Task<AnalysisReport> ExecuteAsync(
        string sourceReference,
        Actor requestedBy,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Право проверяется до импорта, а не после. Импорт создаёт рабочую
        // копию — расшифрованные данные пациента на диске, — и создавать её
        // ради того, чтобы затем отказать, значит выполнить именно ту часть
        // работы, от которой разграничение и защищает.
        await this.RequireAsync(requestedBy, cancellationToken).ConfigureAwait(false);

        var workingCopy = await this.importer.ImportAsync(sourceReference, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await this.AnalyseAsync(workingCopy, requestedBy, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Освобождение в finally, а не после успеха: на ошибке и отмене
            // рабочая копия остаётся на диске ровно так же, и «уберём потом»
            // означает не уберём. Уничтожение начинается с ключа, поэтому
            // прерывание здесь всё равно делает данные нечитаемыми.
            await this.workingCopyLifetime.ReleaseAsync(workingCopy.VolumeReference)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Анализирует уже импортированную рабочую копию.
    ///
    /// Отличие от <see cref="ExecuteAsync"/> одно, и оно принципиальное:
    /// рабочую копию сюда передают, а не создают здесь, и освобождает её
    /// вызывающий. Нужно это тому, кто уже держит рабочую копию открытой —
    /// экрану просмотра. Иначе анализ импортировал бы то же исследование
    /// второй раз, и на диске оказалось бы две копии одних и тех же данных
    /// пациента, а измеряли бы не то, что показано на экране.
    ///
    /// Ответственность за освобождение переходит к вызывающему целиком:
    /// освободить здесь значило бы уничтожить копию, с которой тот работает.
    /// </summary>
    /// <param name="workingCopy">Рабочая копия, созданная вызывающим.</param>
    /// <param name="requestedBy">Тот, от чьего имени выполняется анализ.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сохранённый отчёт с прогнозом либо отказом.</returns>
    /// <exception cref="OperationCanceledException">Если операция отменена.</exception>
    /// <exception cref="AccessDeniedException">
    /// Если у инициатора нет права на анализ.
    /// </exception>
    public async Task<AnalysisReport> AnalyseWorkingCopyAsync(
        WorkingCopy workingCopy,
        Actor requestedBy,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);

        await this.RequireAsync(requestedBy, cancellationToken).ConfigureAwait(false);

        return await this.AnalyseAsync(workingCopy, requestedBy, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AnalysisReport> AnalyseAsync(
        WorkingCopy workingCopy,
        Actor requestedBy,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var study = workingCopy.Study;

        await this.RecordAsync(
                AuditEventCode.StudyImported,
                study.PseudonymousStudyId,
                requestedBy,
                cancellationToken)
            .ConfigureAwait(false);

        var pipeline = await this.engine.DescribePipelineAsync(cancellationToken).ConfigureAwait(false);

        // Выбор серии: анализируется лучшая доступная неконтрастная серия.
        // Постконтрастные серии не подаются в MRI-only конвейер ни на одном уровне входа.
        var series = SelectAnalysableSeries(study);

        if (series is null)
        {
            return await this.RefuseAsync(
                    study,
                    pipeline,
                    QualityAssessment.Clean(),
                    new RefusalReason { Code = RefusalCode.InsufficientAcquisitionTier },
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var request = new AnalysisRequest
        {
            Study = study,
            PseudonymousSeriesId = series.PseudonymousSeriesId,
            VolumeReference = workingCopy.VolumeReference,
        };

        var quality = await this.engine.RunQualityControlAsync(request, cancellationToken)
            .ConfigureAwait(false);

        await this.RecordAsync(
                AuditEventCode.QualityControlCompleted,
                study.PseudonymousStudyId,
                requestedBy,
                cancellationToken)
            .ConfigureAwait(false);

        if (!quality.IsAcceptable)
        {
            return await this.RefuseAsync(
                    study,
                    pipeline,
                    quality,
                    RefusalReason.FromFailedQualityControl(quality),
                    requestedBy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await this.RecordAsync(
                AuditEventCode.AnalysisStarted,
                study.PseudonymousStudyId,
                requestedBy,
                cancellationToken)
            .ConfigureAwait(false);

        AnalysisResult result;

        try
        {
            result = await this.engine.AnalyzeAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена — ожидаемый путь: отчёт не сохраняется, но след в аудите остаётся.
            await this.RecordAsync(
                    AuditEventCode.AnalysisCancelled,
                    study.PseudonymousStudyId,
                    requestedBy,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await this.RecordAsync(
                    AuditEventCode.AnalysisFailed,
                    study.PseudonymousStudyId,
                    requestedBy,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        var outcome = result.Outcome;

        var modelVersion = outcome is AnalysisOutcome.Completed completed
            ? completed.Prediction.Model.Version
            : null;

        await this.RecordAsync(
                outcome is AnalysisOutcome.Completed ? AuditEventCode.AnalysisCompleted : AuditEventCode.AnalysisRefused,
                study.PseudonymousStudyId,
                requestedBy,
                cancellationToken,
                modelVersion)
            .ConfigureAwait(false);

        var report = new AnalysisReport
        {
            PseudonymousStudyId = study.PseudonymousStudyId,
            CreatedAt = this.timeProvider.GetUtcNow(),
            Quality = quality,
            Outcome = outcome,
            Pipeline = pipeline,

            // Измерения попадают в отчёт и при отказе классификации: отказ
            // относится к прогнозу диагноза, а объём желудочков измерен
            // независимо от него (ADR 0005).
            Biomarkers = result.Biomarkers,
        };

        return await this.StoreAsync(report, requestedBy, modelVersion, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Выбирает серию для анализа: наилучший доступный уровень входа среди неконтрастных серий.
    ///
    /// Открыт наружу, потому что этот же выбор нужен экрану: показывать одну
    /// серию, а измерять другую нельзя, а вторая копия правила разошлась бы
    /// с первой незаметно — на экране была бы подпись не к тому изображению.
    /// </summary>
    /// <param name="study">Исследование из рабочей копии.</param>
    /// <returns>Выбранная серия либо <see langword="null"/>, если подходящей нет.</returns>
    public static ImagingSeries? SelectAnalysableSeries(ImagingStudy study)
    {
        ArgumentNullException.ThrowIfNull(study);

        return study.Series
            .Where(series => !series.IsContrastEnhanced && series.Tier != AcquisitionTier.Unusable)
            .OrderByDescending(series => series.Tier)
            .FirstOrDefault();
    }

    private async Task<AnalysisReport> RefuseAsync(
        ImagingStudy study,
        Domain.Provenance.PipelineIdentity pipeline,
        QualityAssessment quality,
        RefusalReason reason,
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        await this.RecordAsync(
                AuditEventCode.AnalysisRefused,
                study.PseudonymousStudyId,
                requestedBy,
                cancellationToken)
            .ConfigureAwait(false);

        var report = new AnalysisReport
        {
            PseudonymousStudyId = study.PseudonymousStudyId,
            CreatedAt = this.timeProvider.GetUtcNow(),
            Quality = quality,
            Outcome = new AnalysisOutcome.Refused { Reason = reason },
            Pipeline = pipeline,
        };

        return await this.StoreAsync(report, requestedBy, modelVersion: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<AnalysisReport> StoreAsync(
        AnalysisReport report,
        Actor requestedBy,
        string? modelVersion,
        CancellationToken cancellationToken)
    {
        await this.reportStore.StoreAsync(report, cancellationToken).ConfigureAwait(false);

        await this.RecordAsync(
                AuditEventCode.ReportStored,
                report.PseudonymousStudyId,
                requestedBy,
                cancellationToken,
                modelVersion)
            .ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Требует право на анализ и записывает отказ в журнал.
    ///
    /// След от неудавшейся попытки нужен именно потому, что она не удалась:
    /// иначе выход за пределы своих прав не оставляет в системе ничего.
    /// </summary>
    private async Task RequireAsync(Actor requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (requestedBy.Can(Capability.AnalyseStudy))
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

        throw AccessDeniedException.For(requestedBy.Role, Capability.AnalyseStudy);
    }

    private Task RecordAsync(
        AuditEventCode code,
        string pseudonymousStudyId,
        Actor requestedBy,
        CancellationToken cancellationToken,
        string? modelVersion = null) =>
        this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = code,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousStudyId = pseudonymousStudyId,

                // Инициатор попадает в каждое событие разбора, а не только
                // в события экспорта: журнал, по которому нельзя сказать, кто
                // запускал анализ, не отвечает на первый же вопрос разбора.
                PseudonymousActorId = requestedBy.PseudonymousUserId,
                ModelVersion = modelVersion,
            },
            cancellationToken);
}
