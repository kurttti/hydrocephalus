using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Индекс Эванса, выведенный из маски, на фантоме с известным ответом.
///
/// Фантом записан как сагиттальная 3D-серия: столбцы идут назад, строки вниз,
/// срезы вправо. Так записаны объёмные T1 и в IXI, и в клинической выборке,
/// и именно здесь аксиальная плоскость лежит не поперёк срезов. Голова —
/// прямоугольный блок: кожа, кость, ликвор, мозг. Ответ считается на бумаге:
/// мозг шириной 64 мм, тёмная полоса по 4 мм с каждой стороны — внутренний
/// диаметр 68 мм; рога от 38-го до 61-го отсчёта — 24 мм.
/// </summary>
public sealed class AutomaticEvansIndexTests
{
    private const int Width = 100;
    private const int Length = 120;
    private const int Height = 60;

    private const float Background = 0;
    private const float Scalp = 900;
    private const float Bone = 40;
    private const float Csf = 100;
    private const float Brain = 600;

    [Fact]
    public void The_index_is_derived_from_the_mask_on_an_axial_plane_of_a_sagittal_volume()
    {
        var (volume, segmentation) = Phantom(Ventricles.Both);

        var result = AutomaticEvansIndex.Measure(volume, segmentation, SeriesWeighting.T1);

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.NotNull(result.Segments);
        Assert.Equal(24.0 / 68.0, result.Biomarker.Value, precision: 6);
        Assert.Equal(VolumeAxis.AcrossRows, result.Segments.AxialAcross);

        // Метод не валидирован против ручной разметки и не выдаёт себя за надёжный.
        Assert.Equal(MeasurementQuality.Questionable, result.Biomarker.Quality);
    }

    [Fact]
    public void A_mask_holding_one_lateral_ventricle_gives_no_index()
    {
        // Ширина одного рога дала бы индекс около 0.15 — правдоподобное число
        // из неверной анатомии. На IXI сегментация так брала один желудочек
        // дважды на сорок серий.
        var (volume, segmentation) = Phantom(Ventricles.LeftOnly);

        var result = AutomaticEvansIndex.Measure(volume, segmentation, SeriesWeighting.T1);

        Assert.Equal(AutomaticEvansRefusal.FrontalHornsNotFound, result.Refusal);
        Assert.Null(result.Biomarker);
    }

    [Fact]
    public void A_lateral_fragment_does_not_widen_the_horns()
    {
        // Область, не подходящая к средней линии, — височный рог или посторонний
        // фрагмент, а не передний рог.
        var (volume, segmentation) = Phantom(Ventricles.Both | Ventricles.LateralFragment);

        var result = AutomaticEvansIndex.Measure(volume, segmentation, SeriesWeighting.T1);

        Assert.NotNull(result.Biomarker);
        Assert.Equal(24.0 / 68.0, result.Biomarker.Value, precision: 6);
    }

    [Fact]
    public void A_refused_segmentation_gives_no_index()
    {
        var (volume, segmentation) = Phantom(Ventricles.Both);

        var result = AutomaticEvansIndex.Measure(
            volume,
            segmentation with { Quality = MeasurementQuality.Unreliable },
            SeriesWeighting.T1);

        Assert.Equal(AutomaticEvansRefusal.SegmentationRefused, result.Refusal);
    }

    [Fact]
    public void A_bright_csf_weighting_is_not_measured()
    {
        // На T2 ликвор яркий, и тёмной полосы «ликвор и кость» нет.
        var (volume, segmentation) = Phantom(Ventricles.Both);

        var result = AutomaticEvansIndex.Measure(volume, segmentation, SeriesWeighting.T2);

        Assert.Equal(AutomaticEvansRefusal.WeightingNotSupported, result.Refusal);
    }

    [Fact]
    public void A_grid_turned_far_from_the_patient_axes_is_refused()
    {
        // Строка под 45° между «назад» и «вверх»: аксиальной плоскости
        // поперёк оси сетки здесь нет.
        var (volume, segmentation) = Phantom(Ventricles.Both, oblique: true);

        var result = AutomaticEvansIndex.Measure(volume, segmentation, SeriesWeighting.T1);

        Assert.Equal(AutomaticEvansRefusal.AxesNotAligned, result.Refusal);
    }

