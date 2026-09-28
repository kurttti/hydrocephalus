using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Запись в отчёт измерения, выполненного врачом вручную.
///
/// Проверяется прежде всего то, что запись ничего не переписывает: хранилище
/// отчётов неизменно по построению (ADR 0005), и ручная отметка ложится новой
/// версией. Прежние версии остаются, и из них складывается история измерений —
/// на ней потом стоит оценка воспроизводимости.
/// </summary>
public sealed class RecordManualMeasurementTests
{
    private static readonly DateTimeOffset Analysed = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Marked = new(2026, 9, 28, 11, 30, 0, TimeSpan.Zero);

    private static readonly Actor Clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);
    private static readonly Actor Researcher = Actor.Create("researcher-1", ClinicalRole.Researcher);
    private static readonly Actor Administrator = Actor.Create("admin-1", ClinicalRole.Administrator);

    [Fact]
    public async Task The_measurement_reaches_the_report_as_a_new_version()
    {
        var store = new RecordingReportStore();

        var recorded = await UseCase(store).ExecuteAsync(
            Report(), [Measurement(0.34)], Clinician, CancellationToken.None);

        var stored = Assert.Single(store.Stored);

        Assert.Same(recorded, stored);

        // Отметка сделана позже разбора, и это момент записи, а не анализа:
        // иначе две версии одного исследования легли бы в один путь.
        Assert.Equal(Marked, stored.CreatedAt);
        Assert.NotEqual(Analysed, stored.CreatedAt);

        var manual = Assert.Single(
            stored.Biomarkers,
            biomarker => biomarker.Method.Code == "evans-index-manual");

        Assert.Equal(0.34, manual.Value, precision: 9);
    }

    [Fact]
    public async Task The_measurements_already_in_the_report_are_kept()
    {
        // Ручная отметка дописывается, а не заменяет отчёт: объём желудочковой
        // системы измерен независимо от неё и пропасть не должен.
        var store = new RecordingReportStore();

        var recorded = await UseCase(store).ExecuteAsync(
            Report(withVolume: true), [Measurement(0.34)], Clinician, CancellationToken.None);

        Assert.Equal(2, recorded.Biomarkers.Count);
        Assert.Contains(recorded.Biomarkers, biomarker => biomarker.Method.Code == "volume.ventricular-system");
    }

    [Fact]
    public async Task Measuring_again_replaces_the_manual_value_within_one_version()
    {
        // Два значения одного метода в одном отчёте не сказали бы, какое верно.
        // Прежнее при этом не теряется: оно осталось в своей версии.
        var store = new RecordingReportStore();
        var useCase = UseCase(store);

        var first = await useCase.ExecuteAsync(
            Report(), [Measurement(0.34)], Clinician, CancellationToken.None);

        var second = await useCase.ExecuteAsync(
            first, [Measurement(0.29)], Clinician, CancellationToken.None);

        var manual = Assert.Single(
            second.Biomarkers,
            biomarker => biomarker.Method.Code == "evans-index-manual");

        Assert.Equal(0.29, manual.Value, precision: 9);

        Assert.Equal(2, store.Stored.Count);
        Assert.Equal(0.34, store.Stored[0].Biomarkers.Single().Value, precision: 9);
    }

    [Fact]
    public async Task The_index_and_the_head_rotation_are_written_as_one_version()
    {
        // Оба признака получены одним действием врача, поэтому и версия отчёта
        // одна: раздельная запись дала бы две версии и два следа в журнале на
        // одно измерение, а история измерений читается по версиям.
        var store = new RecordingReportStore();
        var audit = new RecordingAudit();

        var recorded = await UseCase(store, audit).ExecuteAsync(
            Report(),
            [Measurement(0.34), Rotation(7.5)],
            Clinician,
            CancellationToken.None);

        Assert.Single(store.Stored);
        Assert.Equal(2, recorded.Biomarkers.Count);

        var rotation = Assert.Single(
            recorded.Biomarkers,
            biomarker => biomarker.Method.Code == "head-rotation-in-plane");

        Assert.Equal(7.5, rotation.Value, precision: 9);

        Assert.Single(
            audit.Events,
            item => item.Code == AuditEventCode.MeasurementRecordedByClinician);
    }

    [Fact]
    public async Task Measuring_again_replaces_both_and_keeps_the_rest()
    {
        // Замена идёт по коду каждого признака: перемеряли — обновились оба, а
        // объём желудочковой системы, измеренный не этим действием, остался.
        var store = new RecordingReportStore();
        var useCase = UseCase(store);

        var first = await useCase.ExecuteAsync(
            Report(withVolume: true),
            [Measurement(0.34), Rotation(7.5)],
            Clinician,
            CancellationToken.None);

        var second = await useCase.ExecuteAsync(
            first, [Measurement(0.29), Rotation(2.0)], Clinician, CancellationToken.None);

        Assert.Equal(3, second.Biomarkers.Count);
        Assert.Contains(second.Biomarkers, item => item.Method.Code == "volume.ventricular-system");

        Assert.Equal(
            0.29,
            Assert.Single(second.Biomarkers, item => item.Method.Code == "evans-index-manual").Value,
            precision: 9);

        Assert.Equal(
            2.0,
            Assert.Single(second.Biomarkers, item => item.Method.Code == "head-rotation-in-plane").Value,
            precision: 9);
    }

    [Fact]
    public async Task An_implausible_value_is_refused_rather_than_written_for_good()
    {
        // Хранилище неизменно: записанное остаётся навсегда, а окно — не
        // единственный возможный вызывающий. Найдено на прогоне, где точки
        // пришли в старом порядке: отношение перевернулось, индекс вышел 3,02
        // вместо 0,33 и лёг на диск в обход интерфейса.
        var store = new RecordingReportStore();

        await Assert.ThrowsAsync<Hydrocephalus.Domain.DomainRuleViolationException>(
            () => UseCase(store).ExecuteAsync(
                Report(), [Measurement(3.02), Rotation(7.5)], Clinician, CancellationToken.None));

        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task An_implausible_rotation_is_refused_too()
    {
        // Правило одно на оба признака: угол свыше 45 градусов вероятнее означает
        // перепутанный порядок точек, чем такую укладку.
        var store = new RecordingReportStore();

        await Assert.ThrowsAsync<Hydrocephalus.Domain.DomainRuleViolationException>(
            () => UseCase(store).ExecuteAsync(
                Report(), [Measurement(0.34), Rotation(88.0)], Clinician, CancellationToken.None));

        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task Recording_nothing_is_refused_rather_than_writing_an_empty_version()
    {
        var store = new RecordingReportStore();

        await Assert.ThrowsAsync<ArgumentException>(() => UseCase(store).ExecuteAsync(
            Report(), [], Clinician, CancellationToken.None));

        Assert.Empty(store.Stored);
    }

    [Fact]
    public async Task The_report_given_in_is_not_changed()
    {
        // Отчёт, уже показанный врачу, менять нельзя: запись порождает новый,
        // а прежний остаётся тем, что видели.
        var store = new RecordingReportStore();
        var given = Report();

        await UseCase(store).ExecuteAsync(given, [Measurement(0.34)], Clinician, CancellationToken.None);

        Assert.Empty(given.Biomarkers);
        Assert.Equal(Analysed, given.CreatedAt);
    }

    [Fact]
    public async Task Who_recorded_it_and_when_goes_to_the_audit_log()
    {
        // В отчёте автора нет намеренно: своё поле потребовало бы версии схемы
        // признаков, а комментарий врача заставил бы обезличенный экспорт
        // требовать подтверждения про PHI за текст, которого никто не набирал.
        var audit = new RecordingAudit();

        await UseCase(new RecordingReportStore(), audit).ExecuteAsync(
            Report(), [Measurement(0.34)], Clinician, CancellationToken.None);

        var recorded = Assert.Single(
            audit.Events,
            item => item.Code == AuditEventCode.MeasurementRecordedByClinician);

        Assert.Equal(Clinician.PseudonymousUserId, recorded.PseudonymousActorId);
        Assert.Equal("study-1", recorded.PseudonymousStudyId);
        Assert.Equal(Marked, recorded.OccurredAt);

        // Сохранение отмечается тоже: иначе число сохранённых отчётов в журнале
        // расходилось бы с числом файлов на диске.
        Assert.Contains(audit.Events, item => item.Code == AuditEventCode.ReportStored);
    }

    [Theory]
    [InlineData(ClinicalRole.Researcher)]
    [InlineData(ClinicalRole.Administrator)]
    public async Task A_role_without_the_right_records_nothing_and_leaves_a_trace(ClinicalRole role)
    {
        // След от неудавшейся попытки нужен именно потому, что она не удалась:
        // иначе выход за пределы своих прав не оставляет в системе ничего.
        var store = new RecordingReportStore();
        var audit = new RecordingAudit();
        var actor = role == ClinicalRole.Researcher ? Researcher : Administrator;

        await Assert.ThrowsAsync<AccessDeniedException>(() => UseCase(store, audit).ExecuteAsync(
            Report(), [Measurement(0.34)], actor, CancellationToken.None));

        Assert.Empty(store.Stored);
        Assert.Equal(AuditEventCode.AccessDenied, Assert.Single(audit.Events).Code);
    }

    private static RecordManualMeasurementUseCase UseCase(
        IReportStore store,
        IAuditLog? audit = null) =>
        new(store, audit ?? new RecordingAudit(), new FixedTime(Marked));

    private static Biomarker Measurement(double value) => new()
    {
        Method = new MeasurementMethod
        {
            Code = "evans-index-manual",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Baseline,
        },
        Value = value,
        Unit = MeasurementUnit.Ratio,
        Quality = MeasurementQuality.Reliable,
        AllowedRange = new MeasurementRange(0.10, 0.60),
    };

    private static Biomarker Rotation(double degrees) => new()
    {
        Method = new MeasurementMethod
        {
            Code = "head-rotation-in-plane",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Baseline,
        },
        Value = degrees,
        Unit = MeasurementUnit.Degree,
        Quality = MeasurementQuality.Reliable,
        AllowedRange = new MeasurementRange(0.0, 45.0),
    };

    private static AnalysisReport Report(bool withVolume = false) => new()
    {
        PseudonymousStudyId = "study-1",
        CreatedAt = Analysed,
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
        Biomarkers = withVolume
            ?
            [
                new Biomarker
                {
                    Method = new MeasurementMethod
                    {
                        Code = "volume.ventricular-system",
                        DefinitionVersion = "1.0.0",
                        RequiredTier = AcquisitionTier.Extended,
                    },
                    Value = 85.0,
                    Unit = MeasurementUnit.Millilitre,
                    Quality = MeasurementQuality.Questionable,
                    AllowedRange = new MeasurementRange(5.0, 400.0),
                },
            ]
            : [],
    };

    private sealed class RecordingReportStore : IReportStore
    {
        internal List<AnalysisReport> Stored { get; } = [];

        public Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
        {
            this.Stored.Add(report);

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAudit : IAuditLog
    {
        internal List<AuditEvent> Events { get; } = [];

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            this.Events.Add(auditEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment;
    }
}
