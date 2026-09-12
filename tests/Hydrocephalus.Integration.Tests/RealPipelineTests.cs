using FellowOakDicom;
using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Reporting;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Сценарий целиком на настоящих реализациях: импорт с деидентификацией,
/// входной контроль качества, отчёт и аудит. Подменены только хранилище отчётов
/// и журнал — всё остальное то же, что выполняется в приложении.
///
/// Отдельно от <c>SyntheticPipelineTests</c>, где адаптеры подменены: там
/// проверяется последовательность сценария, здесь — что настоящие части
/// действительно стыкуются.
/// </summary>
public sealed class RealPipelineTests : IDisposable
{
    private const string Salt = "test-salt-not-a-secret";

    private readonly DirectoryInfo source = CreateTempDirectory();
    private readonly DirectoryInfo workingCopy = CreateTempDirectory();
    private readonly RecordingAuditLog audit = new();
    private readonly RecordingReportStore reports = new();

    public void Dispose()
    {
        Delete(this.source);
        Delete(this.workingCopy);
    }

    [Fact]
    public async Task Usable_study_reaches_the_model_and_is_refused_for_want_of_a_package()
    {
        // Пригодный вход доходит до конца сценария и заканчивается честным отказом:
        // проверенного model package ещё нет (ADR 0004, M4), и вернуть вероятность
        // значило бы выдать невалидированное число за результат.
        this.WriteUsableSeries();

        var report = await this.ExecuteAsync();

        var refusal = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);

        Assert.Equal(RefusalCode.ModelPackageUnusable, refusal.Reason.Code);
        Assert.True(report.Quality.IsAcceptable);

