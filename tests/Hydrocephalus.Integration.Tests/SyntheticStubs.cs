using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Синтетические реализации портов. Настоящие адаптеры (DICOM, ONNX, шифрованное хранилище)
/// появятся в M2-M3; здесь проверяется последовательность сценария, а не работа библиотек.
/// Фикстуры синтетические, реальных медицинских данных в тестах нет (tests/README.md).
/// </summary>
internal static class Synthetic
{
    public static readonly DiagnosticClass Inph = new("inph");
    public static readonly DiagnosticClass Alzheimer = new("alzheimer");

    public static ImagingStudy Study(
        MrAcquisitionType acquisitionType = MrAcquisitionType.ThreeDimensional,
        double sliceThickness = 1.0,
        bool contrastEnhanced = false) => new()
    {
        PseudonymousStudyId = "study-0001",
        PseudonymousSubjectId = "subject-0001",
        Series =
        [
            new ImagingSeries
            {
                PseudonymousSeriesId = "series-0001",
                Weighting = SeriesWeighting.T1,
                IsContrastEnhanced = contrastEnhanced,
                Geometry = new SeriesGeometry
                {
                    AcquisitionType = acquisitionType,
                    SliceThicknessMillimetres = sliceThickness,
                    PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                    Dimensions = new VolumeDimensions(256, 256, 180),
                    RowDirection = new SpatialVector(1, 0, 0),
                    ColumnDirection = new SpatialVector(0, 1, 0),
                    Origin = new SpatialVector(0, 0, 0),
                },
            },
        ],
    };

    public static ModelIdentity Model() => new()
    {
        Name = "hydrocephalus-classifier",
        Version = "0.1.0",
        PackageSha256 = new string('0', 64),
        SupportedClasses = [Inph, Alzheimer],
        CalibrationVersion = "1.0.0",
    };

    public static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = "1.0.0",
        FeatureSchemaVersion = "1.0.0",
        LabelMapVersion = "1.0.0",
        ApplicationCommitSha = new string('a', 40),
    };
}

/// <summary>Импортёр, возвращающий заранее заданную рабочую копию.</summary>
internal sealed class StubImporter(ImagingStudy study) : IStudyImporter
{
    public Task<WorkingCopy> ImportAsync(string sourceReference, CancellationToken cancellationToken) =>
        Task.FromResult(new WorkingCopy
        {
            Study = study,
            VolumeReference = "working-copy/volume-0001",
        });
}

/// <summary>Журнал аудита, запоминающий порядок событий.</summary>
internal sealed class RecordingAuditLog : IAuditLog
{
    private readonly List<AuditEvent> events = [];

    public IReadOnlyList<AuditEvent> Events => this.events;

    public IReadOnlyList<AuditEventCode> Codes => this.events.Select(item => item.Code).ToList();

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        this.events.Add(auditEvent);
        return Task.CompletedTask;
    }
}

/// <summary>Хранилище отчётов, запоминающее сохранённое.</summary>
internal sealed class RecordingReportStore : IReportStore
{
    private readonly List<AnalysisReport> reports = [];

    public IReadOnlyList<AnalysisReport> Reports => this.reports;

    public Task StoreAsync(AnalysisReport report, CancellationToken cancellationToken)
    {
        this.reports.Add(report);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Синтетический конвейер анализа. Поведение задаётся конструктором:
/// результат QC, исход анализа и необходимость отменить операцию посередине.
/// </summary>
internal sealed class StubInferenceEngine(
    QualityAssessment quality,
    AnalysisOutcome? outcome = null,
    CancellationTokenSource? cancelDuringAnalysis = null,
    Exception? failWith = null) : IInferenceEngine
{
    public Task<PipelineIdentity> DescribePipelineAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Synthetic.Pipeline());

    public Task<QualityAssessment> RunQualityControlAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken) => Task.FromResult(quality);

    public Task<AnalysisOutcome> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AnalysisProgress(AnalysisStage.Preprocessing, 0.25));

        if (failWith is not null)
        {
            throw failWith;
        }

        if (cancelDuringAnalysis is not null)
        {
            // Отмена приходит в момент, когда анализ уже идёт.
            cancelDuringAnalysis.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        progress?.Report(new AnalysisProgress(AnalysisStage.Classification, 0.9));

        return Task.FromResult(outcome!);
    }
}
