using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Приведение снимка к виду, который принимает обученная модель.
///
/// Настоящая проверка — сверка с выводом FastSurfer на клинических сериях: на
/// аксиальной совпало 100,00 % отсчётов, на сагиттальной 99,99 % точно и 100 % в
/// пределах единицы (docs/data/README.md). Те файлы в репозиторий не входят, они
/// получены из снимков пациентов. Здесь проверяется то, что проверяемо на
/// синтетике: размер куба, укладка и устойчивость шкалы к выбросу.
/// </summary>
public sealed class VolumeConformingTests
{
    private const int Size = 32;

    [Fact]
    public void The_cube_has_the_size_the_model_expects()
    {
        var conformed = VolumeConforming.Conform(Head());

        Assert.Equal(VolumeConforming.Size * VolumeConforming.Size * VolumeConforming.Size, conformed.Length);
    }

    [Fact]
    public void The_axes_come_out_left_inferior_anterior()
    {
        // Метка ставится справа от пациента и сверху. В укладке LIA первая ось
        // идёт влево, вторая вниз: значит метка обязана оказаться в начале
        // первой оси и в начале второй.
        var volume = Head(markRight: true, markSuperior: true);
        var conformed = VolumeConforming.Conform(volume);
        var (first, second) = MarkCentre(conformed);

        Assert.True(first < VolumeConforming.Size / 2, $"метка не слева по первой оси: {first}");
        Assert.True(second < VolumeConforming.Size / 2, $"метка не сверху по второй оси: {second}");
    }

    [Fact]
    public void A_single_bright_outlier_does_not_squash_the_anatomy()
    {
        // Шкала устойчивая, а не по размаху: блик или металл иначе сжали бы всю
        // анатомию в нижние единицы.
        var plain = VolumeConforming.Conform(Head());
        var withOutlier = VolumeConforming.Conform(Head(outlier: 100_000f));

        var plainMean = plain.Where(value => value > 0).Average(value => (double)value);
        var outlierMean = withOutlier.Where(value => value > 0).Average(value => (double)value);

        Assert.InRange(outlierMean, plainMean * 0.9, plainMean * 1.1);
    }

    private static (int First, int Second) MarkCentre(byte[] conformed)
    {
        long firstSum = 0, secondSum = 0, count = 0;

        for (var k = 0; k < VolumeConforming.Size; k++)
        {
            for (var j = 0; j < VolumeConforming.Size; j++)
            {
                for (var i = 0; i < VolumeConforming.Size; i++)
                {
                    if (conformed[(((k * VolumeConforming.Size) + j) * VolumeConforming.Size) + i] < 250)
                    {
                        continue;
                    }

                    firstSum += i;
                    secondSum += j;
                    count++;
                }
            }
        }

        Assert.True(count > 0, "метка не нашлась в приведённом объёме");

        return ((int)(firstSum / count), (int)(secondSum / count));
    }

    /// <summary>
    /// Синтетическая «голова»: шар с необязательной яркой меткой.
    ///
    /// Направления заданы как у обычной аксиальной серии DICOM: строка идёт
    /// налево от пациента, столбец — назад.
    /// </summary>
    private static TestVolume Head(bool markRight = false, bool markSuperior = false, float outlier = 0f)
    {
        var centre = Size / 2.0;

        return new TestVolume((column, row, slice) =>
        {
            if (outlier > 0 && column == 0 && row == 0 && slice == 0)
            {
                return outlier;
            }

            var dx = column - centre;
            var dy = row - centre;
            var dz = slice - centre;
            var inside = (dx * dx) + (dy * dy) + (dz * dz) <= 12 * 12;

            if (!inside)
            {
                return 0f;
            }

            // Метка: справа от пациента — это малые номера столбца, потому что
            // строка идёт налево. Сверху — большие номера среза.
            var right = !markRight || column < centre - 6;
            var superior = !markSuperior || slice > centre + 6;

            return markRight || markSuperior ? (right && superior ? 1000f : 300f) : 300f;
        });
    }

    private sealed class TestVolume : IVoxelVolume
    {
        private readonly float[] voxels;

        public TestVolume(Func<int, int, int, float> value)
        {
            this.Grid = new VolumeGrid
            {
                Dimensions = new VolumeDimensions(Size, Size, Size),
                ColumnSpacingMillimetres = 2.0,
                RowSpacingMillimetres = 2.0,
                SliceSpacingMillimetres = 2.0,
            };

            this.Geometry = new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.ThreeDimensional,
                SliceThicknessMillimetres = 2.0,
                SliceSpacingMillimetres = 2.0,
                PixelSpacing = new InPlaneSpacing(2.0, 2.0),
                Dimensions = this.Grid.Dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = new SpatialVector(-Size, -Size, -Size),
            };

            this.voxels = new float[Size * Size * Size];

            var minimum = float.PositiveInfinity;
            var maximum = float.NegativeInfinity;

            for (var slice = 0; slice < Size; slice++)
            {
                for (var row = 0; row < Size; row++)
                {
                    for (var column = 0; column < Size; column++)
                    {
                        var sample = value(column, row, slice);

                        this.voxels[(((slice * Size) + row) * Size) + column] = sample;
                        minimum = Math.Min(minimum, sample);
                        maximum = Math.Max(maximum, sample);
                    }
                }
            }

            this.Minimum = minimum;
            this.Maximum = maximum;
        }

        public VolumeGrid Grid { get; }

        public SeriesGeometry Geometry { get; }

        public float Minimum { get; }

        public float Maximum { get; }

        public float this[int column, int row, int slice] =>
            this.voxels[(((slice * Size) + row) * Size) + column];
    }
}
