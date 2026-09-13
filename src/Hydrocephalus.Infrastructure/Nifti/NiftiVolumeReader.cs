using System.Buffers.Binary;
using System.IO.Compression;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Nifti;

/// <summary>
/// Чтение воксельных данных NIfTI-1 (.nii и .nii.gz).
///
/// Нужен для проверки сегментации на публичных наборах с эталонной разметкой
/// (docs/data/README.md): они распространяются в NIfTI, а не в DICOM.
/// В клинический путь приложения этот читатель не входит — NIfTI допускается
/// только в исследовательском режиме (docs/clinical/README.md).
///
/// Порядок индексов совпадает с DICOM-объёмом: столбец — i, строка — j,
/// срез — k. Поэтому сегментация получает объём того же вида и не знает,
/// из какого формата он пришёл.
/// </summary>
public static class NiftiVolumeReader
{
    // Коды datatype из спецификации NIfTI-1.
    private const short UInt8 = 2;
    private const short Int16 = 4;
    private const short Int32 = 8;
    private const short Float32 = 16;
    private const short Float64 = 64;
    private const short Int8 = 256;
    private const short UInt16 = 512;
    private const short UInt32 = 768;

    /// <summary>
    /// Читает объём из файла.
    /// </summary>
    /// <param name="path">Путь к файлу .nii или .nii.gz.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Объём; у многомерного файла — первый том.</returns>
    /// <exception cref="InvalidDataException">
    /// Если заголовок неверен, тип данных не поддерживается или данных меньше,
    /// чем заявлено заголовком.
    /// </exception>
    public static async Task<IVoxelVolume> LoadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var file = File.OpenRead(path);
        await using Stream stream = path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
            ? new GZipStream(file, CompressionMode.Decompress)
            : file;

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        return Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>
    /// Разбирает содержимое файла NIfTI-1.
    /// </summary>
    /// <param name="content">Файл целиком, уже распакованный.</param>
    /// <returns>Объём; у многомерного файла — первый том.</returns>
    /// <exception cref="InvalidDataException">
    /// Если заголовок неверен, тип данных не поддерживается или данных меньше,
    /// чем заявлено заголовком.
    /// </exception>
    public static IVoxelVolume Parse(ReadOnlySpan<byte> content)
    {
        var geometry = NiftiHeaderReader.Read(content);

        var littleEndian = BinaryPrimitives.ReadInt32LittleEndian(content) == NiftiHeaderReader.HeaderSizeBytes;

        var datatype = ReadInt16(content[70..], littleEndian);
        var offset = (long)ReadSingle(content[108..], littleEndian);
        var slope = ReadSingle(content[112..], littleEndian);
        var intercept = ReadSingle(content[116..], littleEndian);

        // Нулевой или нечисловой наклон по спецификации означает «масштаба нет».
        // Принять его буквально значило бы обнулить весь объём.
        if (slope == 0 || !float.IsFinite(slope))
        {
            slope = 1;
            intercept = 0;
        }

        var dimensions = geometry.Dimensions;
        var count = (long)dimensions.Columns * dimensions.Rows * Math.Max(dimensions.Slices, 1);
        var size = BytesPer(datatype);

        if (offset < NiftiHeaderReader.HeaderSizeBytes || offset + (count * size) > content.Length)
        {
            // Усечённый файл — частое состояние скачанного архива. Дочитать
            // недостающее нулями значило бы подать сегментации объём с пустой
            // половиной головы.
            throw new InvalidDataException("NIfTI voxel data is shorter than the header declares.");
        }

        var data = content.Slice((int)offset, (int)(count * size));
        var voxels = new float[count];

        for (var index = 0; index < voxels.Length; index++)
        {
            var raw = ReadValue(data.Slice(index * size, size), datatype, littleEndian);
            voxels[index] = (float)((raw * slope) + intercept);
        }

        return new VoxelVolume(geometry, voxels);
    }

    private static int BytesPer(short datatype) => datatype switch
    {
        UInt8 or Int8 => 1,
        Int16 or UInt16 => 2,
        Int32 or UInt32 or Float32 => 4,
        Float64 => 8,
        _ => throw new InvalidDataException("NIfTI datatype is not supported."),
    };

    private static double ReadValue(ReadOnlySpan<byte> bytes, short datatype, bool littleEndian) => datatype switch
    {
        UInt8 => bytes[0],
        Int8 => (sbyte)bytes[0],
        Int16 => ReadInt16(bytes, littleEndian),
        UInt16 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes),
        Int32 => littleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes),
        UInt32 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes),
        Float32 => ReadSingle(bytes, littleEndian),
        Float64 => littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes),
        _ => throw new InvalidDataException("NIfTI datatype is not supported."),
    };

    private static short ReadInt16(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes);

    private static float ReadSingle(ReadOnlySpan<byte> bytes, bool littleEndian) =>
        littleEndian ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : BinaryPrimitives.ReadSingleBigEndian(bytes);
}
