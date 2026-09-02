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
    /// <summary>Читает геометрию из набора тегов.</summary>
    /// <param name="dataset">Набор тегов первого среза серии.</param>
    /// <param name="sliceCount">Число фактически найденных срезов серии.</param>
    /// <returns>Геометрия серии.</returns>
    internal static SeriesGeometry Read(DicomDataset dataset, int sliceCount)
    {
        var pixelSpacing = ReadDecimals(dataset, DicomTag.PixelSpacing);
        var orientation = ReadDecimals(dataset, DicomTag.ImageOrientationPatient);
        var position = ReadDecimals(dataset, DicomTag.ImagePositionPatient);

        return new SeriesGeometry
        {
            AcquisitionType = ReadAcquisitionType(dataset),
            SliceThicknessMillimetres = ReadDouble(dataset, DicomTag.SliceThickness),
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
