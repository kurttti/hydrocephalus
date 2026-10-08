using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Память о разметке последней серии.
///
/// Одну и ту же серию размечают дважды: просмотрщик — для экрана, конвейер —
/// для отчёта. С пороговым методом это стоило секунды, с моделью — около минуты
/// на каждый раз, и открытие исследования встало бы на это время дважды.
/// </summary>
public sealed class CachingVentricleSegmentationTests
{
    [Fact]
    public void The_same_series_is_segmented_once()
    {
        var inner = new CountingSegmentation();
        var caching = new CachingVentricleSegmentation(inner);

        caching.Segment(Volume(), SeriesWeighting.T1, "series-1");
        caching.Segment(Volume(), SeriesWeighting.T1, "series-1");

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Another_series_is_segmented_anew()
    {
        var inner = new CountingSegmentation();
        var caching = new CachingVentricleSegmentation(inner);

        caching.Segment(Volume(), SeriesWeighting.T1, "series-1");
        caching.Segment(Volume(), SeriesWeighting.T1, "series-2");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Returning_to_the_previous_series_segments_it_again()
    {
        // Помнится ровно один ответ: врач работает с одной серией за раз, а
        // маска — байт на воксель, то есть мегабайты. Словарь на всё
        // исследование съел бы память ради случая, которого в работе нет.
        var inner = new CountingSegmentation();
        var caching = new CachingVentricleSegmentation(inner);

        caching.Segment(Volume(), SeriesWeighting.T1, "series-1");
        caching.Segment(Volume(), SeriesWeighting.T1, "series-2");
        caching.Segment(Volume(), SeriesWeighting.T1, "series-1");

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public void A_refusal_is_remembered_too()
    {
        // Отказ стоил полной разметки наравне с успехом.
        var inner = new CountingSegmentation(MeasurementQuality.Unreliable);
        var caching = new CachingVentricleSegmentation(inner);

        var first = caching.Segment(Volume(), SeriesWeighting.T1, "series-1");
        var second = caching.Segment(Volume(), SeriesWeighting.T1, "series-1");

        Assert.Equal(MeasurementQuality.Unreliable, second.Quality);
        Assert.Equal(first.Quality, second.Quality);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public void Provenance_is_that_of_the_wrapped_way()
    {
        var caching = new CachingVentricleSegmentation(new CountingSegmentation());

        Assert.Equal(CountingSegmentation.Code, caching.Provenance);
    }

    private static TestVolume Volume() => new();

    private sealed class CountingSegmentation(
        MeasurementQuality quality = MeasurementQuality.Questionable) : IVentricleSegmentation
    {
        internal const string Code = "counting/1";

        public string Provenance => Code;

        internal int Calls { get; private set; }

        public VentricleSegmentationResult Segment(
            IVoxelVolume volume,
            SeriesWeighting weighting,
            string pseudonymousSeriesId,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;

            var grid = volume.Grid;

            return new VentricleSegmentationResult(
                new VoxelMask(
                    grid,
                    new LabelMap { Version = Code, Labels = [] },
                    new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]),
                [],
                quality);
        }
    }

    private sealed class TestVolume : IVoxelVolume
    {
        private const int Size = 8;

        public TestVolume() => this.Geometry = new SeriesGeometry
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = 1.0,
            SliceSpacingMillimetres = 1.0,
            PixelSpacing = new InPlaneSpacing(1.0, 1.0),
            Dimensions = new VolumeDimensions(Size, Size, Size),
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = default,
        };

        public SeriesGeometry Geometry { get; }

        public VolumeGrid Grid { get; } =
            new(new VolumeDimensions(Size, Size, Size), 1.0, 1.0, 1.0);

        public float Minimum => 0;

        public float Maximum => 1;

        public float this[int column, int row, int slice] => 0;
    }
}