    private static (TestVolume Volume, BaselineSegmentationResult Segmentation) Phantom(
        Ventricles ventricles,
        bool oblique = false)
    {
        // Координаты пациента в отсчётах: x — слева направо, y — спереди назад,
        // z — сверху вниз. Сетка: столбец — y, строка — z, срез — x.
        var dimensions = new VolumeDimensions(Length, Height, Width);
        var labels = new byte[Length * Height * Width];
        var voxels = new float[labels.Length];

        for (var x = 0; x < Width; x++)
        {
            for (var z = 0; z < Height; z++)
            {
                for (var y = 0; y < Length; y++)
                {
                    var offset = (((x * Height) + z) * Length) + y;
                    var ventricle = IsVentricle(ventricles, x, y, z);

                    labels[offset] = ventricle ? (byte)1 : (byte)0;
                    voxels[offset] = ventricle ? Csf : Head(x, y, z);
                }
            }
        }

        var grid = new VolumeGrid(dimensions, 1, 1, 1);
        var mask = new VoxelMask(
            grid,
            new LabelMap
            {
                Version = BaselineVentricleSegmentation.LabelMapVersion,
                Labels = [BaselineVentricleSegmentation.VentricularSystem],
            },
            labels);

        var rowDirection = oblique
            ? new SpatialVector(0, Math.Sqrt(0.5), Math.Sqrt(0.5))
            : new SpatialVector(0, 1, 0);

        var columnDirection = oblique
            ? new SpatialVector(0, Math.Sqrt(0.5), -Math.Sqrt(0.5))
            : new SpatialVector(0, 0, -1);

        var geometry = new SeriesGeometry
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = 1,
            SliceSpacingMillimetres = 1,
            PixelSpacing = new InPlaneSpacing(1, 1),
            Dimensions = dimensions,
            RowDirection = rowDirection,
            ColumnDirection = columnDirection,
            Origin = default,
        };

        return (new TestVolume(geometry, grid, voxels), new BaselineSegmentationResult(mask, [], MeasurementQuality.Questionable));
    }

    private static float Head(int x, int y, int z)
    {
        // Блок головы: снаружи внутрь кожа 4, кость 3, ликвор 1 — по бокам и спереди-сзади.
        if (x is < 10 or > 89 || y is < 10 or > 109 || z is < 5 or > 54)
        {
            return Background;
        }

        var depth = Math.Min(Math.Min(x - 10, 89 - x), Math.Min(y - 10, 109 - y));

        return depth switch
        {
            < 4 => Scalp,
            < 7 => Bone,
            < 8 => Csf,
            _ => Brain,
        };
    }

    private static bool IsVentricle(Ventricles ventricles, int x, int y, int z)
    {
        if (z is < 25 or > 35)
        {
            return false;
        }

        var leftHorn = x is >= 38 and <= 47 && y is >= 30 and <= 50;
        var rightHorn = x is >= 52 and <= 61 && y is >= 30 and <= 50;
        var body = y is >= 51 and <= 90;

        if (ventricles.HasFlag(Ventricles.LateralFragment) && x is >= 20 and <= 25 && y is >= 32 and <= 40)
        {
            return true;
        }

        if (ventricles.HasFlag(Ventricles.LeftOnly))
        {
            return (leftHorn || (body && x is >= 38 and <= 47)) && x < 49;
        }

        return leftHorn || rightHorn || (body && x is >= 44 and <= 55);
    }

    [Flags]
    private enum Ventricles
    {
        Both = 1,
        LeftOnly = 2,
        LateralFragment = 4,
    }

    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid, float[] voxels) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => Background;

        public float Maximum => Scalp;

        public float this[int column, int row, int slice] =>
            voxels[(((slice * this.Grid.Dimensions.Rows) + row) * this.Grid.Dimensions.Columns) + column];
    }
}
