using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Синтетические срезы с пиксельными данными. Отдельно от <see cref="SyntheticDicom"/>,
/// потому что здесь нужен полный набор тегов пикселей, а не только геометрия.
/// </summary>
internal static class SyntheticVolume
{
    internal const string MrSopClassUid = "1.2.840.10008.5.1.4.1.1.4";

    /// <summary>
    /// Записывает срез с заданными значениями пикселей.
    /// </summary>
    /// <param name="path">Полный путь файла.</param>
    /// <param name="columns">Число столбцов.</param>
    /// <param name="rows">Число строк.</param>
    /// <param name="positionMillimetres">Смещение среза вдоль оси Z.</param>
    /// <param name="values">Значения пикселей, столбец быстрее строки.</param>
    /// <param name="rescaleSlope">RescaleSlope.</param>
    /// <param name="rescaleIntercept">RescaleIntercept.</param>
    /// <param name="signed">Признак знакового представления пикселей.</param>
    /// <param name="bitsAllocated">BitsAllocated.</param>
    /// <param name="seriesUid">SeriesInstanceUID.</param>
    /// <param name="customize">Дополнительная правка набора тегов до записи.</param>
    internal static void WriteSlice(
        string path,
        int columns,
        int rows,
        decimal positionMillimetres,
        short[] values,
        decimal rescaleSlope = 1.0m,
        decimal rescaleIntercept = 0.0m,
        bool signed = false,
        ushort bitsAllocated = 16,
        string seriesUid = "1.2.3.11",
        Action<DicomDataset>? customize = null)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, MrSopClassUid },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
            { DicomTag.StudyInstanceUID, "1.2.3.1" },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "MR" },
            { DicomTag.MRAcquisitionType, "3D" },
            { DicomTag.SliceThickness, 1.0m },
            { DicomTag.PixelSpacing, 1.0m, 1.0m },
            { DicomTag.ImageOrientationPatient, 1.0m, 0.0m, 0.0m, 0.0m, 1.0m, 0.0m },
            { DicomTag.ImagePositionPatient, 0.0m, 0.0m, positionMillimetres },
            { DicomTag.Columns, (ushort)columns },
            { DicomTag.Rows, (ushort)rows },
            { DicomTag.BitsAllocated, bitsAllocated },
            { DicomTag.BitsStored, bitsAllocated },
            { DicomTag.HighBit, (ushort)(bitsAllocated - 1) },
            { DicomTag.PixelRepresentation, (ushort)(signed ? 1 : 0) },
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            { DicomTag.RescaleSlope, rescaleSlope },
            { DicomTag.RescaleIntercept, rescaleIntercept },
        };

        customize?.Invoke(dataset);

        var pixelData = DicomPixelData.Create(dataset, newPixelData: true);
        pixelData.AddFrame(new MemoryByteBuffer(Encode(values, bitsAllocated)));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DicomFile(dataset).Save(path);
    }

    /// <summary>
    /// Строит несимметричный образец: значение зависит от координат так, что
    /// перепутанные оси или порядок срезов дают заведомо другое число.
    /// </summary>
    /// <param name="columns">Число столбцов.</param>
    /// <param name="rows">Число строк.</param>
    /// <param name="slice">Номер среза.</param>
    /// <returns>Значения пикселей среза.</returns>
    internal static short[] AsymmetricSlice(int columns, int rows, int slice)
    {
        var values = new short[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                values[(row * columns) + column] = (short)((slice * 10000) + (row * 100) + column);
            }
        }

        return values;
    }

    private static byte[] Encode(short[] values, ushort bitsAllocated)
    {
        if (bitsAllocated == 8)
        {
            var bytes = new byte[values.Length];

            for (var index = 0; index < values.Length; index++)
            {
                bytes[index] = (byte)values[index];
            }

            return bytes;
        }

        var buffer = new byte[values.Length * 2];

        for (var index = 0; index < values.Length; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(
                buffer.AsSpan(index * 2, 2),
                values[index]);
        }

        return buffer;
    }
}
