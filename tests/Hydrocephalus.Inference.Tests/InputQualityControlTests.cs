using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Inference.QualityControl;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Входной контроль качества по геометрии и метаданным.
///
/// Тесты фиксируют не только то, что контроль ловит, но и границу его области:
/// проверки, которым нужны воксели, здесь не выполняются, и «QC пройден» не должно
/// читаться как «изображение проверено целиком».
/// </summary>
public sealed class InputQualityControlTests
{
    private const string SeriesId = "series-1";

    [Fact]
    public void Clean_extended_series_passes()
    {
        var assessment = Evaluate(Geometry());

        Assert.Empty(assessment.Issues);
        Assert.True(assessment.IsAcceptable);
    }

    [Fact]
    public void Clean_baseline_series_passes()
    {
        // Рутинная 2D-серия выборки: 5мм, покрытие меньше объёмного порога.
        // Линейные измерения выполняются на одном срезе, поэтому это не отказ.
        var assessment = Evaluate(Geometry(
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 5.0,
            slices: 24,
            pixelSpacing: new InPlaneSpacing(0.9, 0.9)));

        Assert.Empty(assessment.Issues);
    }

    [Fact]
    public void Inconsistent_geometry_blocks_and_stops_further_checks()
    {
        // Остальные проверки опираются на размеры и косинусы: на противоречивой
        // геометрии они дали бы правдоподобные, но бессмысленные значения.
        var assessment = Evaluate(Geometry(sliceThickness: 0));

        var issue = Assert.Single(assessment.Issues);

        Assert.Equal(QualityIssueCode.InconsistentGeometry, issue.Code);
        Assert.Equal(QualityIssueSeverity.Blocking, issue.Severity);
        Assert.False(assessment.IsAcceptable);
    }

    [Fact]
    public void Contrast_enhanced_series_is_blocked_even_though_selection_should_have_filtered_it()
    {
        // Проверка намеренно дублирует отбор серии в сценарии анализа: контроль
        // качества не должен зависеть от того, что вход отфильтровали правильно.
        var assessment = Evaluate(Geometry(), isContrastEnhanced: true);

        Assert.Contains(
            assessment.Issues,
            issue => issue.Code == QualityIssueCode.ContrastEnhancedSeries
                && issue.Severity == QualityIssueSeverity.Blocking);
    }

    [Fact]
    public void Slice_thickness_beyond_the_supported_range_is_blocked()
    {
        // Рутинные серии выборки — 5–7мм; 10мм за пределами того, на чём
        // конвейер валидируется.
        var assessment = Evaluate(Geometry(
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 10.0,
            slices: 14));

        var issue = Assert.Single(
            assessment.Issues,
            item => item.Code == QualityIssueCode.UnsupportedVoxelGeometry);

        Assert.Equal(QualityIssueSeverity.Blocking, issue.Severity);
        Assert.Equal("sliceThickness", issue.Parameters["parameter"]);
        Assert.Equal("10", issue.Parameters["valueMm"]);
    }

    [Fact]
    public void In_plane_spacing_beyond_the_supported_range_is_blocked()
    {
        var assessment = Evaluate(Geometry(pixelSpacing: new InPlaneSpacing(3.0, 3.0)));

        Assert.Contains(
            assessment.Issues,
            issue => issue.Code == QualityIssueCode.UnsupportedVoxelGeometry
                && issue.Parameters["parameter"] == "pixelSpacing");
    }

    [Fact]
    public void Strongly_non_square_pixels_are_blocked()
    {
        // Единственный результат базового уровня — линейные измерения,
        // и неквадратный пиксель искажает именно их.
        var assessment = Evaluate(Geometry(pixelSpacing: new InPlaneSpacing(0.5, 1.8)));

        Assert.Contains(
            assessment.Issues,
            issue => issue.Code == QualityIssueCode.UnsupportedVoxelGeometry
                && issue.Parameters["parameter"] == "inPlaneAnisotropy");
    }

    [Fact]
    public void Slice_gap_warns_but_does_not_block()
    {
        // Замечание повторяет проверку приёмки намеренно: замечания импорта
        // не входят в запрос на анализ. Зазор не мешает линейным измерениям
        // на одном срезе, но занижает любой объём.
        var assessment = Evaluate(Geometry(
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 5.0,
            sliceSpacing: 6.5,
            slices: 24,
            pixelSpacing: new InPlaneSpacing(0.9, 0.9)));

        var issue = Assert.Single(assessment.Issues);

        Assert.Equal(QualityIssueCode.UnsupportedVoxelGeometry, issue.Code);
        Assert.Equal(QualityIssueSeverity.Warning, issue.Severity);
        Assert.Equal("sliceGap", issue.Parameters["parameter"]);
        Assert.True(assessment.IsAcceptable);
    }

    [Fact]
    public void Field_of_view_too_small_for_a_head_is_blocked()
    {
        // 64 × 0.5мм = 32мм: голова взрослого в такое поле не помещается физически.
        var assessment = Evaluate(Geometry(
            columns: 64,
            rows: 64,
            pixelSpacing: new InPlaneSpacing(0.5, 0.5)));

        var issue = Assert.Single(
            assessment.Issues,
            item => item.Code == QualityIssueCode.HeadTruncated);

        Assert.Equal(QualityIssueSeverity.Blocking, issue.Severity);
        Assert.Equal("fieldOfView", issue.Parameters["parameter"]);
    }

