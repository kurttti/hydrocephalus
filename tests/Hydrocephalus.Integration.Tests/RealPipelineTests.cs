using FellowOakDicom;
using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Infrastructure.Dicom;

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
    public async Task Working_copy_of_the_analysed_study_is_deidentified_on_disk()
    {
        // Сценарий работает по рабочей копии, а не по источнику: проверяется,
        // что то, на что ссылается отчёт, действительно очищено.
        this.WriteUsableSeries();

        await this.ExecuteAsync();

        var written = Directory
            .EnumerateFiles(this.workingCopy.FullName, "*.dcm", SearchOption.AllDirectories)
            .ToArray();

        Assert.NotEmpty(written);

        foreach (var path in written)
        {
            var file = await DicomFile.OpenAsync(path);

            Assert.False(file.Dataset.Contains(DicomTag.PatientName));
            Assert.False(file.Dataset.Contains(DicomTag.PatientID));
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

    private Task<AnalysisReport> ExecuteAsync()
    {
        var useCase = new AnalyzeStudyUseCase(
            new StudyImporter(
                new DicomImportOptions { PseudonymSalt = Salt },
                new WorkingCopyOptions { RootDirectory = this.workingCopy.FullName }),
            new QualityControlOnlyEngine(new InputQualityControl(), Synthetic.Pipeline()),
            this.reports,
            this.audit,
            TimeProvider.System);

        return useCase.ExecuteAsync(this.source.FullName, progress: null, CancellationToken.None);
    }

    private void WriteUsableSeries() => this.WriteSeries();

    private void WriteSeries(
        decimal sliceThickness = 1.0m,
        string acquisitionType = "3D",
        int slices = 120,
        string seriesDescription = "T1 MPRAGE")
    {
        for (var index = 0; index < slices; index++)
        {
            SyntheticStudyFiles.WriteSlice(
                Path.Combine(this.source.FullName, $"IM{index:D4}.dcm"),
                studyUid: "1.2.3.1",
                seriesUid: "1.2.3.11",
                patientId: "P-1",
                seriesDescription: seriesDescription,
                acquisitionType: acquisitionType,
                sliceThickness: sliceThickness,
                slicePosition: index * sliceThickness);
        }
    }
}
