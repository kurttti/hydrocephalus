using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Экспорт манифеста датасета.
///
/// Сценарий отделён от импорта намеренно, и тесты закрепляют именно это:
/// сбор обучающей выборки не должен происходить попутно с клинической работой,
/// без подтверждения, без названного инициатора и без следа в журнале.
/// </summary>
public sealed class ExportDatasetManifestTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_confirmed_export_writes_the_manifest_and_is_audited()
    {
        var store = new RecordingManifestStore();
        var audit = new RecordingAudit();

        var reference = await UseCase(store, audit).ExecuteAsync(Request(), CancellationToken.None);

        Assert.Equal("manifest-reference", reference);
        Assert.Single(store.Written);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.DatasetManifestExported, recorded.Code);
        Assert.Equal("researcher-1", recorded.PseudonymousActorId);
        Assert.Equal(Moment, recorded.OccurredAt);
    }

    [Fact]
    public async Task An_unconfirmed_export_is_refused()
    {
        // Подтверждение — отдельное поле, а не сам факт вызова: иначе экспорт
        // можно вызвать мимоходом из любого места.
        var store = new RecordingManifestStore();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(store, new RecordingAudit())
                .ExecuteAsync(Request(confirmed: false), CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public void An_actor_without_an_identifier_cannot_be_created()
    {
        // Безымянное действие нельзя ни разобрать, ни оспорить, поэтому
        // такой инициатор не существует — проверять его в сценарии незачем.
        Assert.Throws<DomainRuleViolationException>(
            () => Actor.Create("   ", ClinicalRole.Researcher));
    }

    [Fact]
    public async Task A_clinician_may_not_export_a_dataset_manifest()
    {
        // Сбор выборки — не лечебная работа: у врача такого права нет.
        var store = new RecordingManifestStore();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(store, new RecordingAudit()).ExecuteAsync(
                Request(requestedBy: Actor.Create("doctor-1", ClinicalRole.Clinician)),
                CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task A_denied_attempt_is_audited_under_its_own_code()
    {
        // Несанкционированный экспорт назван отдельной угрозой: попытка,
        // не оставившая следа, ничем не отличается от её отсутствия.
        // Спутать с состоявшимся экспортом запись нельзя — код другой.
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(new RecordingManifestStore(), audit).ExecuteAsync(
                Request(requestedBy: Actor.Create("doctor-1", ClinicalRole.Clinician)),
                CancellationToken.None));

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.AccessDenied, recorded.Code);
        Assert.Equal("doctor-1", recorded.PseudonymousActorId);
    }

    [Fact]
    public async Task An_unspecified_role_grants_nothing()
    {
        // Значение по умолчанию у перечисления получают неинициализированные
        // данные; роль по умолчанию с доступом — дыра, которая открывается сама.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(new RecordingManifestStore(), new RecordingAudit()).ExecuteAsync(
                Request(requestedBy: Actor.Create("nobody", ClinicalRole.Unspecified)),
                CancellationToken.None));
    }

    [Fact]
    public async Task An_administrator_gets_no_access_to_patient_data()
    {
        // Обслуживание системы и доступ к её содержимому — разные задачи.
        await Assert.ThrowsAsync<AccessDeniedException>(
            () => UseCase(new RecordingManifestStore(), new RecordingAudit()).ExecuteAsync(
                Request(requestedBy: Actor.Create("admin-1", ClinicalRole.Administrator)),
                CancellationToken.None));
    }

    [Fact]
    public async Task An_empty_export_is_refused()
    {
        // По пустому файлу нельзя отличить «данных нет» от «выгрузка не сработала».
        var store = new RecordingManifestStore();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(store, new RecordingAudit())
                .ExecuteAsync(Request(studies: []), CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task An_export_of_studies_without_series_is_refused()
    {
        var empty = new ImagingStudy
        {
            PseudonymousStudyId = "study-1",
            PseudonymousSubjectId = "subject-1",
            Series = [],
        };

        var store = new RecordingManifestStore();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(store, new RecordingAudit())
                .ExecuteAsync(Request(studies: [empty]), CancellationToken.None));

        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task Nothing_is_audited_when_the_export_is_refused()
    {
        // Журнал отражает состоявшийся экспорт, а не намерение: запись
        // об отклонённом запросе читалась бы как выгрузка данных.
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => UseCase(new RecordingManifestStore(), audit)
                .ExecuteAsync(Request(confirmed: false), CancellationToken.None));

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task The_use_case_exports_exactly_what_it_was_given()
    {
        // Сценарий не ищет данные сам: у сценария, который обходит хранилище,
        // нет естественного предела — он выгрузит всё, что найдёт.
        var store = new RecordingManifestStore();

        await UseCase(store, new RecordingAudit())
            .ExecuteAsync(Request(studies: [Study("a"), Study("b")]), CancellationToken.None);

        var written = Assert.Single(store.Written);

        Assert.Equal(["a", "b"], written.Select(study => study.PseudonymousStudyId));
    }

    [Fact]
    public async Task The_audit_record_carries_no_study_identifier()
    {
        // Событие говорит, кто и когда выгрузил выборку, а не какие пациенты
        // в неё попали: состав описан самим манифестом.
        var audit = new RecordingAudit();

        await UseCase(new RecordingManifestStore(), audit)
            .ExecuteAsync(Request(), CancellationToken.None);

        Assert.Null(Assert.Single(audit.Events).PseudonymousStudyId);
    }

    private static ExportDatasetManifestUseCase UseCase(
        IDatasetManifestStore store,
        IAuditLog audit) =>
        new(store, audit, new FixedTime(Moment));

    private static DatasetExportRequest Request(
        IReadOnlyList<ImagingStudy>? studies = null,
        Actor? requestedBy = null,
        bool confirmed = true) =>
        new()
        {
            Studies = studies ?? [Study("study-1")],
            RequestedBy = requestedBy ?? Actor.Create("researcher-1", ClinicalRole.Researcher),
            Confirmed = confirmed,
        };

    private static ImagingStudy Study(string id) => new()
    {
        PseudonymousStudyId = id,
        PseudonymousSubjectId = "subject-1",
        Series =
        [
            new ImagingSeries
            {
                PseudonymousSeriesId = id + "-series",
                Weighting = SeriesWeighting.T1,
                IsContrastEnhanced = false,
                Geometry = new SeriesGeometry
                {
                    AcquisitionType = MrAcquisitionType.ThreeDimensional,
                    SliceThicknessMillimetres = 1.0,
                    SliceSpacingMillimetres = 1.0,
                    PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                    Dimensions = new VolumeDimensions(256, 256, 176),
                    RowDirection = new SpatialVector(1, 0, 0),
                    ColumnDirection = new SpatialVector(0, 1, 0),
                    Origin = default,
                },
            },
        ],
    };

    private sealed class RecordingManifestStore : IDatasetManifestStore
    {
        private readonly List<IReadOnlyList<ImagingStudy>> written = [];

        public IReadOnlyList<IReadOnlyList<ImagingStudy>> Written => this.written;

        public Task<string> WriteAsync(
            IReadOnlyList<ImagingStudy> studies,
            CancellationToken cancellationToken)
        {
            this.written.Add(studies);

            return Task.FromResult("manifest-reference");
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