    [Fact]
    public void Head_positioned_off_centre_is_not_detected_from_metadata()
    {
        // Известная граница области проверки: по метаданным видно только поле
        // обзора. Голова, смещённая за край достаточно большого поля, требует
        // проверки по вокселям и здесь не обнаруживается.
        var assessment = Evaluate(Geometry(origin: new SpatialVector(400, 400, 400)));

        Assert.Empty(assessment.Issues);
    }

    [Fact]
    public void Partial_volume_coverage_warns_but_does_not_block()
    {
        // Объёмные признаки на неполном покрытии недостоверны, но линейные
        // измерения по такой серии возможны.
        var assessment = Evaluate(Geometry(slices: 40, sliceThickness: 1.0));

        var issue = Assert.Single(assessment.Issues);

        Assert.Equal(QualityIssueCode.HeadTruncated, issue.Code);
        Assert.Equal(QualityIssueSeverity.Warning, issue.Severity);
        Assert.Equal("sliceCoverage", issue.Parameters["parameter"]);
        Assert.True(assessment.IsAcceptable);
    }

    [Fact]
    public void Partial_coverage_is_not_checked_on_the_baseline_tier()
    {
        var assessment = Evaluate(Geometry(
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 5.0,
            slices: 5,
            pixelSpacing: new InPlaneSpacing(0.9, 0.9)));

        Assert.Empty(assessment.Issues);
    }

    [Fact]
    public void Limits_are_configurable_without_touching_the_checks()
    {
        // Пороги пересматриваются вместе с dataset manifest при пополнении выборки,
        // поэтому они вынесены в отдельный тип.
        var geometry = Geometry(
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 8.0,
            slices: 20);

        Assert.Contains(
            Evaluate(geometry).Issues,
            issue => issue.Code == QualityIssueCode.UnsupportedVoxelGeometry);

        var relaxed = new InputQualityControl(
            new InputQualityLimits { MaxSliceThicknessMillimetres = 9.0 });

        Assert.Empty(relaxed.Evaluate(Request(Series(geometry, isContrastEnhanced: false))).Issues);
    }

    [Fact]
    public void Request_naming_an_unknown_series_is_a_rule_violation()
    {
        // Рассогласование запроса и рабочей копии, а не проблема качества:
        // «чистый QC» здесь означал бы допуск анализа неизвестно чего.
        var study = new ImagingStudy
        {
            PseudonymousStudyId = "study-1",
            PseudonymousSubjectId = "subject-1",
            Series = [Series(Geometry(), isContrastEnhanced: false)],
        };

        var request = new AnalysisRequest
        {
            Study = study,
            PseudonymousSeriesId = "series-does-not-exist",
            VolumeReference = "volume",
        };

        Assert.Throws<DomainRuleViolationException>(() => new InputQualityControl().Evaluate(request));
    }

    [Fact]
    public void Passing_quality_control_does_not_mean_the_image_was_inspected()
    {
        // Проверки по вокселям — двигательные артефакты и выход за пределы
        // обучающего распределения — здесь не выполняются и появятся вместе
        // с model package (M4). Тест закрепляет границу области проверки.
        var assessment = Evaluate(Geometry());

        Assert.DoesNotContain(
            assessment.Issues,
            issue => issue.Code is QualityIssueCode.MotionArtefact or QualityIssueCode.OutOfDistribution);

        Assert.True(assessment.IsAcceptable);
    }

    private static QualityAssessment Evaluate(SeriesGeometry geometry, bool isContrastEnhanced = false) =>
        new InputQualityControl().Evaluate(Request(Series(geometry, isContrastEnhanced)));

    private static AnalysisRequest Request(ImagingSeries series) =>
        new()
        {
            Study = new ImagingStudy
            {
                PseudonymousStudyId = "study-1",
                PseudonymousSubjectId = "subject-1",
                Series = [series],
            },
            PseudonymousSeriesId = series.PseudonymousSeriesId,
            VolumeReference = "volume",
        };

    private static ImagingSeries Series(SeriesGeometry geometry, bool isContrastEnhanced) =>
        new()
        {
            PseudonymousSeriesId = SeriesId,
            Geometry = geometry,
            Weighting = SeriesWeighting.T1,
            IsContrastEnhanced = isContrastEnhanced,
        };

    private static SeriesGeometry Geometry(
        MrAcquisitionType acquisitionType = MrAcquisitionType.ThreeDimensional,
        double sliceThickness = 1.0,
        InPlaneSpacing? pixelSpacing = null,
        int columns = 256,
        int rows = 256,
        int slices = 180,
        SpatialVector? origin = null,
        double? sliceSpacing = null) =>
        new()
        {
            AcquisitionType = acquisitionType,
            SliceThicknessMillimetres = sliceThickness,
            SliceSpacingMillimetres = sliceSpacing ?? sliceThickness,
            PixelSpacing = pixelSpacing ?? new InPlaneSpacing(0.9, 0.9),
            Dimensions = new VolumeDimensions(columns, rows, slices),
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = origin ?? new SpatialVector(0, 0, 0),
        };
}
