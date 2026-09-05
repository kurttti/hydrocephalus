using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Извлечение плоскостей из объёма.
///
/// Проверяется соответствие пикселя вокселю: перепутанные оси дают картинку,
/// которая выглядит как анатомия и при этом показывает не тот срез. Отдельно
/// проверяется, что анатомическое имя вида берётся из направляющих косинусов,
/// а не назначается по номеру оси.
/// </summary>
public sealed class VolumeSlicerTests : IDisposable
{
    private const int Columns = 4;
    private const int Rows = 3;
    private const int Slices = 5;

    // Окно шире диапазона значений: яркость здесь не проверяется, важно
    // соответствие координат.
    private static readonly WindowLevel Wide = new(Center: 25000, Width: 60000);

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Acquired_plane_shows_the_slice_it_is_asked_for()
    {
        var volume = await this.LoadAsync();

        var plane = VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, index: 2, Wide);

        Assert.Equal(Columns, plane.Width);
        Assert.Equal(Rows, plane.Height);

        AssertMatchesVoxels(plane, volume, (x, y) => (x, y, 2));
    }

    [Fact]
    public async Task Reformat_across_rows_walks_columns_and_slices()
    {
        var volume = await this.LoadAsync();

        var plane = VolumeSlicer.Extract(volume, VolumeAxis.AcrossRows, index: 1, Wide);

        Assert.Equal(Columns, plane.Width);
        Assert.Equal(Slices, plane.Height);

        AssertMatchesVoxels(plane, volume, (x, y) => (x, 1, y));
    }

    [Fact]
    public async Task Reformat_across_columns_walks_rows_and_slices()
    {
        var volume = await this.LoadAsync();

        var plane = VolumeSlicer.Extract(volume, VolumeAxis.AcrossColumns, index: 3, Wide);

        Assert.Equal(Rows, plane.Width);
        Assert.Equal(Slices, plane.Height);

        AssertMatchesVoxels(plane, volume, (x, y) => (3, x, y));
    }

    [Fact]
    public async Task Each_axis_reports_how_many_planes_it_has()
    {
        var volume = await this.LoadAsync();

        Assert.Equal(Slices, VolumeSlicer.CountAlong(volume, VolumeAxis.AcrossSlices));
        Assert.Equal(Rows, VolumeSlicer.CountAlong(volume, VolumeAxis.AcrossRows));
        Assert.Equal(Columns, VolumeSlicer.CountAlong(volume, VolumeAxis.AcrossColumns));
    }

    [Fact]
    public async Task Asking_past_the_last_plane_is_rejected()
    {
        var volume = await this.LoadAsync();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, Slices, Wide));
    }

    [Fact]
    public async Task Views_of_an_axial_series_are_named_from_its_cosines()
    {
        // Серия получена аксиально: вид поперёк срезов аксиальный, а два реформата —
        // корональный и сагиттальный. Имена выводятся из геометрии, поэтому
        // на серии другой ориентации они будут другими.
        var volume = await this.LoadAsync();

        Assert.Equal(
            ImagingPlane.Axial,
            VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, 0, Wide).Plane);

        Assert.Equal(
            ImagingPlane.Coronal,
            VolumeSlicer.Extract(volume, VolumeAxis.AcrossRows, 0, Wide).Plane);

        Assert.Equal(
            ImagingPlane.Sagittal,
            VolumeSlicer.Extract(volume, VolumeAxis.AcrossColumns, 0, Wide).Plane);
    }

    [Fact]
    public async Task Side_labels_of_the_acquired_plane_follow_the_patient_frame()
    {
        var volume = await this.LoadAsync();

        var labels = VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, 0, Wide).Labels;

        Assert.Equal(AnatomicalDirection.Left, labels.Right);
        Assert.Equal(AnatomicalDirection.Right, labels.Left);
        Assert.Equal(AnatomicalDirection.Posterior, labels.Bottom);
        Assert.Equal(AnatomicalDirection.Anterior, labels.Top);
    }

    [Fact]
    public async Task Reformats_carry_the_slice_step_as_their_pixel_height()
    {
        // Объём анизотропен: у реформата высота пикселя равна шагу между срезами,
        // и вывод «пиксель в пиксель» растянул бы анатомию в пять раз.
        var volume = await this.LoadAsync(sliceStepMillimetres: 5.0m);

        var acquired = VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, 0, Wide);
        var reformat = VolumeSlicer.Extract(volume, VolumeAxis.AcrossRows, 0, Wide);

        Assert.Equal(1.0, acquired.PixelHeightMillimetres, precision: 6);
        Assert.Equal(5.0, reformat.PixelHeightMillimetres, precision: 6);
        Assert.Equal(1.0, reformat.PixelWidthMillimetres, precision: 6);
    }

    [Fact]
    public async Task Window_is_applied_to_the_extracted_plane()
    {
        var volume = await this.LoadAsync();

        // Узкое окно вокруг известного значения: соседние воксели отличаются
        // на 1 и обязаны попасть в разные концы шкалы.
        var narrow = new WindowLevel(Center: volume[1, 0, 0], Width: 2);

        var plane = VolumeSlicer.Extract(volume, VolumeAxis.AcrossSlices, 0, narrow);

        Assert.Equal(0, plane.Pixels[0]);
        Assert.Equal(255, plane.Pixels[2]);
    }

    private static void AssertMatchesVoxels(
        PlaneImage plane,
        VoxelVolume volume,
        Func<int, int, (int Column, int Row, int Slice)> locate)
    {
        for (var y = 0; y < plane.Height; y++)
        {
            for (var x = 0; x < plane.Width; x++)
            {
                var (column, row, slice) = locate(x, y);

                Assert.Equal(
                    Wide.Map(volume[column, row, slice]),
                    plane.Pixels[(y * plane.Width) + x]);
            }
        }
    }

    private async Task<VoxelVolume> LoadAsync(decimal sliceStepMillimetres = 1.0m)
    {
        for (var slice = 0; slice < Slices; slice++)
        {
            SyntheticVolume.WriteSlice(
                Path.Combine(this.root.FullName, $"{slice:D2}.dcm"),
                Columns,
                Rows,
                positionMillimetres: slice * sliceStepMillimetres,
                SyntheticVolume.AsymmetricSlice(Columns, Rows, slice),
                customize: dataset => dataset.AddOrUpdate(DicomTag.SliceThickness, sliceStepMillimetres));
        }

        return await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);
    }
}
