using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Measurements;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Линейные признаки на фантоме с известным ответом.
///
/// Проверяется прежде всего то, что измерение идёт в миллиметрах системы
/// координат пациента, а не в индексах вокселей. На изотропной сетке разницы
/// не видно — отношение сокращает её, а угол совпадает; ошибка проявляется
/// только на анизотропной, то есть ровно на рутинных 2D-сериях выборки.
/// </summary>
public sealed class LinearBiomarkerTests
{
    [Fact]
    public void Distance_is_measured_in_millimetres_and_not_in_voxels()
    {
        // Шаг по столбцам 2мм: десять шагов сетки — это 20мм.
        var volume = Axial(columnSpacing: 2.0, rowSpacing: 1.0);

        var distance = PatientSpace.DistanceMillimetres(
            volume,
            new VoxelPosition(0, 0, 0),
            new VoxelPosition(10, 0, 0));

        Assert.Equal(20.0, distance, precision: 9);
    }

    [Fact]
    public void An_angle_on_an_anisotropic_grid_is_not_the_angle_between_indices()
    {
        // Отрезки по 10 шагов сетки: в индексах угол ровно 45°, но шаг по
        // столбцам вдвое крупнее, поэтому в анатомии угол другой. Считать
        // по индексам значило бы ошибиться на полтора десятка градусов.
        var volume = Axial(columnSpacing: 2.0, rowSpacing: 1.0);

        var angle = PatientSpace.AngleDegrees(
            volume,
            new VoxelPosition(0, 0, 0),
            new VoxelPosition(10, 0, 0),
            new VoxelPosition(10, 10, 0));

        Assert.Equal(26.565, angle, tolerance: 0.001);
    }

    [Fact]
    public void A_right_angle_is_a_right_angle()
    {
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        var angle = PatientSpace.AngleDegrees(
            volume,
            new VoxelPosition(5, 5, 0),
            new VoxelPosition(15, 5, 0),
            new VoxelPosition(5, 15, 0));

        Assert.Equal(90.0, angle, precision: 9);
    }

    [Fact]
    public void Coincident_points_do_not_produce_a_measurement()
    {
        // Метка, поставленная не туда, должна называться ошибкой, а не давать
        // ноль градусов, неотличимый от настоящего измерения.
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        Assert.Throws<DomainRuleViolationException>(() => PatientSpace.AngleDegrees(
            volume,
            new VoxelPosition(5, 5, 0),
            new VoxelPosition(5, 5, 0),
            new VoxelPosition(15, 5, 0)));
    }

