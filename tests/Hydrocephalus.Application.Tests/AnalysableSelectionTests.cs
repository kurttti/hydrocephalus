using Hydrocephalus.Application;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Выбор серии и исследования для анализа.
///
/// Врач выбирает папку, а не серию, и приложение обязано само взять то,
/// по чему можно измерять. Ошибка здесь не видна как ошибка: открывается
/// «какая-то» серия, маска не строится, и отказ выглядит свойством данных.
/// </summary>
public sealed class AnalysableSelectionTests
{
    [Fact]
    public void A_volume_series_of_known_weighting_wins_over_one_of_unknown_weighting()
    {
        // В выборке так было у 15 исследований: первой по уровню шла 3D-серия
        // с нераспознанной взвешенностью, по которой сегментация не запускается.
        var unknown = Series("unknown", SeriesWeighting.Unknown, slices: 200);
        var t1 = Series("t1", SeriesWeighting.T1, slices: 160);

        var chosen = AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", unknown, t1));

        Assert.Same(t1, chosen);
    }

    [Theory]
    [InlineData(SeriesWeighting.T2)]
    [InlineData(SeriesWeighting.Flair)]
    public void T1_is_preferred_among_volume_series(SeriesWeighting other)
    {
        var alternative = Series("other", other, slices: 200);
        var t1 = Series("t1", SeriesWeighting.T1, slices: 160);

        Assert.Same(t1, AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", alternative, t1)));
        Assert.Same(t1, AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", t1, alternative)));
    }

    [Fact]
    public void Among_equal_series_the_one_covering_more_is_taken()
    {
        // Прицельный блок охватывает желудочки не целиком.
        var slab = Series("slab", SeriesWeighting.T2, slices: 60);
        var whole = Series("whole", SeriesWeighting.T2, slices: 176);

        Assert.Same(whole, AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", slab, whole)));
    }

    [Fact]
    public void The_acquisition_tier_still_comes_first()
    {
        // Взвешенность не перевешивает уровень: по толстым срезам объём не считается.
        var thickT1 = Series("thick", SeriesWeighting.T1, slices: 24, sliceMillimetres: 5);
        var volumeT2 = Series("volume", SeriesWeighting.T2, slices: 176);

        Assert.Same(volumeT2, AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", thickT1, volumeT2)));
    }

    [Fact]
    public void A_contrast_enhanced_series_is_never_chosen()
    {
        var enhanced = Series("enhanced", SeriesWeighting.T1, slices: 176, contrast: true);

        Assert.Null(AnalyzeStudyUseCase.SelectAnalysableSeries(Study("s", enhanced)));
    }

    [Fact]
    public void The_study_holding_the_best_series_is_chosen_whatever_its_order()
    {
        // Папка пациента с несколькими исследованиями открывается, а не
        // отвергается, и выбор не зависит от порядка файлов на диске.
        var older = Study("older", Series("a", SeriesWeighting.T2, slices: 24, sliceMillimetres: 5));
        var volume = Study("volume", Series("b", SeriesWeighting.T1, slices: 176));

        Assert.Same(volume, AnalyzeStudyUseCase.SelectAnalysableStudy([older, volume]));
        Assert.Same(volume, AnalyzeStudyUseCase.SelectAnalysableStudy([volume, older]));
    }

    [Fact]
    public void Without_any_analysable_series_the_first_study_is_opened_for_viewing()
    {
        // Смотреть снимки можно и там, где измерять нечего.
        var first = Study("first", Series("a", SeriesWeighting.T1, slices: 176, contrast: true));
        var second = Study("second", Series("b", SeriesWeighting.T1, slices: 176, contrast: true));

        Assert.Same(first, AnalyzeStudyUseCase.SelectAnalysableStudy([first, second]));
        Assert.Null(AnalyzeStudyUseCase.SelectAnalysableStudy([]));
    }

    private static ImagingStudy Study(string id, params ImagingSeries[] series) => new()
    {
        PseudonymousStudyId = id,
        PseudonymousSubjectId = "subject",
        Series = series,
    };

    private static ImagingSeries Series(
        string id,
        SeriesWeighting weighting,
        int slices,
        double sliceMillimetres = 1.0,
        bool contrast = false) => new()
        {
            PseudonymousSeriesId = id,
            Weighting = weighting,
            IsContrastEnhanced = contrast,
            Geometry = new SeriesGeometry
            {
                AcquisitionType = sliceMillimetres <= 1.5
                    ? MrAcquisitionType.ThreeDimensional
                    : MrAcquisitionType.TwoDimensional,
                SliceThicknessMillimetres = sliceMillimetres,
                SliceSpacingMillimetres = sliceMillimetres,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = new VolumeDimensions(256, 256, slices),
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            },
        };
}
