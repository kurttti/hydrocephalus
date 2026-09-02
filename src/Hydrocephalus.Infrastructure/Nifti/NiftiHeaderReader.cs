using System.Buffers.Binary;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Nifti;

/// <summary>
/// Чтение геометрии из заголовка NIfTI-1.
///
/// NIfTI допускается только в исследовательском режиме (docs/clinical/README.md),
/// поэтому реализован разбор заголовка, а не полноценная поддержка формата:
/// пиксельные данные конвейеру передаются отдельно. Заголовок NIfTI-1 фиксирован
/// (348 байт) и документирован, поэтому собственный разбор не требует зависимости.
/// </summary>
public static class NiftiHeaderReader
{
    /// <summary>Размер заголовка NIfTI-1 в байтах.</summary>
    public const int HeaderSizeBytes = 348;

    /// <summary>
    /// Разбирает заголовок и строит геометрию.
    /// </summary>
    /// <param name="header">Первые 348 байт файла.</param>
    /// <returns>Геометрия серии.</returns>
    /// <exception cref="InvalidDataException">
    /// Если заголовок короче требуемого, не является NIfTI-1 либо содержит
    /// недопустимое число измерений.
    /// </exception>
    public static SeriesGeometry Read(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSizeBytes)
        {
            throw new InvalidDataException(
                $"NIfTI header must be at least {HeaderSizeBytes} bytes, got {header.Length}.");
        }

        // sizeof_hdr равен 348 и служит определителем порядка байтов: файлы приходят
        // с обеих архитектур, и молчаливое чтение в неверном порядке дало бы
        // правдоподобный, но неверный размер вокселя.
        var littleEndian = BinaryPrimitives.ReadInt32LittleEndian(header) == HeaderSizeBytes;

        if (!littleEndian && BinaryPrimitives.ReadInt32BigEndian(header) != HeaderSizeBytes)
        {
            throw new InvalidDataException("Not a NIfTI-1 header: sizeof_hdr is neither 348 nor byte-swapped 348.");
        }

        var dimensions = ReadInt16Array(header.Slice(40, 16), littleEndian);
        var pixelDimensions = ReadSingleArray(header.Slice(76, 32), littleEndian);

        var rank = dimensions[0];

        if (rank is < 1 or > 7)
        {
            throw new InvalidDataException($"NIfTI dim[0] must be between 1 and 7, got {rank}.");
        }

        var columns = rank >= 1 ? dimensions[1] : 0;
        var rows = rank >= 2 ? dimensions[2] : 0;
        var slices = rank >= 3 ? dimensions[3] : 0;

        return new SeriesGeometry
        {
            // Объёмное получение выводится из наличия третьего измерения: тега
            // MRAcquisitionType в NIfTI нет, он остаётся принадлежностью DICOM.
            AcquisitionType = slices > 1
                ? MrAcquisitionType.ThreeDimensional
                : MrAcquisitionType.TwoDimensional,
            SliceThicknessMillimetres = rank >= 3 ? pixelDimensions[3] : 0,
            PixelSpacing = new InPlaneSpacing(
                rank >= 2 ? pixelDimensions[2] : 0,
                rank >= 1 ? pixelDimensions[1] : 0),
            Dimensions = new VolumeDimensions(columns, rows, slices),
            RowDirection = ReadRow(header, 280, littleEndian),
            ColumnDirection = ReadRow(header, 296, littleEndian),
            Origin = ReadOrigin(header, littleEndian),
        };
    }

    /// <summary>
    /// Разбирает заголовок из потока.
    /// </summary>
    /// <param name="stream">Поток, установленный на начало файла.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Геометрия серии.</returns>
    public static async Task<SeriesGeometry> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[HeaderSizeBytes];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

        return Read(buffer);
    }

    private static short[] ReadInt16Array(ReadOnlySpan<byte> source, bool littleEndian)
    {
        var values = new short[source.Length / sizeof(short)];

        for (var index = 0; index < values.Length; index++)
        {
            var slice = source.Slice(index * sizeof(short), sizeof(short));

            values[index] = littleEndian
                ? BinaryPrimitives.ReadInt16LittleEndian(slice)
                : BinaryPrimitives.ReadInt16BigEndian(slice);
        }

        return values;
    }

    private static float[] ReadSingleArray(ReadOnlySpan<byte> source, bool littleEndian)
    {
        var values = new float[source.Length / sizeof(float)];

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadSingle(source.Slice(index * sizeof(float), sizeof(float)), littleEndian);
        }

        return values;
    }

    private static float ReadSingle(ReadOnlySpan<byte> source, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadSingleLittleEndian(source)
            : BinaryPrimitives.ReadSingleBigEndian(source);

    /// <summary>Читает первые три компоненты строки матрицы sform.</summary>
    private static SpatialVector ReadRow(ReadOnlySpan<byte> header, int offset, bool littleEndian) =>
        new(
            ReadSingle(header.Slice(offset, sizeof(float)), littleEndian),
            ReadSingle(header.Slice(offset + sizeof(float), sizeof(float)), littleEndian),
            ReadSingle(header.Slice(offset + (2 * sizeof(float)), sizeof(float)), littleEndian));

    /// <summary>Читает сдвиги трёх строк матрицы sform — положение начала координат.</summary>
    private static SpatialVector ReadOrigin(ReadOnlySpan<byte> header, bool littleEndian) =>
        new(
            ReadSingle(header.Slice(280 + (3 * sizeof(float)), sizeof(float)), littleEndian),
            ReadSingle(header.Slice(296 + (3 * sizeof(float)), sizeof(float)), littleEndian),
            ReadSingle(header.Slice(312 + (3 * sizeof(float)), sizeof(float)), littleEndian));
}
