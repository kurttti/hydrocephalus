using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Совпадение наложенной маски со срезом изображения — пункт плана проверки ADR 0007.
///
/// Совпадение обеспечено общей адресацией плоскости, а не согласованностью двух
/// реализаций. Тест это фиксирует: если кто-то заведёт вторую адресацию, маска
/// поедет относительно изображения, и на картинке это будет выглядеть как
/// неточная сегментация, а не как дефект кода.
/// </summary>
public sealed class OverlayCorrespondenceTests
{
    // Окно подобрано так, что три значения фантома дают три различные яркости:
    // по яркости однозначно восстанавливается метка, и совпадение проверяется
    // именно попиксельно, а не «на глаз».
    private static readonly WindowLevel Window = new(Center: 100, Width: 200);

    [Theory]
    [InlineData(VolumeAxis.AcrossSlices)]
    [InlineData(VolumeAxis.AcrossRows)]
    [InlineData(VolumeAxis.AcrossColumns)]
    public void Mask_plane_matches_the_image_plane_pixel_for_pixel(VolumeAxis axis)
    {
        var grid = new VolumeGrid(new VolumeDimensions(7, 5, 3), 1.0, 2.0, 3.0);
        var volume = new TestVolume(Geometry(grid), grid);
        var mask = Mask(grid);

        Assert.True(mask.Fits(volume));

        var count = VolumeSlicer.CountAlong(volume, axis);

        for (var index = 0; index < count; index++)
        {
            var image = VolumeSlicer.Extract(volume, axis, index, Window);
            var overlay = mask.ExtractPlane(axis, index);

            Assert.Equal(image.Width * image.Height, overlay.Length);

            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var offset = (y * image.Width) + x;

                    // Значение вокселя закодировано так, что метка выводится
                    // из него: расхождение адресации сразу нарушает это равенство.
                    Assert.Equal(ExpectedLabel(image.Pixels[offset]), overlay[offset]);
                }
            }
        }
    }

    [Fact]
    public void A_mask_from_another_grid_does_not_fit()
    {
        var grid = new VolumeGrid(new VolumeDimensions(7, 5, 3), 1.0, 2.0, 3.0);
        var volume = new TestVolume(Geometry(grid), grid);

        var otherGrid = grid with { SliceSpacingMillimetres = 1.0 };

        Assert.False(Mask(otherGrid).Fits(volume));
    }

    /// <summary>Метка, ожидаемая по яркости среза.</summary>
    private static byte ExpectedLabel(byte brightness) => brightness switch
    {
        0 => 0,
        255 => 2,
        _ => 1,
    };

    /// <summary>
    /// Значение отсчёта, из которого выводится его метка. Три уровня, чтобы
    /// сдвиг или перестановка адресации нарушили равенство почти везде.
    /// </summary>
    private static float Encode(int ordinal) => LabelOf(ordinal) * 100;

    private static byte LabelOf(int ordinal) => (byte)(ordinal % 3);

    private static SeriesGeometry Geometry(VolumeGrid grid) => new()
    {
        AcquisitionType = MrAcquisitionType.ThreeDimensional,
        SliceThicknessMillimetres = grid.SliceSpacingMillimetres,
        SliceSpacingMillimetres = grid.SliceSpacingMillimetres,
        PixelSpacing = new InPlaneSpacing(grid.RowSpacingMillimetres, grid.ColumnSpacingMillimetres),
        Dimensions = grid.Dimensions,
        RowDirection = new SpatialVector(1, 0, 0),
        ColumnDirection = new SpatialVector(0, 1, 0),
        Origin = default,
    };

    private static VoxelMask Mask(VolumeGrid grid)
    {
        var dimensions = grid.Dimensions;
        var labels = new byte[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var index = 0; index < labels.Length; index++)
        {
            labels[index] = LabelOf(index);
        }

        return new VoxelMask(
            grid,
            new LabelMap
            {
                Version = "1.0.0",
                Labels = [new AnatomicalLabel("first"), new AnatomicalLabel("second")],
            },
            labels);
    }

    /// <summary>
    /// Фантом, в котором значение отсчёта однозначно определяет его метку.
    /// </summary>
    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => 200;

        public float this[int column, int row, int slice] => Encode(
            (((slice * this.Grid.Dimensions.Rows) + row) * this.Grid.Dimensions.Columns) + column);
    }
}
