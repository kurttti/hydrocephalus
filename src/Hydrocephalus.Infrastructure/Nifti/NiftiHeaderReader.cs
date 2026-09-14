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
    public static SeriesGeometry Read(ReadOnlySpan<byte> header) => ReadWithSliceOrder(header).Geometry;

    /// <summary>
    /// Разбирает заголовок и сообщает, нужно ли развернуть порядок срезов.
    ///
    /// Геометрия домена задаёт ось срезов как векторное произведение строки
    /// и столбца — так устроен DICOM. В NIfTI третья ось файла независима,
    /// и у «левой» тройки осей (так записаны сагиттальные T1 IXI) она
    /// направлена против произведения. Без разворота срезы легли бы
    /// зеркально: сагиттальная серия меняла бы местами левое и правое.
    /// </summary>
    /// <param name="header">Первые 348 байт файла.</param>
    /// <returns>Геометрия и признак обратного порядка срезов в файле.</returns>
    internal static (SeriesGeometry Geometry, bool ReversesSlices) ReadWithSliceOrder(ReadOnlySpan<byte> header)
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

        var orientation = Orientation.Read(header, pixelDimensions, littleEndian);
        var reverses = slices > 1 && orientation.ReversesSlices;

        var geometry = new SeriesGeometry
        {
            // Объёмное получение выводится из наличия третьего измерения: тега
            // MRAcquisitionType в NIfTI нет, он остаётся принадлежностью DICOM.
            AcquisitionType = slices > 1
                ? MrAcquisitionType.ThreeDimensional
                : MrAcquisitionType.TwoDimensional,
            SliceThicknessMillimetres = rank >= 3 ? pixelDimensions[3] : 0,

            // В NIfTI хранится один шаг вокселя: сетка равномерна по построению,
            // и зазор между срезами в этом формате выразить нечем.
            SliceSpacingMillimetres = rank >= 3 ? pixelDimensions[3] : 0,
            PixelSpacing = new InPlaneSpacing(
                rank >= 2 ? pixelDimensions[2] : 0,
                rank >= 1 ? pixelDimensions[1] : 0),
            Dimensions = new VolumeDimensions(columns, rows, slices),

            // RowDirection в DICOM — куда ведёт рост номера столбца, то есть
            // первого индекса файла.
            RowDirection = orientation.FirstIndex,
            ColumnDirection = orientation.SecondIndex,

            // При развороте первым становится последний срез файла.
            Origin = reverses
                ? Advance(orientation.Origin, orientation.ThirdIndexStep, slices - 1)
                : orientation.Origin,
        };

        return (geometry, reverses);
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

    private static short ReadInt16(ReadOnlySpan<byte> source, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadInt16LittleEndian(source)
            : BinaryPrimitives.ReadInt16BigEndian(source);

    private static SpatialVector Advance(SpatialVector origin, SpatialVector step, int count) =>
        new(origin.X + (step.X * count), origin.Y + (step.Y * count), origin.Z + (step.Z * count));

    /// <summary>
    /// Оси файла в системе координат пациента DICOM (LPS).
    ///
    /// NIfTI задаёт мировые координаты в RAS: X растёт к правой стороне
    /// пациента, Y — вперёд. Домен, как и DICOM, работает в LPS, поэтому X и Y
    /// меняют знак. Матрица sform и кватернион qform хранят оси файла по
    /// столбцам: первый столбец — куда ведёт рост первого индекса. Прежде
    /// строки матрицы читались как направления, и сагиттальная T1 из IXI
    /// выходила аксиальной с перепутанными сторонами.
    /// </summary>
    /// <param name="FirstIndex">Направление роста первого индекса, нормированное.</param>
    /// <param name="SecondIndex">Направление роста второго индекса, нормированное.</param>
    /// <param name="ThirdIndexStep">Шаг третьего индекса в миллиметрах, как записан в файле.</param>
    /// <param name="Origin">Положение первого вокселя файла.</param>
    /// <param name="ReversesSlices">Третья ось файла направлена против произведения первых двух.</param>
    private readonly record struct Orientation(
        SpatialVector FirstIndex,
        SpatialVector SecondIndex,
        SpatialVector ThirdIndexStep,
        SpatialVector Origin,
        bool ReversesSlices)
    {
        public static Orientation Read(ReadOnlySpan<byte> header, float[] pixelDimensions, bool littleEndian)
        {
            var qformCode = ReadInt16(header.Slice(252, 2), littleEndian);
            var sformCode = ReadInt16(header.Slice(254, 2), littleEndian);

            if (sformCode > 0)
            {
                var x = ReadSingleArray(header.Slice(280, 16), littleEndian);
                var y = ReadSingleArray(header.Slice(296, 16), littleEndian);
                var z = ReadSingleArray(header.Slice(312, 16), littleEndian);

                return FromAxes(
                    FromRas(x[0], y[0], z[0]),
                    FromRas(x[1], y[1], z[1]),
                    FromRas(x[2], y[2], z[2]),
                    FromRas(x[3], y[3], z[3]));
            }

            if (qformCode > 0)
            {
                return FromQuaternion(header, pixelDimensions, littleEndian);
            }

            // Метод 1 спецификации: ориентации в файле нет, и о сторонах
            // пациента заголовок ничего не утверждает. Оси берутся как есть.
            return new Orientation(
                new SpatialVector(1, 0, 0),
                new SpatialVector(0, 1, 0),
                new SpatialVector(0, 0, pixelDimensions[3]),
                default,
                ReversesSlices: false);
        }

        private static Orientation FromQuaternion(ReadOnlySpan<byte> header, float[] pixelDimensions, bool littleEndian)
        {
            double b = ReadSingle(header.Slice(256, 4), littleEndian);
            double c = ReadSingle(header.Slice(260, 4), littleEndian);
            double d = ReadSingle(header.Slice(264, 4), littleEndian);
            var a = Math.Sqrt(Math.Max(0, 1 - ((b * b) + (c * c) + (d * d))));

            // qfac хранится в pixdim[0]; всё, кроме -1, спецификация велит считать единицей.
            var qfac = pixelDimensions[0] < 0 ? -1.0 : 1.0;

            var r11 = (a * a) + (b * b) - (c * c) - (d * d);
            var r12 = 2 * ((b * c) - (a * d));
            var r13 = 2 * ((b * d) + (a * c));
            var r21 = 2 * ((b * c) + (a * d));
            var r22 = (a * a) + (c * c) - (b * b) - (d * d);
            var r23 = 2 * ((c * d) - (a * b));
            var r31 = 2 * ((b * d) - (a * c));
            var r32 = 2 * ((c * d) + (a * b));
            var r33 = (a * a) + (d * d) - (c * c) - (b * b);

            double columnStep = pixelDimensions[1];
            double rowStep = pixelDimensions[2];
            var sliceStep = pixelDimensions[3] * qfac;

            return FromAxes(
                FromRas(r11 * columnStep, r21 * columnStep, r31 * columnStep),
                FromRas(r12 * rowStep, r22 * rowStep, r32 * rowStep),
                FromRas(r13 * sliceStep, r23 * sliceStep, r33 * sliceStep),
                FromRas(
                    ReadSingle(header.Slice(268, 4), littleEndian),
                    ReadSingle(header.Slice(272, 4), littleEndian),
                    ReadSingle(header.Slice(276, 4), littleEndian)));
        }

        private static Orientation FromAxes(
            SpatialVector first,
            SpatialVector second,
            SpatialVector third,
            SpatialVector origin)
        {
            var firstDirection = first.Normalized();
            var secondDirection = second.Normalized();

            return new Orientation(
                firstDirection,
                secondDirection,
                third,
                origin,
                ReversesSlices: firstDirection.Cross(secondDirection).Dot(third) < 0);
        }

        private static SpatialVector FromRas(double x, double y, double z) => new(-x, -y, z);
    }
}
