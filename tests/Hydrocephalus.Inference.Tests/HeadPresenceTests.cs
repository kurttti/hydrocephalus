using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Проверка «голова ли это» по размеру анатомии.
///
/// Нужна потому, что теги об этом лгут: найденная в выборке абдоминальная МРТ
/// объявляет себя `TEMP^HEAD` при пустой области съёмки (docs/data/README.md).
/// </summary>
public sealed class HeadPresenceTests
{
    private const int Size = 64;

    [Fact]
    public void A_head_sized_object_is_accepted()
    {
        // Шар поперёк 180 мм при шаге 3 мм — обычная голова.
        var volume = Sphere(spacingMillimetres: 3.0, radiusVoxels: 30);

        var (extent, looksLikeHead) = HeadPresence.Measure(volume);

        Assert.True(looksLikeHead);
        Assert.InRange(extent, 170, 190);
    }

    [Fact]
    public void An_object_wider_than_a_head_is_refused()
    {
        // Тот же шар при шаге 6 мм — 360 мм поперёк, головой быть не может.
        var volume = Sphere(spacingMillimetres: 6.0, radiusVoxels: 30);

        var (extent, looksLikeHead) = HeadPresence.Measure(volume);

        Assert.False(looksLikeHead);
        Assert.InRange(extent, 350, 370);
    }

    [Fact]
    public void A_uniform_frame_is_not_called_a_foreign_body_part()
    {
        // Однородный кадр — изъян, но не этой проверки: порога между воздухом
        // и тканью в нём нет, и судить не о чем. Прежде здесь падал расчёт
        // порога, и вместе с ним весь контроль качества.
        var volume = new TestVolume(Grid(1.0), (_, _, _) => 100f);

        var (extent, looksLikeHead) = HeadPresence.Measure(volume);

        Assert.True(looksLikeHead);
        Assert.Equal(0.0, extent);
    }

    private static TestVolume Sphere(double spacingMillimetres, int radiusVoxels)
    {
        var centre = Size / 2.0;

        return new TestVolume(Grid(spacingMillimetres), (column, row, slice) =>
        {
            var dx = column - centre;
            var dy = row - centre;
            var dz = slice - centre;

            return (dx * dx) + (dy * dy) + (dz * dz) <= radiusVoxels * radiusVoxels ? 900f : 10f;
        });
    }

    private static VolumeGrid Grid(double spacingMillimetres) => new()
    {
        Dimensions = new VolumeDimensions(Size, Size, Size),
        ColumnSpacingMillimetres = spacingMillimetres,
        RowSpacingMillimetres = spacingMillimetres,
        SliceSpacingMillimetres = spacingMillimetres,
    };

    private sealed class TestVolume : IVoxelVolume
    {
        private readonly float[] voxels;

        public TestVolume(VolumeGrid grid, Func<int, int, int, float> value)
        {
            this.Grid = grid;
            this.Geometry = new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.ThreeDimensional,
                SliceThicknessMillimetres = grid.SliceSpacingMillimetres,
                SliceSpacingMillimetres = grid.SliceSpacingMillimetres,
                PixelSpacing = new InPlaneSpacing(
                    grid.RowSpacingMillimetres, grid.ColumnSpacingMillimetres),
                Dimensions = grid.Dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
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

        public float Minimum { get; }

        public float Maximum { get; }

        public VolumeGrid Grid { get; }

        public SeriesGeometry Geometry { get; }

        public float this[int column, int row, int slice] =>
            this.voxels[(((slice * Size) + row) * Size) + column];
    }
}
