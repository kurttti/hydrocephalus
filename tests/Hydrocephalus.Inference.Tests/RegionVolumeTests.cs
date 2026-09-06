using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Объёмы структур по маске с известным ответом.
///
/// Ошибка здесь не выглядит как сбой: объём получается правдоподобного порядка
/// и неверный. Поэтому фантом задан так, что правильный ответ считается на бумаге.
/// </summary>
public sealed class RegionVolumeTests
{
    private static readonly AnatomicalLabel Ventricles = new("lateral-ventricles");
    private static readonly AnatomicalLabel Brain = new("brain");

    [Fact]
    public void Volume_is_the_voxel_count_times_the_voxel_size()
    {
        // 1000 отсчётов по 1мм³ — это ровно 1мл.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(10, 10, 10), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        var volumes = RegionVolumes.Measure(mask, AcquisitionTier.Extended);

        Assert.Equal(1.0, Single(volumes, Ventricles).Value, precision: 9);
        Assert.Equal(MeasurementUnit.Millilitre, Single(volumes, Ventricles).Unit);
    }

    [Fact]
    public void Anisotropic_voxels_change_the_volume()
    {
        // Тот же счёт отсчётов при шаге 0.5 × 0.5 × 4мм даёт вчетверо иной объём.
        // Считать «по числу вокселей» значило бы ошибиться ровно во столько раз.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(10, 10, 10), 0.5, 0.5, 4.0),
            (column, row, slice) => (byte)1);

        var volume = Single(RegionVolumes.Measure(mask, AcquisitionTier.Extended), Ventricles);

        Assert.Equal(1000 * 0.5 * 0.5 * 4.0 / 1000.0, volume.Value, precision: 9);
    }

    [Fact]
    public void Voxel_size_uses_the_slice_step_and_not_the_slice_thickness()
    {
        // При зазоре шаг больше толщины, и объём по толщине занижен ровно
        // на долю неполученной ткани.
        var grid = new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 3.0);

        Assert.Equal(3.0, RegionVolumes.VoxelVolumeCubicMillimetres(grid), precision: 9);
    }

    [Fact]
    public void Background_is_not_a_structure()
    {
        // Половина отсчётов — фон. Объём фона никому не нужен и в признаки
        // не попадает.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(10, 10, 10), 1.0, 1.0, 1.0),
            (column, row, slice) => slice < 5 ? (byte)1 : LabelMap.BackgroundLabel);

        var volumes = RegionVolumes.Measure(mask, AcquisitionTier.Extended);

        Assert.Equal(2, volumes.Count);
        Assert.Equal(0.5, Single(volumes, Ventricles).Value, precision: 9);
    }

    [Fact]
    public void Every_structure_of_the_map_gets_a_biomarker_even_if_empty()
    {
        // Отсутствующая структура — это ноль, а не пропущенная строка отчёта:
        // иначе «признака нет» неотличимо от «структура не найдена».
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        var volumes = RegionVolumes.Measure(mask, AcquisitionTier.Extended);

        Assert.Equal(2, volumes.Count);
        Assert.Equal(0.0, Single(volumes, Brain).Value, precision: 9);
    }

    [Fact]
    public void A_structure_carries_the_label_it_was_measured_from()
    {
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        var volume = Single(RegionVolumes.Measure(mask, AcquisitionTier.Extended), Ventricles);

        Assert.Equal(Ventricles, Assert.Single(volume.SourceLabels));
    }

    [Fact]
    public void Volumetric_features_are_refused_at_the_baseline_tier()
    {
        // На наборе отдельных срезов с зазором объём структуры недостоверен.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        Assert.Throws<DomainRuleViolationException>(
            () => RegionVolumes.Measure(mask, AcquisitionTier.Baseline));
    }

    [Fact]
    public void A_label_outside_the_map_is_refused()
    {
        // Маска и карта меток из разных версий: числа приписались бы
        // неизвестно каким структурам.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)7);

        Assert.Throws<DomainRuleViolationException>(
            () => RegionVolumes.Measure(mask, AcquisitionTier.Extended));
    }

    [Fact]
    public void A_mask_of_the_wrong_size_is_refused_at_construction()
    {
        // Маска с другой сетки накладывается со смещением и при этом выглядит
        // правдоподобно, поэтому размер проверяется сразу.
        Assert.Throws<DomainRuleViolationException>(() => new VoxelMask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            Map(),
            new byte[10]));
    }

    [Fact]
    public void The_fraction_of_two_volumes_is_a_ratio()
    {
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(10, 10, 10), 1.0, 1.0, 1.0),
            (column, row, slice) => slice < 2 ? (byte)1 : (byte)2);

        var volumes = RegionVolumes.Measure(mask, AcquisitionTier.Extended);

        var fraction = RegionVolumes.Fraction(
            Single(volumes, Ventricles),
            Single(volumes, Brain),
            "ventricle-to-brain",
            AcquisitionTier.Extended);

        Assert.Equal(0.25, fraction.Value, precision: 9);
        Assert.Equal(MeasurementUnit.Ratio, fraction.Unit);
        Assert.Equal(2, fraction.SourceLabels.Count);
    }

    [Fact]
    public void A_fraction_of_something_other_than_volumes_is_refused()
    {
        // Делить миллилитры на градусы можно арифметически и нельзя клинически.
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        var volume = Single(RegionVolumes.Measure(mask, AcquisitionTier.Extended), Ventricles);

        var angle = Biomarker.Create(
            new MeasurementMethod
            {
                Code = "callosal-angle",
                DefinitionVersion = "1.0.0",
                RequiredTier = AcquisitionTier.Baseline,
            },
            AcquisitionTier.Extended,
            90.0,
            MeasurementUnit.Degree,
            MeasurementQuality.Reliable,
            new MeasurementRange(0, 180));

        Assert.Throws<DomainRuleViolationException>(
            () => RegionVolumes.Fraction(volume, angle, "nonsense", AcquisitionTier.Extended));
    }

    [Fact]
    public void An_empty_denominator_is_refused()
    {
        var mask = Mask(
            new VolumeGrid(new VolumeDimensions(4, 4, 4), 1.0, 1.0, 1.0),
            (column, row, slice) => (byte)1);

        var volumes = RegionVolumes.Measure(mask, AcquisitionTier.Extended);

        Assert.Throws<DomainRuleViolationException>(() => RegionVolumes.Fraction(
            Single(volumes, Ventricles),
            Single(volumes, Brain),
            "ventricle-to-brain",
            AcquisitionTier.Extended));
    }

    private static Biomarker Single(IReadOnlyList<Biomarker> volumes, AnatomicalLabel structure) =>
        volumes.Single(item => item.Method.Code == "volume." + structure.Code);

    private static LabelMap Map() => new()
    {
        Version = "1.0.0",
        Labels = [Ventricles, Brain],
    };

    private static VoxelMask Mask(VolumeGrid grid, Func<int, int, int, byte> label)
    {
        var dimensions = grid.Dimensions;
        var labels = new byte[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    labels[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column] =
                        label(column, row, slice);
                }
            }
        }

        return new VoxelMask(grid, Map(), labels);
    }
}
