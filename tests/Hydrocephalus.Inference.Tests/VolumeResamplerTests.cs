using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Inference.Preprocessing;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Приведение объёма к изотропной сетке.
///
/// Главное, что проверяется, — ресэмплинг не выдумывает данных: он сохраняет
/// физический размер и значения в узлах, не выходит за исходный диапазон
/// и не улучшает уровень входа. Ошибка здесь не выглядит как сбой: получается
/// гладкая правдоподобная картинка с неверными измерениями.
/// </summary>
public sealed class VolumeResamplerTests
{
    [Fact]
    public void Physical_size_survives_the_change_of_grid()
    {
        // 5 отсчётов с шагом 2мм — это 8мм от центра первого до центра последнего.
        // На шаге 1мм тех же 8мм соответствуют 9 отсчётов.
        var source = Volume(columns: 5, rows: 5, slices: 5, spacing: 2.0);

        var resampled = VolumeResampler.ToIsotropic(source, 1.0, CancellationToken.None);

        Assert.Equal(new VolumeDimensions(9, 9, 9), resampled.Grid.Dimensions);
        Assert.Equal(source.Grid.WidthMillimetres, resampled.Grid.WidthMillimetres, precision: 6);
        Assert.Equal(source.Grid.DepthMillimetres, resampled.Grid.DepthMillimetres, precision: 6);
    }

    [Fact]
    public void An_anisotropic_volume_becomes_isotropic()
    {
        var source = Volume(
            columns: 8,
            rows: 8,
            slices: 4,
            spacing: 1.0,
            sliceSpacing: 5.0);

        var resampled = VolumeResampler.ToIsotropic(source, 1.0, CancellationToken.None);

        Assert.True(resampled.Grid.IsIsotropic);
        Assert.Equal(16, resampled.Grid.Dimensions.Slices);
    }