    [Fact]
    public void Evans_index_is_the_ratio_of_the_two_widths()
    {
        // Рога 40мм, череп 140мм: отношение 0.2857…
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        var biomarker = LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12));

        Assert.Equal(40.0 / 140.0, biomarker.Value, precision: 9);
        Assert.Equal(MeasurementUnit.Ratio, biomarker.Unit);
        Assert.Equal(LinearBiomarkers.EvansIndexCode, biomarker.Method.Code);
        Assert.False(biomarker.IsOutOfRange);
    }

    [Fact]
    public void Evans_index_on_an_anisotropic_grid_uses_millimetres()
    {
        // Оба отрезка идут вдоль столбцов, поэтому отношение сокращает шаг —
        // и именно поэтому ошибка «считать в индексах» здесь незаметна.
        // Тест закрепляет, что результат не зависит от шага сетки.
        var square = LinearBiomarkers.EvansIndex(
            Axial(columnSpacing: 1.0, rowSpacing: 1.0),
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12));

        var stretched = LinearBiomarkers.EvansIndex(
            Axial(columnSpacing: 2.0, rowSpacing: 1.0),
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12));

        Assert.Equal(square.Value, stretched.Value, precision: 9);
    }

    [Fact]
    public void Evans_index_needs_an_axial_plane()
    {
        // В другой плоскости те же точки дают число того же вида и другого смысла.
        var coronal = Coronal();

        Assert.Throws<DomainRuleViolationException>(() => LinearBiomarkers.EvansIndex(
            coronal,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12)));
    }

    [Fact]
    public void Points_from_different_slices_are_refused()
    {
        // Иначе в длину войдёт расстояние между срезами, и измерение перестанет
        // быть тем, что называется.
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        Assert.Throws<DomainRuleViolationException>(() => LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 13),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12)));
    }

    [Fact]
    public void A_zero_skull_diameter_is_refused()
    {
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        Assert.Throws<DomainRuleViolationException>(() => LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(10, 60, 12)));
    }

    [Fact]
    public void An_implausible_ratio_is_flagged_rather_than_hidden()
    {
        // Рога шире черепа — постановка точек неверна. Признак считается,
        // но помечается вышедшим за диапазон, а не молча используется.
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        var biomarker = LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12),
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12));

        Assert.True(biomarker.IsOutOfRange);
    }

    [Fact]
    public void Callosal_angle_is_measured_on_a_coronal_plane()
    {
        var volume = Coronal();

        var biomarker = LinearBiomarkers.CallosalAngle(
            volume,
            new VoxelPosition(80, 60, 20),
            new VoxelPosition(60, 40, 20),
            new VoxelPosition(100, 40, 20));

        Assert.Equal(90.0, biomarker.Value, precision: 6);
        Assert.Equal(MeasurementUnit.Degree, biomarker.Unit);
        Assert.Equal(LinearBiomarkers.CallosalAngleCode, biomarker.Method.Code);
    }

    [Fact]
    public void Callosal_angle_refuses_an_axial_plane()
    {
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        Assert.Throws<DomainRuleViolationException>(() => LinearBiomarkers.CallosalAngle(
            volume,
            new VoxelPosition(80, 60, 20),
            new VoxelPosition(60, 40, 20),
            new VoxelPosition(100, 40, 20)));
    }

    [Fact]
    public void Linear_measurements_are_available_at_the_baseline_tier()
    {
        // Единственный результат базового уровня входа — линейные измерения,
        // и требовать для них объёмную серию было бы отказом на ровном месте.
        var volume = Axial(
            columnSpacing: 1.0,
            rowSpacing: 1.0,
            acquisitionType: MrAcquisitionType.TwoDimensional,
            sliceThickness: 5.0);

        Assert.Equal(AcquisitionTier.Baseline, volume.Geometry.Tier);

        var biomarker = LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(90, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12));

        Assert.Equal(AcquisitionTier.Baseline, biomarker.Method.RequiredTier);
    }

    [Fact]
    public void The_diagnostic_threshold_is_not_encoded_in_the_measurement()
    {
        // Пороги принадлежат модели и протоколу валидации, а не измерителю:
        // диапазон здесь проверяет правдоподобие постановки точек.
        var volume = Axial(columnSpacing: 1.0, rowSpacing: 1.0);

        var biomarker = LinearBiomarkers.EvansIndex(
            volume,
            new VoxelPosition(50, 60, 12),
            new VoxelPosition(100, 60, 12),
            new VoxelPosition(10, 60, 12),
            new VoxelPosition(150, 60, 12));

        // 0.357 — выше общеизвестного 0.3, но признак остаётся в допустимом
        // диапазоне: измеритель не выносит суждения.
        Assert.True(biomarker.Value > 0.3);
        Assert.False(biomarker.IsOutOfRange);
    }

    private static TestVolume Axial(
        double columnSpacing,
        double rowSpacing,
        MrAcquisitionType acquisitionType = MrAcquisitionType.ThreeDimensional,
        double sliceThickness = 1.0) =>
        Build(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, 1, 0),
            columnSpacing,
            rowSpacing,
            acquisitionType,
            sliceThickness);

    private static TestVolume Coronal() =>
        Build(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, 0, -1),
            1.0,
            1.0,
            MrAcquisitionType.ThreeDimensional,
            1.0);

    private static TestVolume Build(
        SpatialVector rowDirection,
        SpatialVector columnDirection,
        double columnSpacing,
        double rowSpacing,
        MrAcquisitionType acquisitionType,
        double sliceThickness)
    {
        var dimensions = new VolumeDimensions(200, 200, 40);

        return new TestVolume(
            new SeriesGeometry
            {
                AcquisitionType = acquisitionType,
                SliceThicknessMillimetres = sliceThickness,
                SliceSpacingMillimetres = sliceThickness,
                PixelSpacing = new InPlaneSpacing(rowSpacing, columnSpacing),
                Dimensions = dimensions,
                RowDirection = rowDirection,
                ColumnDirection = columnDirection,
                Origin = default,
            },
            new VolumeGrid(dimensions, columnSpacing, rowSpacing, sliceThickness));
    }

    /// <summary>Объём без данных: измерения зависят только от геометрии.</summary>
    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => 0;

        public float this[int column, int row, int slice] => 0;
    }
}
