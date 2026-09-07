using System.Globalization;
using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Извлечение геометрии серии из DICOM-тегов.
/// Уровень входа (2D/3D) не читается из метаданных как отдельное поле, а выводится
/// доменной моделью из полученной геометрии.
/// </summary>
internal static class DicomGeometryReader
{
    /// <summary>Читает положение среза.</summary>
    /// <param name="dataset">Набор тегов среза.</param>
    /// <returns>Положение первого воксела в системе координат пациента.</returns>
    internal static SpatialVector ReadPosition(DicomDataset dataset)
    {
        var position = ReadDecimals(dataset, DicomTag.ImagePositionPatient);

        return new SpatialVector(
            position.ElementAtOrDefault(0),
            position.ElementAtOrDefault(1),
            position.ElementAtOrDefault(2));
    }

    /// <summary>
    /// Читает экземпляр серии в том виде, в каком он важен для разбора
    /// взаимного расположения срезов.
    ///
    /// Ключи осей строятся как строки и никуда не выводятся: по ним считается
    /// только число различных значений. Само значение эха или направляющих
    /// косинусов в журнал не попадает.
    /// </summary>
    /// <param name="dataset">Набор тегов экземпляра.</param>
    /// <returns>Экземпляр серии.</returns>
    internal static SliceSample ReadSample(DicomDataset dataset)
    {
        var position = ReadDecimals(dataset, DicomTag.ImagePositionPatient);
        var orientation = ReadDecimals(dataset, DicomTag.ImageOrientationPatient);

        return new SliceSample(
            Position: new SpatialVector(
                position.ElementAtOrDefault(0),
                position.ElementAtOrDefault(1),
                position.ElementAtOrDefault(2)),

            // Отсутствующий тег читается как нулевое положение, и без этого
            // признака серия из таких экземпляров выглядела бы как набор
            // совпадающих срезов в точке начала координат.
            HasPosition: position.Length >= 3,

            OrientationKey: Key(orientation),

            // Номер эха предпочтительнее времени эха: он целый и не страдает
            // от записи одного и того же значения разными строками.
            EchoKey: First(
                dataset.GetSingleValueOrDefault(DicomTag.EchoNumbers, string.Empty),
                dataset.GetSingleValueOrDefault(DicomTag.EchoTime, string.Empty)),

            AcquisitionKey:
                dataset.GetSingleValueOrDefault(DicomTag.AcquisitionNumber, string.Empty)
                + "/"
                + dataset.GetSingleValueOrDefault(DicomTag.TemporalPositionIdentifier, string.Empty),

            IsMultiFrame: dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, string.Empty) is { } frames
                && int.TryParse(frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                && count > 1);
    }

    /// <summary>Читает геометрию из набора тегов.</summary>
    /// <param name="dataset">Набор тегов первого среза серии.</param>
    /// <param name="sliceCount">Число фактически найденных срезов серии.</param>
    /// <param name="sliceSpacingMillimetres">
    /// Шаг между срезами, вычисленный по их положениям.
    /// </param>
    /// <returns>Геометрия серии.</returns>
    internal static SeriesGeometry Read(
        DicomDataset dataset,
        int sliceCount,
        double sliceSpacingMillimetres)
    {
        var pixelSpacing = ReadDecimals(dataset, DicomTag.PixelSpacing);
        var orientation = ReadDecimals(dataset, DicomTag.ImageOrientationPatient);
        var position = ReadDecimals(dataset, DicomTag.ImagePositionPatient);

        return new SeriesGeometry
        {
            AcquisitionType = ReadAcquisitionType(dataset),
            SliceThicknessMillimetres = ReadDouble(dataset, DicomTag.SliceThickness),
            SliceSpacingMillimetres = sliceSpacingMillimetres,
            PixelSpacing = new InPlaneSpacing(
                pixelSpacing.ElementAtOrDefault(0),
                pixelSpacing.ElementAtOrDefault(1)),
            Dimensions = new VolumeDimensions(
                dataset.GetSingleValueOrDefault<ushort>(DicomTag.Columns, 0),
                dataset.GetSingleValueOrDefault<ushort>(DicomTag.Rows, 0),
                sliceCount),
            RowDirection = new SpatialVector(
                orientation.ElementAtOrDefault(0),
                orientation.ElementAtOrDefault(1),
                orientation.ElementAtOrDefault(2)),
            ColumnDirection = new SpatialVector(
                orientation.ElementAtOrDefault(3),
                orientation.ElementAtOrDefault(4),
                orientation.ElementAtOrDefault(5)),
            Origin = new SpatialVector(
                position.ElementAtOrDefault(0),
                position.ElementAtOrDefault(1),
                position.ElementAtOrDefault(2)),
        };
    }

    private static MrAcquisitionType ReadAcquisitionType(DicomDataset dataset)
    {
        var value = dataset.GetSingleValueOrDefault(DicomTag.MRAcquisitionType, string.Empty)?.Trim();

        return value switch
        {
            "3D" => MrAcquisitionType.ThreeDimensional,
            "2D" => MrAcquisitionType.TwoDimensional,
            _ => MrAcquisitionType.Unknown,
        };
    }

    private static string First(string preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback?.Trim() ?? string.Empty : preferred.Trim();

    // Округление до четвёртого знака: направляющие косинусы одной серии
    // записываются одинаково, а различие в последнем знаке округления
    // не должно читаться как другая плоскость.
    private static string Key(double[] values) =>
        string.Join(
            '/',
            values.Select(value => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture)));

    private static double ReadDouble(DicomDataset dataset, DicomTag tag) =>
        dataset.TryGetSingleValue<decimal>(tag, out var value)
            ? (double)value
            : 0;

    private static double[] ReadDecimals(DicomDataset dataset, DicomTag tag)
    {
        if (!dataset.TryGetValues<string>(tag, out var raw) || raw is null)
        {
            return [];
        }

        return raw
            .Select(item => double.TryParse(
                item,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0)
            .ToArray();
    }
}