        Assert.Equal(
            [
                AuditEventCode.StudyImported,
                AuditEventCode.QualityControlCompleted,
                AuditEventCode.AnalysisStarted,
                AuditEventCode.AnalysisRefused,
                AuditEventCode.ReportStored,
            ],
            this.audit.Codes);
    }

    [Fact]
    public async Task Unusable_geometry_is_refused_before_the_model_is_consulted()
    {
        // Толщина среза 12мм за пределами того, на чём конвейер валидируется.
        // Анализ не должен запускаться вовсе: AnalysisStarted в журнале означало бы,
        // что непригодный вход дошёл до модели.
        this.WriteSeries(sliceThickness: 12.0m, acquisitionType: "2D", slices: 12);

        var report = await this.ExecuteAsync();

        var refusal = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);

        Assert.Equal(RefusalCode.QualityControlFailed, refusal.Reason.Code);
        Assert.False(report.Quality.IsAcceptable);
        Assert.DoesNotContain(AuditEventCode.AnalysisStarted, this.audit.Codes);

        Assert.Contains(
            refusal.Reason.ContributingIssues,
            issue => issue.Code == QualityIssueCode.UnsupportedVoxelGeometry);
    }

    [Fact]
    public async Task Contrast_enhanced_only_study_never_reaches_quality_control()
    {
        // Постконтрастные серии не подаются в MRI-only конвейер ни на одном уровне
        // входа, поэтому отбор серии не находит ничего и отказ наступает раньше QC.
        this.WriteSeries(seriesDescription: "T1 MPRAGE +C");

        var report = await this.ExecuteAsync();

        var refusal = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);

        Assert.Equal(RefusalCode.InsufficientAcquisitionTier, refusal.Reason.Code);
        Assert.DoesNotContain(AuditEventCode.QualityControlCompleted, this.audit.Codes);
    }

    [Fact]
    public async Task Report_carries_no_source_identifier()
    {
        // Отчёт уходит из процесса, поэтому в нём не должно остаться ни исходного
        // UID исследования, ни фамилии из имени файла.
        SyntheticStudyFiles.WriteSlice(
            Path.Combine(this.source.FullName, "Ivanov I.I", "Ivanov-t1.dcm"),
            studyUid: "1.2.826.0.1.3680043.9.7133.1.1",
            seriesUid: "1.2.826.0.1.3680043.9.7133.1.2",
            patientId: "MRN-778899",
            patientName: "Ivanov^Ivan",
            slicePosition: 0m);

        var report = await this.ExecuteAsync();

        Assert.DoesNotContain("Ivanov", report.PseudonymousStudyId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("778899", report.PseudonymousStudyId, StringComparison.Ordinal);
        Assert.NotEqual("1.2.826.0.1.3680043.9.7133.1.1", report.PseudonymousStudyId);

        Assert.All(
            this.audit.Events,
            item => Assert.DoesNotContain(
                "Ivanov",
                item.PseudonymousStudyId ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Re_analysing_the_same_study_back_to_back_stores_both_reports()
    {
        // Системные часы Windows идут шагами около 15мс, поэтому два разбора
        // подряд получают одинаковую отметку времени. Если имя файла зависит
        // только от неё, второй отчёт не сохранится вовсе, а сценарий закончится
        // ошибкой вместо результата.
        this.WriteSeries(slices: 4);

        var store = new JsonReportStore(Path.Combine(this.workingCopy.FullName, "reports"));

        var first = await this.ExecuteAsync(store);
        var second = await this.ExecuteAsync(store);

        Assert.NotNull(first);
        Assert.NotNull(second);

        Assert.NotEmpty(Directory.GetFiles(
            Path.Combine(this.workingCopy.FullName, "reports"),
            "*.json",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task The_working_copy_is_gone_when_the_scenario_ends()
    {
        // ADR 0006: очистка выполняется в finally — на успехе, ошибке и отмене.
        // Отсутствие очистки считается дефектом, а не шумом.
        this.WriteUsableSeries();

        var report = await this.ExecuteAsync();

        Assert.NotNull(report);

        var remaining = Directory
            .EnumerateFiles(this.workingCopy.FullName, "*", SearchOption.AllDirectories)
            .ToArray();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Nothing_readable_as_dicom_is_left_behind()
    {
        this.WriteSeries(sliceThickness: 12.0m, acquisitionType: "2D", slices: 12);

        await this.ExecuteAsync();

        foreach (var path in Directory.EnumerateFiles(
            this.workingCopy.FullName,
            "*",
            SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);

            Assert.DoesNotContain(
                "DICM",
                System.Text.Encoding.ASCII.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    private static DirectoryInfo CreateTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hydro-" + Guid.NewGuid().ToString("N")));

    private static void Delete(DirectoryInfo directory)
    {
        if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_real_series_is_measured_end_to_end()
    {
        // До сих пор ни один сценарный тест не доходил до измерения: у фикстур
        // не было пикселей, и движок честно возвращал пустой список. Проверялся
        // весь путь, кроме того единственного места, где появляются миллилитры.
        this.WriteSeries(phantom: true);

        var report = await this.ExecuteAsync();

        var biomarker = Assert.Single(report.Biomarkers);

        Assert.Equal("volume.ventricular-system", biomarker.Method.Code);
        Assert.Equal(MeasurementUnit.Millilitre, biomarker.Unit);
        Assert.True(biomarker.Value > 0, "Объём желудочка на фантоме обязан быть положительным.");

        // Пометка достоверности проверяется здесь, а не только в модульном тесте
        // движка: у RegionVolumes значение по умолчанию — «надёжно», и потеря
        // аргумента где-нибудь по дороге дала бы правдоподобное число
        // с чужой уверенностью в клиническом отчёте.
        Assert.Equal(MeasurementQuality.Questionable, biomarker.Quality);
    }

    [Fact]
    public async Task A_measured_study_still_refuses_to_classify()
    {
        // Измерение не превращает конвейер в диагностический: проверенного
        // model package по-прежнему нет (ADR 0004).
        this.WriteSeries(phantom: true);

        var report = await this.ExecuteAsync();

        Assert.NotEmpty(report.Biomarkers);

        var refusal = Assert.IsType<AnalysisOutcome.Refused>(report.Outcome);

        Assert.Equal(RefusalCode.ModelPackageUnusable, refusal.Reason.Code);
    }

    [Fact]
    public async Task The_same_input_gives_a_byte_identical_report()
    {
        // «Воспроизводимый результат для одного model package и одного входа»
        // (docs/windows/README.md) до сих пор ничем не проверялся. Арифметика
        // в одном процессе детерминирована и так; настоящий источник расхождений
        // здесь — порядок: множество принятых экземпляров при разборе, порядок
        // обхода файлов, словари параметров замечаний, порядок серий
        // в исследовании. Канонический JSON ловит их все разом, поэтому
        // сравниваются именно его байты, а не поля отчёта.
        var clock = new FixedTime(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));

        // Две серии и неполное покрытие по оси срезов: на одной чистой серии
        // сравнивать было бы нечего — ни порядка, ни замечаний, и тест проходил
        // бы всегда. 60 срезов по 1 мм — это 60 мм при минимуме 100, то есть
        // замечание с числами, но не препятствие: анализ доходит до конца.
        this.WriteSeries(slices: 60, phantom: true);

        this.WriteSeries(
            sliceThickness: 6.0m,
            acquisitionType: "2D",
            slices: 20,
            seriesDescription: "T2 TSE",
            seriesUid: "1.2.3.12",
            filePrefix: "AX");

        var first = await this.ExecuteAsync(clock: clock);
        var second = await this.ExecuteAsync(clock: clock);

        // Сначала — что сравнивать есть что. Отчёт без замечаний даёт почти
        // постоянный JSON, и равенство байтов не доказывало бы ничего.
        var issue = Assert.Single(
            first.Quality.Issues,
            candidate => candidate.Parameters.Count > 0);

        Assert.NotEmpty(issue.Parameters);

        // С фантомом в сравнение попадают и измерения, то есть порядок обхода
        // маски и сегментации, а не только разбор DICOM.
        Assert.NotEmpty(first.Biomarkers);

        Assert.Equal(
            CanonicalReportJson.Serialize(first),
            CanonicalReportJson.Serialize(second));
    }

    private Task<AnalysisReport> ExecuteAsync(IReportStore? store = null, TimeProvider? clock = null)
    {
        var importer = new StudyImporter(
            new DicomImportOptions { PseudonymSalt = Salt },
            new WorkingCopyOptions
            {
                RootDirectory = this.workingCopy.FullName,

                // ACL выключен: временный каталог теста живёт в общем
                // расположении, ограничивать его учётной записью незачем.
                RestrictAccessToCurrentUser = false,
            });

        var useCase = new AnalyzeStudyUseCase(
            importer,
            importer,
            new BaselineMeasurementEngine(
                new InputQualityControl(),
                new WorkingCopyVolumeSource(importer),
                Synthetic.Pipeline()),
            store ?? this.reports,
            this.audit,
            clock ?? TimeProvider.System);

        return useCase.ExecuteAsync(this.source.FullName, Synthetic.Clinician(), progress: null, CancellationToken.None);
    }

    private void WriteUsableSeries() => this.WriteSeries();

    private void WriteSeries(
        decimal sliceThickness = 1.0m,
        string acquisitionType = "3D",
        int slices = 120,
        string seriesDescription = "T1 MPRAGE",
        string seriesUid = "1.2.3.11",
        string filePrefix = "IM",
        decimal? sliceSpacing = null,
        bool phantom = false)
    {
        var step = sliceSpacing ?? sliceThickness;

        for (var index = 0; index < slices; index++)
        {
            SyntheticStudyFiles.WriteSlice(
                Path.Combine(this.source.FullName, $"{filePrefix}{index:D4}.dcm"),
                studyUid: "1.2.3.1",
                seriesUid: seriesUid,
                patientId: "P-1",
                seriesDescription: seriesDescription,
                acquisitionType: acquisitionType,
                sliceThickness: sliceThickness,
                slicePosition: index * step,
                phantom: phantom,
                sliceIndex: index,
                sliceCount: slices);
        }
    }
}
