using Hydrocephalus.Domain.Abstractions;
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
    private readonly IInferenceEngine engine;
    private readonly IReportStore reportStore;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="importer">Импорт и создание рабочей копии.</param>
    /// <param name="engine">Конвейер анализа.</param>
    /// <param name="reportStore">Хранилище отчётов.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public AnalyzeStudyUseCase(
        IStudyImporter importer,
        IInferenceEngine engine,
        IReportStore reportStore,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(reportStore);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.importer = importer;
        this.engine = engine;
        this.reportStore = reportStore;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Выполняет сценарий целиком.
    /// </summary>
    /// <param name="sourceReference">Ссылка на источник исследования.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Сохранённый отчёт с прогнозом либо отказом.</returns>
    /// <exception cref="OperationCanceledException">Если операция отменена.</exception>
    public async Task<AnalysisReport> ExecuteAsync(
        string sourceReference,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var workingCopy = await this.importer.ImportAsync(sourceReference, cancellationToken)
            .ConfigureAwait(false);

        var study = workingCopy.Study;

        await this.RecordAsync(AuditEventCode.StudyImported, study.PseudonymousStudyId, cancellationToken)
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

        await this.RecordAsync(AuditEventCode.QualityControlCompleted, study.PseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);

        if (!quality.IsAcceptable)
        {
            return await this.RefuseAsync(
                    study,
                    pipeline,
                    quality,
                    RefusalReason.FromFailedQualityControl(quality),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await this.RecordAsync(AuditEventCode.AnalysisStarted, study.PseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);

        AnalysisOutcome outcome;

        try
        {
            outcome = await this.engine.AnalyzeAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена — ожидаемый путь: отчёт не сохраняется, но след в аудите остаётся.
            await this.RecordAsync(AuditEventCode.AnalysisCancelled, study.PseudonymousStudyId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await this.RecordAsync(AuditEventCode.AnalysisFailed, study.PseudonymousStudyId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        var modelVersion = outcome is AnalysisOutcome.Completed completed
            ? completed.Prediction.Model.Version
            : null;

        await this.RecordAsync(
                outcome is AnalysisOutcome.Completed ? AuditEventCode.AnalysisCompleted : AuditEventCode.AnalysisRefused,
                study.PseudonymousStudyId,
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
        };

        return await this.StoreAsync(report, modelVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Выбирает серию для анализа: наилучший доступный уровень входа среди неконтрастных серий.
    /// </summary>
    private static ImagingSeries? SelectAnalysableSeries(ImagingStudy study) =>
        study.Series
            .Where(series => !series.IsContrastEnhanced && series.Tier != AcquisitionTier.Unusable)
            .OrderByDescending(series => series.Tier)
            .FirstOrDefault();

    private async Task<AnalysisReport> RefuseAsync(
        ImagingStudy study,
        Domain.Provenance.PipelineIdentity pipeline,
        QualityAssessment quality,
        RefusalReason reason,
        CancellationToken cancellationToken)
    {
        await this.RecordAsync(AuditEventCode.AnalysisRefused, study.PseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);

        var report = new AnalysisReport
        {
            PseudonymousStudyId = study.PseudonymousStudyId,
            CreatedAt = this.timeProvider.GetUtcNow(),
            Quality = quality,
            Outcome = new AnalysisOutcome.Refused { Reason = reason },
            Pipeline = pipeline,
        };

        return await this.StoreAsync(report, modelVersion: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AnalysisReport> StoreAsync(
        AnalysisReport report,
        string? modelVersion,
        CancellationToken cancellationToken)
    {
        await this.reportStore.StoreAsync(report, cancellationToken).ConfigureAwait(false);

        await this.RecordAsync(
                AuditEventCode.ReportStored,
                report.PseudonymousStudyId,
                cancellationToken,
                modelVersion)
            .ConfigureAwait(false);

        return report;
    }

    private Task RecordAsync(
        AuditEventCode code,
        string pseudonymousStudyId,
        CancellationToken cancellationToken,
        string? modelVersion = null) =>
        this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = code,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousStudyId = pseudonymousStudyId,
                ModelVersion = modelVersion,
            },
            cancellationToken);
}
