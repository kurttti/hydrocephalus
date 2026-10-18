using System.Text.Json;
using System.Text.Json.Nodes;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Infrastructure.Reporting;
using Json.Schema;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Соответствие канонического отчёта схеме `report.schema.json`.
///
/// ADR 0005 требует версионированной схемы с самого начала и теста
/// соответствия ей в CI; до сих пор схемы не было вовсе, а версия жила
/// константой в коде записи. Смысл проверки не в том, что JSON разбирается, —
/// это и так видно, — а в том, что состав полей нельзя изменить, не изменив
/// схему: правило связки с версионированием конвейера из CONTRIBUTING.md
/// иначе держится на внимательности.
///
/// Проверяются образцы, а не отчёты во время работы: валидация на каждой
/// записи стоила бы времени у врача и ничего не добавила бы — писать отчёт
/// умеет единственный класс.
/// </summary>
public sealed class ReportSchemaTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 10, 12, 8, 30, 0, TimeSpan.Zero);

    private static readonly JsonSchema Schema = JsonSchema.FromText(CanonicalReportJson.SchemaJson);

    public static TheoryData<string> Shapes => new(
        nameof(RefusedBeforeSeries),
        nameof(RefusedWithSeries),
        nameof(CompletedWithEverything),
        nameof(MeasurementWithoutRefusal));

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Every_shape_the_writer_produces_conforms_to_the_schema(string shape)
    {
        // Ветви записи перебираются поимённо: серия и её отсутствие, заключение
        // и отказ, сегментация и её отсутствие, попытка измерения с названной
        // причиной и без неё. Непройденная ветвь — непроверенная часть схемы.
        var json = CanonicalReportJson.SerializeToString(Report(shape));

        AssertValid(json);
    }

    [Fact]
    public void The_schema_names_the_version_the_writer_stamps()
    {
        // Иначе схема описывала бы один формат, а запись давала другой, и
        // расхождение нашлось бы у того, кто станет читать отчёты.
        using var document = JsonDocument.Parse(CanonicalReportJson.SchemaJson);

        var declared = document.RootElement
            .GetProperty("properties")
            .GetProperty("schemaVersion")
            .GetProperty("const")
            .GetString();

        Assert.Equal(CanonicalReportJson.SchemaVersion, declared);
    }

    [Theory]
    [InlineData("volumeAxis", typeof(VolumeAxis))]
    [InlineData("seriesWeighting", typeof(SeriesWeighting))]
    [InlineData("imagingPlane", typeof(ImagingPlane))]
    [InlineData("acquisitionTier", typeof(AcquisitionTier))]
    [InlineData("measurementQuality", typeof(MeasurementQuality))]
    [InlineData("measurementUnit", typeof(MeasurementUnit))]
    [InlineData("measurementRefusal", typeof(MeasurementRefusal))]
    [InlineData("qualityIssueCode", typeof(QualityIssueCode))]
    [InlineData("qualityIssueSeverity", typeof(QualityIssueSeverity))]
    [InlineData("refusalCode", typeof(RefusalCode))]
    public void A_listed_enumeration_holds_exactly_the_names_the_writer_can_emit(
        string definition,
        Type enumeration)
    {
        // Запись выводит перечисления через ToString(), поэтому новый член
        // появляется в отчёте сам собой, а в схеме — только если о нём
        // вспомнили. Это и есть тот разъезд, который здесь ловится.
        using var document = JsonDocument.Parse(CanonicalReportJson.SchemaJson);

        var listed = document.RootElement
            .GetProperty("$defs")
            .GetProperty(definition)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToArray();

        Assert.Equal(Enum.GetNames(enumeration), listed);
    }

    [Fact]
    public void A_report_missing_a_required_field_does_not_conform()
    {
        // Отрицательные случаи строятся правкой настоящего отчёта, а не
        // написанием негодного JSON руками: рукописный разошёлся бы с формой
        // записи и проверял бы не то.
        var json = Mutate(CompletedWithEverything(), node => node.AsObject().Remove("pipeline"));

        Assert.False(Schema.Evaluate(Parse(json)).IsValid);
    }

    [Fact]
    public void An_unknown_field_inside_a_block_does_not_conform()
    {
        // Поле, добавленное без изменения схемы, обязано ронять проверку —
        // ради этого состав и закрыт.
        var json = Mutate(
            CompletedWithEverything(),
            node => node["series"]!.AsObject().Add("extra", "x"));

        Assert.False(Schema.Evaluate(Parse(json)).IsValid);
    }

    [Fact]
    public void An_unlisted_enumeration_value_does_not_conform()
    {
        var json = Mutate(
            CompletedWithEverything(),
            node => node["series"]!["plane"] = "Oblique");

        Assert.False(Schema.Evaluate(Parse(json)).IsValid);
    }

    [Fact]
    public void A_timestamp_without_the_canonical_shape_does_not_conform()
    {
        // Отметка времени проверяется образцом, а не ключевым словом format:
        // в версии 2020-12 format по умолчанию ничего не проверяет, и местное
        // время прошло бы молча.
        var json = Mutate(
            CompletedWithEverything(),
            node => node["createdAt"] = "2026-10-12T11:30:00+03:00");

        Assert.False(Schema.Evaluate(Parse(json)).IsValid);
    }

    private static void AssertValid(string json)
    {
        var results = Schema.Evaluate(
            Parse(json),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(results.IsValid, Explain(results));
    }

    private static string Explain(EvaluationResults results) =>
        string.Join(
            Environment.NewLine,
            (results.Details ?? [])
                .Where(detail => detail.Errors is { Count: > 0 })
                .SelectMany(detail => detail.Errors!.Select(error =>
                    $"{detail.InstanceLocation} ({detail.EvaluationPath}): {error.Key} — {error.Value}")));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Mutate(AnalysisReport report, Action<JsonNode> change)
    {
        var node = JsonNode.Parse(CanonicalReportJson.SerializeToString(report))!;

        change(node);

        return node.ToJsonString();
    }

    private static AnalysisReport Report(string shape) => shape switch
    {
        nameof(RefusedBeforeSeries) => RefusedBeforeSeries(),
        nameof(RefusedWithSeries) => RefusedWithSeries(),
        nameof(CompletedWithEverything) => CompletedWithEverything(),
        nameof(MeasurementWithoutRefusal) => MeasurementWithoutRefusal(),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>Отказ до выбора серии: серии, сегментации и измерения нет.</summary>
    private static AnalysisReport RefusedBeforeSeries()
    {
        var quality = QualityAssessment.Clean();

        return new AnalysisReport
        {
            PseudonymousStudyId = "study-0001",
            CreatedAt = Moment,
            Quality = quality,
            Outcome = new AnalysisOutcome.Refused
            {
                Reason = new RefusalReason { Code = RefusalCode.InsufficientAcquisitionTier },
            },
            Pipeline = Pipeline(),
        };
    }

    /// <summary>Отказ входного контроля: серия названа, причина несёт проблемы.</summary>
    private static AnalysisReport RefusedWithSeries()
    {
        var quality = new QualityAssessment
        {
            Issues =
            [
                new QualityIssue
                {
                    Code = QualityIssueCode.UnsupportedVoxelGeometry,
                    Severity = QualityIssueSeverity.Blocking,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["parameter"] = "sliceThickness",
                        ["valueMm"] = "12",
                    },
                },
            ],
        };

        return RefusedBeforeSeries() with
        {
            Series = Series(),
            Quality = quality,
            Outcome = new AnalysisOutcome.Refused
            {
                Reason = RefusalReason.FromFailedQualityControl(quality),
            },
        };
    }

    /// <summary>
    /// Заключение со всем, что отчёт умеет нести: серия, прогноз, признаки
    /// с метками источника, сегментация, попытка измерения с названной
    /// причиной и комментарий врача.
    /// </summary>
    private static AnalysisReport CompletedWithEverything()
    {
        var inph = new DiagnosticClass("inph");
        var other = new DiagnosticClass("non_inph");

        var model = new ModelIdentity
        {
            Name = "hydrocephalus-classifier",
            Version = "0.2.0",
            PackageSha256 = new string('0', 64),
            SupportedClasses = [inph, other],
            CalibrationVersion = "1.0.0",
        };

        var quality = QualityAssessment.Clean();

        return new AnalysisReport
        {
            PseudonymousStudyId = "study-0002",
            CreatedAt = Moment,
            Series = Series(),
            Quality = quality,
            Outcome = new AnalysisOutcome.Completed
            {
                Prediction = Prediction.Create(
                    quality,
                    model,
                    [new ClassProbability(inph, 0.72), new ClassProbability(other, 0.28)],
                    new Uncertainty { LowerBound = 0.6, UpperBound = 0.84, ConfidenceLevel = 0.95 }),
            },
            Pipeline = Pipeline(),
            Biomarkers = [Volume(), Angle()],
            Segmentation = new SegmentationResult
            {
                LabelMapVersion = "baseline-1.4.0",
                MaskReference = "mask-0002",
                PassedGeometricQualityControl = true,
                Labels = [new AnatomicalLabel("ventricle.lateral.left")],
            },
            Measurement = new MeasurementAttempt
            {
                LabelMapVersion = "baseline-1.4.0",
                MaskQuality = MeasurementQuality.Questionable,
                Refusal = MeasurementRefusal.FrontalHornsNotFound,
                SegmentationIssues =
                [
                    new QualityIssue
                    {
                        Code = QualityIssueCode.HeadTruncated,
                        Severity = QualityIssueSeverity.Warning,
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
                    },
                ],
            },
            ClinicianAnnotations =
            [
                new ClinicianAnnotation
                {
                    PseudonymousAuthorId = "user-0001",
                    CreatedAt = Moment,
                    Text = "Рога размечены вручную.",
                },
            ],
        };
    }

    /// <summary>Попытка измерения без причины отказа: в JSON там null.</summary>
    private static AnalysisReport MeasurementWithoutRefusal() =>
        CompletedWithEverything() with
        {
            Measurement = new MeasurementAttempt
            {
                LabelMapVersion = "baseline-1.4.0",
                MaskQuality = MeasurementQuality.Reliable,
                Refusal = null,
                SegmentationIssues = [],
            },
        };

    private static AnalysedSeries Series() => new()
    {
        PseudonymousSeriesId = "series-0001",
        Weighting = SeriesWeighting.T1,
        Plane = ImagingPlane.Axial,
        Tier = AcquisitionTier.Extended,
    };

    private static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
        LabelMapVersion = "baseline-1.4.0",
        ApplicationCommitSha = "0000000",
    };

    /// <summary>
    /// Признак без ссылки на источник: у метода она необязательна, и запись
    /// кладёт в это поле null — схема обязана такое принимать.
    /// </summary>
    private static Biomarker Volume() => Biomarker.Create(
        new MeasurementMethod
        {
            Code = "volume.ventricular-system",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Extended,
        },
        AcquisitionTier.Extended,
        42.3,
        MeasurementUnit.Millilitre,
        MeasurementQuality.Questionable,
        new MeasurementRange(0.0, 400.0),
        [new AnatomicalLabel("ventricle.lateral.left")]);

    /// <summary>Признак со ссылкой на источник и в градусах.</summary>
    private static Biomarker Angle() => Biomarker.Create(
        new MeasurementMethod
        {
            Code = "callosal-angle-manual",
            DefinitionVersion = "1.0.0",
            RequiredTier = AcquisitionTier.Baseline,
            Reference = "Nakajima 2021",
        },
        AcquisitionTier.Extended,
        68.0,
        MeasurementUnit.Degree,
        MeasurementQuality.Questionable,
        new MeasurementRange(20.0, 180.0));
}
