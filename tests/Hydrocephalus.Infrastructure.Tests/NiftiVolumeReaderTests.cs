using System.Buffers.Binary;
using System.IO.Compression;
using Hydrocephalus.Infrastructure.Nifti;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Чтение воксельных данных NIfTI.
///
/// Нужно для проверки сегментации на публичных наборах с эталонной разметкой.
/// Главное здесь — порядок индексов и масштаб: переставленные оси дают объём
/// того же размера и правдоподобного вида, а неприменённый scl_slope — те же
/// контрасты в других единицах. Ни то, ни другое не видно по картинке.
/// </summary>
public sealed class NiftiVolumeReaderTests : IDisposable
{
    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Voxels_are_indexed_column_row_slice_like_the_dicom_volume()
    {
        // Каждый воксель хранит свой адрес: 100*i + 10*j + k.
        var volume = NiftiVolumeReader.Parse(Build(4, 3, 2, (i, j, k) => (100 * i) + (10 * j) + k));

        Assert.Equal(321, volume[3, 2, 1]);
        Assert.Equal(10, volume[0, 1, 0]);
        Assert.Equal(1, volume[0, 0, 1]);
    }

    [Fact]
    public void The_scale_from_the_header_is_applied()
    {
        var volume = NiftiVolumeReader.Parse(Build(2, 2, 2, (_, _, _) => 10, slope: 2.5f, intercept: -1));

        Assert.Equal(24f, volume[1, 1, 1]);
    }

    [Fact]
    public void A_zero_slope_means_no_scale_rather_than_an_empty_volume()
    {
        // По спецификации нулевой наклон — «масштаба нет». Буквальное прочтение
        // обнулило бы весь объём, и сегментация честно не нашла бы ничего.
        var volume = NiftiVolumeReader.Parse(Build(2, 2, 2, (_, _, _) => 7, slope: 0f, intercept: 5));

        Assert.Equal(7f, volume[0, 0, 0]);
    }

    [Fact]
    public void Voxel_size_reaches_the_grid()
    {
        var volume = NiftiVolumeReader.Parse(Build(2, 2, 2, (_, _, _) => 0, voxelX: 0.94f, voxelY: 0.94f, voxelZ: 1.2f));

        Assert.Equal(0.94, volume.Grid.ColumnSpacingMillimetres, 3);
        Assert.Equal(1.2, volume.Grid.SliceSpacingMillimetres, 3);
    }

    [Fact]
    public void A_truncated_file_is_refused_rather_than_padded()
    {
        // Недокачанный архив — обычное дело. Дополнить нулями значило бы подать
        // сегментации голову без половины.
        var content = Build(4, 4, 4, (_, _, _) => 1);

        Assert.Throws<InvalidDataException>(() => NiftiVolumeReader.Parse(content.AsSpan(0, content.Length - 10)));
    }

    [Fact]
    public async Task A_gzipped_file_reads_the_same_as_a_plain_one()
    {
        var content = Build(3, 3, 3, (i, j, k) => i + j + k);
        var path = Path.Combine(this.root.FullName, "volume.nii.gz");

        await using (var file = File.Create(path))
        await using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        {
            await zip.WriteAsync(content);
        }

        var volume = await NiftiVolumeReader.LoadAsync(path, CancellationToken.None);

        Assert.Equal(6f, volume[2, 2, 2]);
    }

    private static byte[] Build(
        short columns,
        short rows,
        short slices,
        Func<int, int, int, int> value,
        float slope = 1f,
        float intercept = 0f,
        float voxelX = 1f,
        float voxelY = 1f,
        float voxelZ = 1f)
    {
        var header = SyntheticDicom.BuildNiftiHeader(columns, rows, slices, voxelX, voxelY, voxelZ);

        // datatype int16, bitpix 16, vox_offset 352, наклон и сдвиг.
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(70, 2), 4);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(72, 2), 16);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(108, 4), 352f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(112, 4), slope);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(116, 4), intercept);

        var content = new byte[header.Length + (columns * rows * slices * 2)];
        header.CopyTo(content, 0);

        for (var k = 0; k < slices; k++)
        {
            for (var j = 0; j < rows; j++)
            {
                for (var i = 0; i < columns; i++)
                {
                    var index = (((k * rows) + j) * columns) + i;
                    BinaryPrimitives.WriteInt16LittleEndian(
                        content.AsSpan(header.Length + (index * 2), 2),
                        (short)value(i, j, k));
                }
            }
        }

        return content;
    }
}
