using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Measurements;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Индекс Эванса, отмеченный врачом.
///
/// Случай, ради которого разметка и нужна: автоматический индекс требует маски
/// объёмной серии, а в контрольной группе ни одной пригодной объёмной серии нет
/// (docs/data/README.md). Сравнение групп поэтому возможно только руками, на
/// рутинных толстосрезовых сериях.
///
/// Проверяется не арифметика — она у автоматического пути общая, — а то, что
/// разметка не даёт получить правдоподобное число из неправильных точек.
/// </summary>
public sealed class ManualEvansIndexTests
{
    [Fact]
    public void Four_points_on_a_thick_sliced_series_give_the_index()
    {
        // Рога 40мм, череп 160мм: отношение 0,25.
        var volume = ThickSlicedAxial();
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(60, 100, 10)));
        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(100, 100, 10)));
        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(20, 100, 10)));
        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(180, 100, 10)));

        Assert.True(marking.IsComplete);

        var result = marking.Measure(volume);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.Equal(0.25, result.Biomarker.Value, precision: 9);
        Assert.False(result.Biomarker.IsOutOfRange);
    }

    [Fact]
    public void A_manual_measurement_is_told_apart_from_an_automatic_one()
    {
        // На объёмных сериях оба измерения существуют одновременно. Без различия
        // в коде метода это две записи одного признака, и сказать, какая из них
        // чья, было бы нечем — а сверять автоматический метод предстоит именно
        // с ручным.
        var result = Complete().Measure(ThickSlicedAxial());

        Assert.NotNull(result.Biomarker);
        Assert.Equal(LinearBiomarkers.ManualEvansIndexCode, result.Biomarker.Method.Code);
        Assert.NotEqual(LinearBiomarkers.EvansIndexCode, result.Biomarker.Method.Code);
    }

    [Fact]
    public void A_manual_measurement_is_not_marked_questionable()
    {
        // Сознательное отличие от автоматического пути: тот сомнителен потому,
        // что не сверен с ручной разметкой. Ручная разметка и есть эта разметка.
        var result = Complete().Measure(ThickSlicedAxial());

        Assert.NotNull(result.Biomarker);
        Assert.Equal(MeasurementQuality.Reliable, result.Biomarker.Quality);
    }

    [Fact]
    public void Marking_on_another_slice_starts_over_instead_of_measuring_across_slices()
    {
        // Врач листает срезы, подбирая уровень. Точки, поставленные до
        // перелистывания, к новому срезу отношения не имеют, и косое измерение
        // выглядело бы как обычное.
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(60, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));

        Assert.Equal(
            MarkingOutcome.RestartedOnAnotherPlane,
            marking.Add(new VoxelPosition(20, 100, 11)));

        Assert.Single(marking.Points);
        Assert.Equal(11, marking.PlaneIndex);
    }

    [Fact]
    public void An_incomplete_marking_refuses_instead_of_measuring_what_it_has()
    {
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(60, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(20, 100, 10));

        var result = marking.Measure(ThickSlicedAxial());

        Assert.Equal(ManualEvansRefusal.PointsIncomplete, result.Refusal);
        Assert.Null(result.Biomarker);
    }

    [Fact]
    public void Coincident_skull_points_are_refused_by_name()
    {
        // Промах постановки: диаметр нулевой. Назвать причину нужно, потому что
        // на экране два совпавших нажатия ничем не отличаются от одного.
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(60, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(20, 100, 10));
        marking.Add(new VoxelPosition(20, 100, 10));

        var result = marking.Measure(ThickSlicedAxial());

        Assert.Equal(ManualEvansRefusal.SkullDiameterIsZero, result.Refusal);
        Assert.Null(result.Biomarker);
    }

    [Fact]
    public void The_last_point_can_be_taken_back()
    {
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(60, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));

        Assert.True(marking.UndoLast());
        Assert.Single(marking.Points);

        Assert.True(marking.UndoLast());
        Assert.Empty(marking.Points);
        Assert.Null(marking.PlaneIndex);

        Assert.False(marking.UndoLast());
    }

    [Fact]
    public void A_fifth_point_is_not_taken()
    {
        var marking = Complete();

        Assert.Equal(MarkingOutcome.AlreadyComplete, marking.Add(new VoxelPosition(1, 1, 10)));
        Assert.Equal(ManualEvansMarking.RequiredPoints, marking.Points.Count);
    }

    [Fact]
    public void An_implausible_index_is_recorded_and_flagged_rather_than_hidden()
    {
        // Соглашение то же, что у автоматического пути: значение записывается,
        // а вне диапазона его объявляет непригодным читалка результата. Скрывать
        // его нельзя — врач должен увидеть, что точки стоят не там.
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        // Рога шире, чем полчерепа: 150мм против 160мм, отношение 0,94.
        marking.Add(new VoxelPosition(20, 100, 10));
        marking.Add(new VoxelPosition(170, 100, 10));
        marking.Add(new VoxelPosition(20, 100, 10));
        marking.Add(new VoxelPosition(180, 100, 10));

        var result = marking.Measure(ThickSlicedAxial());

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.True(result.Biomarker.IsOutOfRange);
    }

    [Fact]
    public void The_segments_carry_the_plane_they_were_marked_on()
    {
        // Отрезки рисуются тем же путём, что автоматические, поэтому плоскость
        // должна приехать вместе с ними — иначе оверлей ляжет на другой срез.
        var result = Complete().Measure(ThickSlicedAxial());

        Assert.NotNull(result.Segments);
        Assert.Equal(10, result.Segments.PlaneIndex);
        Assert.Equal(VolumeAxis.AcrossSlices, result.Segments.AxialAcross);
    }

    private static ManualEvansMarking Complete()
    {
        var marking = new ManualEvansMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(60, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(20, 100, 10));
        marking.Add(new VoxelPosition(180, 100, 10));

        return marking;
    }

    private static TestVolume ThickSlicedAxial()
    {
        var dimensions = new VolumeDimensions(200, 200, 40);

        return new TestVolume(
            new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.TwoDimensional,
                SliceThicknessMillimetres = 5.0,
                SliceSpacingMillimetres = 5.0,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            },
            new VolumeGrid(dimensions, 1.0, 1.0, 5.0));
    }

    // Значения вокселей не нужны: разметка приходит точками, а не находится
    // по яркости. Проверяется геометрия и порядок, а не содержимое снимка.
    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => 0;

        public float this[int column, int row, int slice] => 0;
    }
}
