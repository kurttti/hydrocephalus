using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Экспорт отчёта в двух вариантах (ADR 0005).
///
/// Разграничение проверяется здесь, на уровне сценария, а не на отрисовке:
/// рендерер не решает, что скрывать. Ошибка в этом месте не выглядит как сбой —
/// наружу уходит файл с идентификаторами, и отозвать его нельзя.
/// </summary>
public sealed class ExportReportTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly Actor Clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);
    private static readonly Actor Researcher = Actor.Create("researcher-1", ClinicalRole.Researcher);

    [Fact]
    public async Task A_clinician_exports_the_clinical_variant_with_identifiers()
    {
        var store = new RecordingExportStore();

        await UseCase(store, KnownPatient()).ExecuteAsync(
            Request(ReportExportVariant.Clinical, Clinician),
            CancellationToken.None);

        var written = Assert.Single(store.Written);

        Assert.Equal(ReportExportVariant.Clinical, written.Variant);
        Assert.NotNull(written.PatientIdentity);
        Assert.Equal("Иванов Иван Иванович", written.PatientIdentity.FullName);
    }

    [Fact]
    public async Task A_deidentified_export_carries_no_identifiers()
    {
        var store = new RecordingExportStore();

        await UseCase(store, KnownPatient()).ExecuteAsync(
            Request(ReportExportVariant.Deidentified, Researcher),
            CancellationToken.None);

        var written = Assert.Single(store.Written);

        Assert.Equal(ReportExportVariant.Deidentified, written.Variant);
        Assert.Null(written.PatientIdentity);
    }

    [Fact]
    public async Task The_registry_is_not_consulted_for_a_deidentified_export()
    {
        // Обращение к реестру ради файла, которому идентификаторы не нужны,
        // — лишний повод их достать.
        var registry = KnownPatient();

        await UseCase(new RecordingExportStore(), registry).ExecuteAsync(
            Request(ReportExportVariant.Deidentified, Researcher),
            CancellationToken.None);

        Assert.Equal(0, registry.Calls);
    }

    [Fact]
    public async Task A_researcher_may_not_export_the_clinical_variant()
    {
        // Клинический вариант несёт идентификаторы пациента, а исследовательская
        // задача в них не нуждается.
        var store = new RecordingExportStore();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(store, KnownPatient()).ExecuteAsync(
                Request(ReportExportVariant.Clinical, Researcher),
                CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task A_denied_export_is_audited_and_writes_nothing()
    {
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(new RecordingExportStore(), KnownPatient(), audit).ExecuteAsync(
                Request(ReportExportVariant.Clinical, Researcher),
                CancellationToken.None));

        Assert.Equal(AuditEventCode.AccessDenied, Assert.Single(audit.Events).Code);
    }

    [Fact]
    public async Task An_unknown_variant_is_refused_rather_than_guessed()
    {
        // Выбрать за пользователя более безопасный вариант нельзя: он мог иметь
        // в виду другой, и молчаливая подмена варианта — подмена содержимого.
        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(new RecordingExportStore(), KnownPatient()).ExecuteAsync(
                Request(ReportExportVariant.Unspecified, Clinician),
                CancellationToken.None));
    }

    [Fact]
    public async Task A_clinical_export_of_an_unknown_study_is_refused()
    {
        // Выдать обезличенный файл под клиническим названием нельзя: врач
        // решит, что перед ним карта конкретного пациента.
        var store = new RecordingExportStore();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(store, new EmptyRegistry()).ExecuteAsync(
                Request(ReportExportVariant.Clinical, Clinician),
                CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task A_deidentified_export_of_annotated_report_needs_an_acknowledgement()
    {
        // Комментарии врача — свободный текст и могут содержать введённую
        // вручную PHI. Вырезать их молча нельзя, отправить не предупредив тоже.
        var store = new RecordingExportStore();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(store, KnownPatient()).ExecuteAsync(
                Request(ReportExportVariant.Deidentified, Researcher, annotated: true),
                CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task An_acknowledged_annotated_report_is_exported()
    {
        var store = new RecordingExportStore();

        await UseCase(store, KnownPatient()).ExecuteAsync(
            Request(ReportExportVariant.Deidentified, Researcher, annotated: true) with
            {
                AcknowledgeAnnotationsMayContainPhi = true,
            },
            CancellationToken.None);

        Assert.Single(store.Written);
    }

    [Fact]
    public async Task Annotations_need_no_acknowledgement_for_the_clinical_variant()
    {
        // Клинический вариант и так несёт идентификаторы и остаётся
        // в организации: предупреждать здесь не о чем.
        var store = new RecordingExportStore();

        await UseCase(store, KnownPatient()).ExecuteAsync(
            Request(ReportExportVariant.Clinical, Clinician, annotated: true),
            CancellationToken.None);

        Assert.Single(store.Written);
    }

    [Fact]
    public async Task The_audit_records_which_variant_left_the_application()
    {
        // «Экспорт был» без указания варианта не отвечает на главный вопрос —
        // ушли ли наружу идентификаторы.
        var audit = new RecordingAudit();

        await UseCase(new RecordingExportStore(), KnownPatient(), audit).ExecuteAsync(
            Request(ReportExportVariant.Clinical, Clinician),
            CancellationToken.None);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.ReportExported, recorded.Code);
        Assert.Equal(ReportExportVariant.Clinical, recorded.ReportExportVariant);
        Assert.Equal("doctor-1", recorded.PseudonymousActorId);
        Assert.Equal("study-1", recorded.PseudonymousStudyId);
    }

    [Fact]
    public void A_clinical_export_without_identifiers_cannot_be_built()
    {
        // Инвариант задан конструкцией: такого объекта не существует.
        Assert.Throws<DomainRuleViolationException>(() => ReportExport.Clinical(
            Report(),
            new PatientIdentity { FullName = "  ", MedicalRecordNumber = "MRN-1" },
            Clinician,
            Moment));
    }

    private static ExportReportUseCase UseCase(
        IReportExportStore store,
        IPatientIdentityRegistry registry,
        IAuditLog? audit = null) =>
        new(store, registry, audit ?? new RecordingAudit(), new FixedTime(Moment));

    private static ReportExportRequest Request(
        ReportExportVariant variant,
        Actor requestedBy,
        bool annotated = false) =>
        new()
        {
            Report = Report(annotated),
            Variant = variant,
            RequestedBy = requestedBy,
        };

    private static AnalysisReport Report(bool annotated = false) => new()
    {
        PseudonymousStudyId = "study-1",
        CreatedAt = Moment,
        Quality = QualityAssessment.Clean(),
        Outcome = new AnalysisOutcome.Refused
        {
            Reason = new RefusalReason { Code = RefusalCode.ModelPackageUnusable },
        },
        Pipeline = new PipelineIdentity
        {
            PreprocessingVersion = "1.0.0",
            FeatureSchemaVersion = "1.0.0",
            LabelMapVersion = "1.0.0",
            ApplicationCommitSha = new string('a', 40),
        },
        ClinicianAnnotations = annotated
            ?
            [
                new ClinicianAnnotation
                {
                    PseudonymousAuthorId = "doctor-1",
                    CreatedAt = Moment,
                    Text = "Желудочки расширены.",
                },
            ]
            : [],
    };

    private static KnownPatientRegistry KnownPatient() => new();

    private sealed class KnownPatientRegistry : IPatientIdentityRegistry
    {
        public int Calls { get; private set; }

        public Task<PatientIdentity?> ResolveAsync(
            string pseudonymousStudyId,
            CancellationToken cancellationToken)
        {
            this.Calls++;

            return Task.FromResult<PatientIdentity?>(new PatientIdentity
            {
                FullName = "Иванов Иван Иванович",
                MedicalRecordNumber = "MRN-778899",
                BirthDate = new DateOnly(1955, 11, 3),
            });
        }
    }

    private sealed class EmptyRegistry : IPatientIdentityRegistry
    {
        public Task<PatientIdentity?> ResolveAsync(
            string pseudonymousStudyId,
            CancellationToken cancellationToken) =>
            Task.FromResult<PatientIdentity?>(null);
    }

    private sealed class RecordingExportStore : IReportExportStore
    {
        private readonly List<ReportExport> written = [];

        public IReadOnlyList<ReportExport> Written => this.written;

        public Task<string> WriteAsync(ReportExport prepared, CancellationToken cancellationToken)
        {
            this.written.Add(prepared);

            return Task.FromResult("export-reference");
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        private readonly List<AuditEvent> events = [];

        public IReadOnlyList<AuditEvent> Events => this.events;

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            this.events.Add(auditEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment;
    }
}
