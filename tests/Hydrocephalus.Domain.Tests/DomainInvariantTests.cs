using System.Reflection;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Domain.Tests;

/// <summary>
/// По одному тесту на каждый инвариант из src/Hydrocephalus.Domain/README.md.
/// Каждый пытается построить недопустимое состояние и проверяет, что это невозможно.
/// </summary>
public sealed class DomainInvariantTests
{
    private static readonly DiagnosticClass Inph = new("inph");
    private static readonly DiagnosticClass Alzheimer = new("alzheimer");

    /// <summary>Инвариант 1: прогноз невозможен без успешного QC.</summary>
    [Fact]
    public void Prediction_cannot_be_produced_when_quality_control_failed()
    {
        var failedQc = new QualityAssessment
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

        Assert.False(failedQc.IsAcceptable);

        var exception = Assert.Throws<DomainRuleViolationException>(
            () => Prediction.Create(failedQc, Model(), [new ClassProbability(Inph, 0.8)], SomeUncertainty()));

        Assert.Contains("quality control", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Инвариант 2: каждое измерение связано с методом, версией и единицей.</summary>
    [Fact]
    public void Measurement_requires_a_method_with_version_and_a_unit()
    {
        var method = EvansIndexMethod();

        // Версия определения — обязательный член записи, без неё измерение не скомпилируется.
        Assert.False(string.IsNullOrWhiteSpace(method.DefinitionVersion));

        // Единица не может отсутствовать во время выполнения.
        Assert.Throws<DomainRuleViolationException>(() => Biomarker.Create(
            method,
            AcquisitionTier.Baseline,
            value: 0.31,
            unit: MeasurementUnit.Unspecified,
            quality: MeasurementQuality.Reliable,
            allowedRange: new MeasurementRange(0, 1)));
    }

    /// <summary>Инвариант 3: прогноз связан с моделью и перечнем поддерживаемых классов.</summary>
    [Fact]
    public void Prediction_rejects_classes_outside_the_model_class_set()
    {
        var model = Model();
        var unsupported = new DiagnosticClass("vascular_dementia");

        Assert.DoesNotContain(unsupported, model.SupportedClasses);

        Assert.Throws<DomainRuleViolationException>(() => Prediction.Create(
            QualityAssessment.Clean(),
            model,
            [new ClassProbability(unsupported, 0.6)],
            SomeUncertainty()));
    }

    /// <summary>Инвариант 4: отказ от ответа не преобразуется в отрицательный диагноз.</summary>
    [Fact]
    public void Refusal_carries_no_probabilities_and_the_outcome_hierarchy_is_closed()
    {
        var refusal = RefusalReason.FromFailedQualityControl(new QualityAssessment
        {
            Issues =
            [
                new QualityIssue
                {
                    Code = QualityIssueCode.HeadTruncated,
                    Severity = QualityIssueSeverity.Blocking,
                },
            ],
        });

        AnalysisOutcome outcome = new AnalysisOutcome.Refused { Reason = refusal };

        // У отказа нет ни одного члена, несущего вероятность или класс.
        var members = outcome.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("Prediction", members);
        Assert.DoesNotContain("Probabilities", members);

        // Третьего состояния не существует: наследоваться извне нельзя.
        Assert.Empty(typeof(AnalysisOutcome).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var subtypes = typeof(AnalysisOutcome).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(AnalysisOutcome)))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Completed", "Refused"], subtypes);
    }

    /// <summary>Инвариант 5: ручное исправление врача хранится отдельно от вывода модели.</summary>
    [Fact]
    public void Clinician_annotations_are_stored_apart_from_model_output()
    {
        var prediction = Prediction.Create(
            QualityAssessment.Clean(),
            Model(),
            [new ClassProbability(Inph, 0.7), new ClassProbability(Alzheimer, 0.2)],
            SomeUncertainty());

        var report = new AnalysisReport
        {
            PseudonymousStudyId = "study-0001",
            CreatedAt = DateTimeOffset.UnixEpoch,
            Quality = QualityAssessment.Clean(),
            Outcome = new AnalysisOutcome.Completed { Prediction = prediction },
            Pipeline = Pipeline(),
            ClinicianAnnotations =
            [
                new ClinicianAnnotation
                {
                    PseudonymousAuthorId = "clinician-01",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    Text = "Маска бокового желудочка занижена справа.",
                },
            ],
        };

        // Комментарий живёт в отчёте, а не внутри прогноза.
        Assert.Single(report.ClinicianAnnotations);

        var predictionMembers = typeof(Prediction)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.DoesNotContain("ClinicianAnnotations", predictionMembers);
        Assert.DoesNotContain("Annotations", predictionMembers);
    }

    /// <summary>
    /// Дополнительно: объёмный признак нельзя получить из 2D-серии.
    /// Правило следует из уровней входа в docs/clinical/README.md и обеспечивается
    /// тем, что уровень выводится из геометрии, а не объявляется отдельно.
    /// </summary>
    [Fact]
    public void Volumetric_measurement_is_impossible_on_a_baseline_tier_study()
    {
        var twoDimensional = Geometry(MrAcquisitionType.TwoDimensional, sliceThickness: 5.0);
        Assert.Equal(AcquisitionTier.Baseline, twoDimensional.Tier);

        var volumetricMethod = new MeasurementMethod
        {
            Code = "ventricular_volume",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Extended,
        };

        Assert.Throws<DomainRuleViolationException>(() => Biomarker.Create(
            volumetricMethod,
            twoDimensional.Tier,
            value: 42.0,
            unit: MeasurementUnit.Millilitre,
            quality: MeasurementQuality.Reliable,
            allowedRange: new MeasurementRange(0, 500)));

        // Тонкая 3D-серия тот же признак допускает.
        var threeDimensional = Geometry(MrAcquisitionType.ThreeDimensional, sliceThickness: 1.0);
        Assert.Equal(AcquisitionTier.Extended, threeDimensional.Tier);

        var biomarker = Biomarker.Create(
            volumetricMethod,
            threeDimensional.Tier,
            value: 42.0,
            unit: MeasurementUnit.Millilitre,
            quality: MeasurementQuality.Reliable,
            allowedRange: new MeasurementRange(0, 500));

        Assert.Equal(42.0, biomarker.Value);
    }

    private static SeriesGeometry Geometry(MrAcquisitionType acquisitionType, double sliceThickness) => new()
    {
        AcquisitionType = acquisitionType,
        SliceThicknessMillimetres = sliceThickness,
        SliceSpacingMillimetres = sliceThickness,
        PixelSpacing = new InPlaneSpacing(1.0, 1.0),
        Dimensions = new VolumeDimensions(256, 256, 180),
        RowDirection = new SpatialVector(1, 0, 0),
        ColumnDirection = new SpatialVector(0, 1, 0),
        Origin = new SpatialVector(0, 0, 0),
    };

    private static MeasurementMethod EvansIndexMethod() => new()
    {
        Code = "evans_index",
        DefinitionVersion = "1.0.0",
        RequiredTier = AcquisitionTier.Baseline,
    };

    private static ModelIdentity Model() => new()
    {
        Name = "hydrocephalus-classifier",
        Version = "0.1.0",
        PackageSha256 = new string('0', 64),
        SupportedClasses = [Inph, Alzheimer],
    };

    private static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = "1.0.0",
        FeatureSchemaVersion = "1.0.0",
        LabelMapVersion = "1.0.0",
        ApplicationCommitSha = new string('a', 40),
    };

    private static Uncertainty SomeUncertainty() => new()
    {
        LowerBound = 0.6,
        UpperBound = 0.8,
        ConfidenceLevel = 0.95,
    };
}