    [Fact]
    public void Values_at_coinciding_nodes_are_unchanged()
    {
        // Узлы целевой сетки, совпадающие с исходными, обязаны дать ровно те же
        // значения: интерполяция не должна «подтягивать» соседей.
        var source = Volume(columns: 5, rows: 5, slices: 5, spacing: 2.0);

        var resampled = VolumeResampler.ToIsotropic(source, 2.0, CancellationToken.None);

        for (var slice = 0; slice < 5; slice++)
        {
            for (var row = 0; row < 5; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    Assert.Equal(source[column, row, slice], resampled[column, row, slice]);
                }
            }
        }
    }

    [Fact]
    public void A_midpoint_is_the_average_of_its_neighbours()
    {
        // Линейный образец: половинный шаг обязан дать ровно середину.
        var source = Volume(columns: 3, rows: 1, slices: 1, spacing: 2.0);

        var resampled = VolumeResampler.ToIsotropic(source, 1.0, CancellationToken.None);

        Assert.Equal(
            (source[0, 0, 0] + source[1, 0, 0]) / 2,
            resampled[1, 0, 0],
            tolerance: 1e-4f);
    }

    [Fact]
    public void Interpolation_stays_inside_the_source_range()
    {
        // Трилинейная интерполяция не даёт выбросов, а более высокие порядки дают:
        // интенсивность, которой в ткани не было, попала бы в маску.
        var source = Volume(columns: 6, rows: 6, slices: 6, spacing: 3.0);

        var resampled = VolumeResampler.ToIsotropic(source, 0.7, CancellationToken.None);

        Assert.True(resampled.Minimum >= source.Minimum);
        Assert.True(resampled.Maximum <= source.Maximum);
    }

    [Fact]
    public void Resampling_does_not_improve_the_acquisition_tier()
    {
        // Рутинная 2D-серия 5мм, приведённая к 1мм, остаётся базовым уровнем.
        // Интерполяция не добавляет данных, и объёмные признаки на ней
        // по-прежнему недоступны.
        var source = Volume(
            columns: 8,
            rows: 8,
            slices: 8,
            spacing: 1.0,
            sliceSpacing: 5.0,
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 5.0);

        Assert.Equal(AcquisitionTier.Baseline, source.Geometry.Tier);

        var resampled = VolumeResampler.ToIsotropic(source, 1.0, CancellationToken.None);

        Assert.Equal(AcquisitionTier.Baseline, resampled.Geometry.Tier);
        Assert.Equal(1.0, resampled.Grid.SliceSpacingMillimetres, precision: 6);
    }

    [Fact]
    public void A_thick_three_dimensional_series_also_keeps_its_tier()
    {
        // 3D-серия с шагом 3мм — базовый уровень по фактической плотности выборки.
        // Приведение к 1мм не должно превращать её в расширенный.
        var source = Volume(
            columns: 8,
            rows: 8,
            slices: 8,
            spacing: 1.0,
            sliceSpacing: 3.0,
            acquisitionType: MrAcquisitionType.ThreeDimensional,
            sliceThickness: 3.0);

        Assert.Equal(AcquisitionTier.Baseline, source.Geometry.Tier);

        Assert.Equal(
            AcquisitionTier.Baseline,
            VolumeResampler.ToIsotropic(source, 1.0, CancellationToken.None).Geometry.Tier);
    }

    [Fact]
    public void Side_labels_survive_the_resampling()
    {
        // Сетка другая, стороны пациента те же.
        var source = Volume(columns: 4, rows: 4, slices: 4, spacing: 1.0);

        var resampled = VolumeResampler.ToIsotropic(source, 0.5, CancellationToken.None);

        Assert.Equal(source.Geometry.RowDirection, resampled.Geometry.RowDirection);
        Assert.Equal(AnatomicalDirection.Left, resampled.Geometry.RowDirectionTowards);
    }

    [Fact]
    public void The_result_is_the_same_every_time()
    {
        // Одинаковый вход даёт одинаковый результат: без этого golden-тесты
        // измерений теряют смысл.
        var source = Volume(columns: 5, rows: 5, slices: 5, spacing: 1.3);

        var first = VolumeResampler.ToIsotropic(source, 0.9, CancellationToken.None);
        var again = VolumeResampler.ToIsotropic(source, 0.9, CancellationToken.None);

        for (var slice = 0; slice < first.Grid.Dimensions.Slices; slice++)
        {
            for (var row = 0; row < first.Grid.Dimensions.Rows; row++)
            {
                for (var column = 0; column < first.Grid.Dimensions.Columns; column++)
                {
                    Assert.Equal(first[column, row, slice], again[column, row, slice]);
                }
            }
        }
    }

    [Fact]
    public void The_natural_spacing_is_the_finest_the_source_actually_has()
    {
        // Взять шаг мельче исходного значило бы платить памятью и временем
        // за отсчёты, которые не несут данных.
        var source = Volume(columns: 8, rows: 8, slices: 4, spacing: 0.8, sliceSpacing: 5.0);

        Assert.Equal(0.8, VolumeResampler.NaturalSpacingOf(source), precision: 6);
    }

    [Fact]
    public void A_grid_that_would_not_fit_in_memory_is_refused()
    {
        // Шаг задаётся снаружи, и опечатка в нём не должна оборачиваться
        // попыткой выделить десятки гигабайт.
        var source = Volume(columns: 512, rows: 512, slices: 512, spacing: 1.0);

        Assert.Throws<InvalidOperationException>(
            () => VolumeResampler.ToIsotropic(source, 0.1, CancellationToken.None));
    }

    [Fact]
    public void A_spacing_below_the_limit_is_refused()
    {
        var source = Volume(columns: 4, rows: 4, slices: 4, spacing: 1.0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VolumeResampler.ToIsotropic(source, 0.0, CancellationToken.None));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var source = Volume(columns: 64, rows: 64, slices: 64, spacing: 1.0);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => VolumeResampler.ToIsotropic(source, 0.5, cancellation.Token));
    }

    private static TestVolume Volume(
        int columns,
        int rows,
        int slices,
        double spacing,
        double? sliceSpacing = null,
        MrAcquisitionType acquisitionType = MrAcquisitionType.ThreeDimensional,
        double sliceThickness = 1.0) =>
        new(
            new SeriesGeometry
            {
                AcquisitionType = acquisitionType,
                SliceThicknessMillimetres = sliceThickness,
                SliceSpacingMillimetres = sliceSpacing ?? spacing,
                PixelSpacing = new InPlaneSpacing(spacing, spacing),
                Dimensions = new VolumeDimensions(columns, rows, slices),
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            },
            new VolumeGrid(
                new VolumeDimensions(columns, rows, slices),
                spacing,
                spacing,
                sliceSpacing ?? spacing));

    /// <summary>
    /// Объём с известным значением в каждом узле: значение линейно по всем трём
    /// осям, поэтому середина между узлами обязана быть их средним, и любая
    /// перестановка осей даёт заведомо другое число.
    /// </summary>
    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => Value(
            this.Grid.Dimensions.Columns - 1,
            this.Grid.Dimensions.Rows - 1,
            this.Grid.Dimensions.Slices - 1);

        public float this[int column, int row, int slice] => Value(column, row, slice);

        private static float Value(int column, int row, int slice) =>
            (slice * 100) + (row * 10) + column;
    }
}
