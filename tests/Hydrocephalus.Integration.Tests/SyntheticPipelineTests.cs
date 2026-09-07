using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Synthetic end-to-end: импорт → QC → инференс → отчёт (M1 в docs/roadmap.md,
/// уровень Integration в tests/README.md). Проверяется последовательность сценария
/// и его поведение на отказе, отмене и ошибке — реальные адаптеры появятся в M2-M3.
/// </summary>
public sealed class SyntheticPipelineTests
{
    [Fact]
    public async Task Successful_run_produces_a_stored_report_and_an_ordered_audit_trail()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();
        var quality = QualityAssessment.Clean();

        var prediction = Prediction.Create(
            quality,
            Synthetic.Model(),
            [new ClassProbability(Synthetic.Inph, 0.72), new ClassProbability(Synthetic.Alzheimer, 0.18)],
            new Uncertainty { LowerBound = 0.6, UpperBound = 0.83, ConfidenceLevel = 0.95 });

        var useCase = UseCase(
            Synthetic.Study(),
            new StubInferenceEngine(quality, new AnalysisOutcome.Completed { Prediction = prediction }),
            store,
            audit);

        var stages = new List<AnalysisStage>();
        var progress = new Progress<AnalysisProgress>(update => stages.Add(update.Stage));

        var report = await useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress, CancellationToken.None);

        var completed = Assert.IsType<AnalysisOutcome.Completed>(report.Outcome);
        Assert.Equal(Synthetic.Inph, completed.Prediction.MostLikely.Class);

        // Отчёт сохранён ровно один раз и совпадает с возвращённым.
        Assert.Same(report, Assert.Single(store.Reports));

        // Provenance отчёта заполнено: без версий результат невоспроизводим.
        Assert.Equal("1.0.0", report.Pipeline.PreprocessingVersion);

        // Порядок событий важнее их наличия: отчёт не может быть сохранён раньше результата QC.
        Assert.Equal(
            [
                AuditEventCode.StudyImported,
                AuditEventCode.QualityControlCompleted,
                AuditEventCode.AnalysisStarted,
                AuditEventCode.AnalysisCompleted,
                AuditEventCode.ReportStored,
            ],
            audit.Codes);

        // Версия модели попадает в аудит (docs/architecture/README.md).
        Assert.Equal("0.1.0", audit.Events.Single(e => e.Code == AuditEventCode.AnalysisCompleted).ModelVersion);
    }

    [Fact]
    public async Task Failed_quality_control_refuses_without_reaching_the_classifier()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();

        var blocked = new QualityAssessment
        {
            Issues =
            [
                new QualityIssue
                {
                    Code = QualityIssueCode.MotionArtefact,
                    Severity = QualityIssueSeverity.Blocking,
                },
            ],
        };

        // Исход анализа не задан: если сценарий дойдёт до классификатора, тест упадёт.
        var useCase = UseCase(Synthetic.Study(), new StubInferenceEngine(blocked), store, audit);

        var report = await useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress: null, CancellationToken.None);

        var refused = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);
        Assert.Equal(RefusalCode.QualityControlFailed, refused.Reason.Code);
        Assert.Equal(QualityIssueCode.MotionArtefact, Assert.Single(refused.Reason.ContributingIssues).Code);

        // Отказ сохраняется как полноценный отчёт, но анализ не запускался.
        Assert.Single(store.Reports);
        Assert.DoesNotContain(AuditEventCode.AnalysisStarted, audit.Codes);
        Assert.Contains(AuditEventCode.AnalysisRefused, audit.Codes);
    }

    [Fact]
    public async Task Baseline_tier_study_is_refused_because_the_pipeline_requires_a_volume()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();

        // Постконтрастная серия — единственная в исследовании, значит анализировать нечего.
        var study = Synthetic.Study(contrastEnhanced: true);

        var useCase = UseCase(study, new StubInferenceEngine(QualityAssessment.Clean()), store, audit);

        var report = await useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress: null, CancellationToken.None);

        var refused = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);
        Assert.Equal(RefusalCode.InsufficientAcquisitionTier, refused.Reason.Code);
        Assert.DoesNotContain(AuditEventCode.QualityControlCompleted, audit.Codes);
    }

    [Fact]
    public async Task Cancellation_stores_no_report_but_leaves_a_trace_in_the_audit_log()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();

        using var cancellation = new CancellationTokenSource();

        var useCase = UseCase(
            Synthetic.Study(),
            new StubInferenceEngine(QualityAssessment.Clean(), cancelDuringAnalysis: cancellation),
            store,
            audit);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress: null, cancellation.Token));

        // Наблюдаемое состояние важнее самого исключения.
        Assert.Empty(store.Reports);
        Assert.DoesNotContain(AuditEventCode.ReportStored, audit.Codes);
        Assert.Contains(AuditEventCode.AnalysisCancelled, audit.Codes);
    }

    [Fact]
    public async Task Engine_failure_is_audited_and_no_partial_report_is_stored()
    {
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();

        var useCase = UseCase(
            Synthetic.Study(),
            new StubInferenceEngine(QualityAssessment.Clean(), failWith: new InvalidOperationException("engine failure")),
            store,
            audit);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress: null, CancellationToken.None));

        Assert.Empty(store.Reports);
        Assert.Contains(AuditEventCode.AnalysisFailed, audit.Codes);
    }

    [Fact]
    public async Task A_full_run_releases_the_working_copy_it_created()
    {
        var importer = new StubImporter(Synthetic.Study());

        var useCase = Build(
            importer,
            new StubInferenceEngine(QualityAssessment.Clean(), Completed()),
            new RecordingReportStore(),
            new RecordingAuditLog());

        await useCase.ExecuteAsync("source/study-0001", Synthetic.Clinician(), progress: null, CancellationToken.None);

        Assert.Single(importer.Released);
    }

    [Fact]
    public async Task Analysing_an_already_imported_copy_leaves_its_lifetime_to_the_caller()
    {
        // Экран просмотра держит рабочую копию открытой, пока по ней смотрят
        // изображение. Если бы анализ освобождал её, картинка осталась бы без
        // данных ровно в тот момент, когда получен отчёт по ней же.
        var importer = new StubImporter(Synthetic.Study());
        var store = new RecordingReportStore();

        var useCase = Build(
            importer,
            new StubInferenceEngine(QualityAssessment.Clean(), Completed()),
            store,
            new RecordingAuditLog());

        var workingCopy = await importer.ImportAsync("source/study-0001", CancellationToken.None);

        var report = await useCase.AnalyseWorkingCopyAsync(
            workingCopy,
            Synthetic.Clinician(),
            progress: null,
            CancellationToken.None);

        Assert.Same(report, Assert.Single(store.Reports));
        Assert.Empty(importer.Released);
    }

    [Fact]
    public async Task An_unconfigured_installation_cannot_analyse_and_nothing_is_imported()
    {
        // Право проверяется до импорта: импорт создаёт рабочую копию —
        // расшифрованные данные пациента на диске. Событие StudyImported
        // в журнале означало бы, что копия всё-таки была создана ради того,
        // чтобы затем отказать.
        var audit = new RecordingAuditLog();
        var store = new RecordingReportStore();

        var useCase = Build(
            new StubImporter(Synthetic.Study()),
            new StubInferenceEngine(QualityAssessment.Clean(), Completed()),
            store,
            audit);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => useCase.ExecuteAsync(
                "source/study-0001",
                Synthetic.Unconfigured(),
                progress: null,
                CancellationToken.None));

        Assert.Equal([AuditEventCode.AccessDenied], audit.Codes);
        Assert.Empty(store.Reports);
    }

    [Fact]
    public async Task An_unconfigured_installation_cannot_analyse_an_already_imported_copy_either()
    {
        // Второй вход в сценарий обязан проверять то же самое: право,
        // выполняемое только на одном из путей, не является правом.
        var importer = new StubImporter(Synthetic.Study());
        var audit = new RecordingAuditLog();

        var useCase = Build(
            importer,
            new StubInferenceEngine(QualityAssessment.Clean(), Completed()),
            new RecordingReportStore(),
            audit);

        var workingCopy = await importer.ImportAsync("source/study-0001", CancellationToken.None);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => useCase.AnalyseWorkingCopyAsync(
                workingCopy,
                Synthetic.Unconfigured(),
                progress: null,
                CancellationToken.None));

        Assert.Equal([AuditEventCode.AccessDenied], audit.Codes);
    }

    [Fact]
    public async Task Every_event_of_a_run_names_who_started_it()
    {
        // Журнал, по которому нельзя сказать, кто запускал анализ,
        // не отвечает на первый же вопрос разбора.
        var audit = new RecordingAuditLog();

        var useCase = Build(
            new StubImporter(Synthetic.Study()),
            new StubInferenceEngine(QualityAssessment.Clean(), Completed()),
            new RecordingReportStore(),
            audit);

        var actor = Synthetic.Clinician();

        await useCase.ExecuteAsync("source/study-0001", actor, progress: null, CancellationToken.None);

        Assert.All(
            audit.Events,
            recorded => Assert.Equal(actor.PseudonymousUserId, recorded.PseudonymousActorId));
    }

    private static AnalysisOutcome.Completed Completed() => new()
    {
        Prediction = Prediction.Create(
            QualityAssessment.Clean(),
            Synthetic.Model(),
            [new ClassProbability(Synthetic.Inph, 0.72), new ClassProbability(Synthetic.Alzheimer, 0.18)],
            new Uncertainty { LowerBound = 0.6, UpperBound = 0.83, ConfidenceLevel = 0.95 }),
    };

    private static AnalyzeStudyUseCase UseCase(
        ImagingStudy study,
        IInferenceEngine engine,
        IReportStore store,
        IAuditLog audit) =>
        Build(new StubImporter(study), engine, store, audit);

    private static AnalyzeStudyUseCase Build(
        StubImporter importer,
        IInferenceEngine engine,
        IReportStore store,
        IAuditLog audit) =>
        new(importer, importer, engine, store, audit, TimeProvider.System);
}
