using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Сборка объёма из срезов.
///
/// Ошибки этого этапа не выглядят как сбой: перепутанные оси или обратный порядок
/// срезов дают правдоподобную картинку и зеркальные измерения. Поэтому образец
/// несимметричен по всем трём осям — значение вокселя однозначно говорит,
/// откуда он взят.
/// </summary>
public sealed class DicomVolumeReaderTests : IDisposable
{
    private const int Columns = 4;
    private const int Rows = 3;
    private const int Slices = 5;

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Voxels_land_where_the_slices_put_them()
    {
        this.WriteVolume();

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        for (var slice = 0; slice < Slices; slice++)
        {
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    Assert.Equal(
                        (slice * 10000) + (row * 100) + column,
                        volume[column, row, slice]);
                }
            }
        }
    }

    [Fact]
    public async Task Dimensions_are_columns_rows_and_slice_count()
    {
        this.WriteVolume();

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(Columns, volume.Geometry.Dimensions.Columns);
        Assert.Equal(Rows, volume.Geometry.Dimensions.Rows);
        Assert.Equal(Slices, volume.Geometry.Dimensions.Slices);
    }

    [Fact]
    public async Task Slice_order_comes_from_position_and_not_from_the_file_name()
    {
        // Имена файлов в рабочей копии — псевдонимы и лексикографически произвольны.
        // Здесь порядок имён обратен пространственному: собранный по именам объём
        // оказался бы перевёрнутым и выглядел бы при этом нормально.
        for (var slice = 0; slice < Slices; slice++)
        {
            SyntheticVolume.WriteSlice(
                Path.Combine(this.root.FullName, $"{Slices - slice:D2}.dcm"),
                Columns,
                Rows,
                positionMillimetres: slice,
                SyntheticVolume.AsymmetricSlice(Columns, Rows, slice));
        }

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(0, volume[0, 0, 0]);
        Assert.Equal((Slices - 1) * 10000, volume[0, 0, Slices - 1]);
    }

    [Fact]
    public async Task Rescale_is_applied_per_slice()
    {
        // У части производителей наклон и сдвиг меняются от среза к срезу.
        // Один коэффициент на серию дал бы ступеньку яркости посреди объёма.
        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            SyntheticVolume.AsymmetricSlice(Columns, Rows, 0),
            rescaleSlope: 2.0m,
            rescaleIntercept: 100.0m);

        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "b.dcm"),
            Columns,
            Rows,
            positionMillimetres: 1,
            SyntheticVolume.AsymmetricSlice(Columns, Rows, 0),
            rescaleSlope: 1.0m,
            rescaleIntercept: 0.0m);

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(102, volume[1, 0, 0]);
        Assert.Equal(1, volume[1, 0, 1]);
    }

    [Fact]
    public async Task Signed_pixels_keep_their_sign()
    {
        // Прочитанное без учёта PixelRepresentation отрицательное значение
        // превращается в очень большое положительное.
        var values = new short[Columns * Rows];
        values[0] = -500;

        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            values,
            signed: true);

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(-500, volume[0, 0, 0]);
        Assert.Equal(-500, volume.Minimum);
    }

    [Fact]
    public async Task Spacing_is_taken_from_the_slice_positions()
    {
        for (var slice = 0; slice < Slices; slice++)
        {
            SyntheticVolume.WriteSlice(
                Path.Combine(this.root.FullName, $"{slice:D2}.dcm"),
                Columns,
                Rows,
                positionMillimetres: slice * 3.0m,
                SyntheticVolume.AsymmetricSlice(Columns, Rows, slice));
        }

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(3.0, volume.Geometry.SliceSpacingMillimetres, precision: 6);
        Assert.True(volume.Geometry.HasSliceGap);
    }

    [Fact]
    public async Task Slices_of_different_size_are_refused()
    {
        // Под одним идентификатором серии лежит больше одного набора:
        // собрать из них объём нельзя, а «собрать что получится» — молчаливая порча.
        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            Columns,
            Rows,
            positionMillimetres: 0,
            SyntheticVolume.AsymmetricSlice(Columns, Rows, 0));

        SyntheticVolume.WriteSlice(
            Path.Combine(this.root.FullName, "b.dcm"),
            Columns + 1,
            Rows,
            positionMillimetres: 1,
            SyntheticVolume.AsymmetricSlice(Columns + 1, Rows, 1));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_directory_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None));
    }

    [Fact]
    public async Task Value_range_covers_the_whole_volume()
    {
        this.WriteVolume();

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(0, volume.Minimum);
        Assert.Equal(((Slices - 1) * 10000) + ((Rows - 1) * 100) + Columns - 1, volume.Maximum);
    }

    [Fact]
    public async Task Reading_outside_the_volume_is_rejected()
    {
        this.WriteVolume();

        var volume = await DicomVolumeReader.LoadAsync(this.root.FullName, CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => volume[Columns, 0, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => volume[0, Rows, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => volume[0, 0, Slices]);
    }

    private void WriteVolume()
    {
        for (var slice = 0; slice < Slices; slice++)
        {
            SyntheticVolume.WriteSlice(
                Path.Combine(this.root.FullName, $"{slice:D2}.dcm"),
                Columns,
                Rows,
                positionMillimetres: slice,
                SyntheticVolume.AsymmetricSlice(Columns, Rows, slice));
        }
    }
}
