using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Строка состояния о сегментации.
///
/// Проверяется, что отказы различимы: порог, взявший ткань, фрагмент
/// и область у края кадра — разные поломки, и врач, открывший разбор решения,
/// должен знать, какую смотрит. Общее «маска не построена» их сливало.
/// </summary>
public sealed class SegmentationReadoutTests
{
    private static readonly VolumeGrid Grid = new(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0);

    [Fact]
    public void An_unknown_weighting_is_named_as_the_reason_for_no_mask() =>
        Assert.Contains("взвешенность", SegmentationReadout.Describe(null), StringComparison.Ordinal);

    [Theory]
    [InlineData("thresholdDidNotIsolateCsf", "selectedFraction", "0.61", "61")]
    [InlineData("ventricularSystemImplausiblySmall", "millilitres", "3.2", "3,2 мл")]
    [InlineData("ventricularSystemTruncatedByFrame", null, null, "край кадра")]
    public void A_refusal_is_named_by_its_reason_and_points_to_the_review(
        string reason,
        string? parameter,
        string? value,
        string expected)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason };

        if (parameter is not null)
        {
            parameters[parameter] = value!;
        }

        var text = SegmentationReadout.Describe(Result(
            MeasurementQuality.Unreliable,
            new QualityIssue
            {
                Code = QualityIssueCode.InconsistentGeometry,
                Severity = QualityIssueSeverity.Blocking,
                Parameters = parameters,
            },
            filled: false));

        Assert.Contains("Объём не посчитан", text, StringComparison.Ordinal);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.Contains("Разбор метода", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_thick_series_says_volume_is_not_measured_rather_than_naming_a_segmentation_failure()
    {
        // «Упирается в край кадра» на толстосрезовой серии читалось как дефект
        // серии; на деле по ней объём не считается вовсе.
        var text = SegmentationReadout.Describe(
            Result(
                MeasurementQuality.Unreliable,
                new QualityIssue
                {
                    Code = QualityIssueCode.HeadTruncated,
                    Severity = QualityIssueSeverity.Blocking,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = "ventricularSystemTruncatedByFrame" },
                },
                filled: false),
            AcquisitionTier.Baseline);

        Assert.Contains("объём желудочков по ней не считается", text, StringComparison.Ordinal);
        Assert.DoesNotContain("край кадра", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_mask_without_a_refusal_says_the_ventricles_were_not_found()
    {
        var text = SegmentationReadout.Describe(Result(MeasurementQuality.Questionable, issue: null, filled: false));

        Assert.Contains("Желудочки не найдены", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mask_is_still_called_unvalidated()
    {
        var text = SegmentationReadout.Describe(Result(MeasurementQuality.Questionable, issue: null, filled: true));

        Assert.Contains("не является проверенной сегментацией", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_measured_evans_index_is_given_with_where_to_see_its_segments()
    {
        // Число без места измерения проверить нельзя.
        var text = SegmentationReadout.Describe(
            Result(MeasurementQuality.Questionable, issue: null, filled: true),
            AcquisitionTier.Extended,
            new AutomaticEvansResult
            {
                Biomarker = new Biomarker
                {
                    Method = new MeasurementMethod
                    {
                        Code = LinearBiomarkers.EvansIndexCode,
                        DefinitionVersion = LinearBiomarkers.DefinitionVersion,
                        RequiredTier = AcquisitionTier.Baseline,
                    },
                    Value = 0.264,
                    Unit = MeasurementUnit.Ratio,
                    Quality = MeasurementQuality.Questionable,
                    AllowedRange = new MeasurementRange(0.10, 0.60),
                },
            });

        Assert.Contains("Индекс Эванса 0,26 (сомнительно)", text, StringComparison.Ordinal);
        Assert.Contains("Разборе метода", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_evans_index_not_derived_is_named_by_its_reason()
    {
        var text = SegmentationReadout.Describe(
            Result(MeasurementQuality.Questionable, issue: null, filled: true),
            AcquisitionTier.Extended,
            new AutomaticEvansResult { Refusal = AutomaticEvansRefusal.FrontalHornsNotFound });

        Assert.Contains("Индекс Эванса не посчитан", text, StringComparison.Ordinal);
        Assert.Contains("обоих желудочков", text, StringComparison.Ordinal);
    }

    private static BaselineSegmentationResult Result(MeasurementQuality quality, QualityIssue? issue, bool filled)
    {
        var labels = new byte[4 * 4 * 4];

        if (filled)
        {
            labels[21] = 1;
        }

        var mask = new VoxelMask(
            Grid,
            new LabelMap { Version = "test-1.0.0", Labels = [new AnatomicalLabel("ventricles")] },
            labels);

        return new BaselineSegmentationResult(mask, issue is null ? [] : [issue], quality);
    }
}
