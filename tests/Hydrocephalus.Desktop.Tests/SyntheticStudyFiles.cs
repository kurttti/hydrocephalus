using System.IO;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Запись синтетических DICOM-файлов для сценарных тестов сборки приложения.
///
/// Свой экземпляр, а не общий со сценарными тестами: тестовые проекты не должны
/// зависеть друг от друга, иначе правка фикстуры одного слоя ломает тесты другого.
/// Здесь к той же записи добавлена ориентация среза — без неё нельзя получить
/// корональную серию, а весь смысл проверки в том, что угол мозолистого тела
/// ложится в отчёт именно корональной серии.
///
/// Реальных медицинских данных здесь нет и быть не может (tests/README.md).
/// </summary>
internal static class SyntheticStudyFiles
{
    /// <summary>Размер стороны изображения, отсчётов.</summary>
    internal const ushort Size = 256;

    /// <summary>Радиус головы фантома, мм.</summary>
    internal const int HeadRadius = 50;

    /// <summary>Радиус желудочка фантома, мм.</summary>
    internal const int VentricleRadius = 15;

    /// <summary>Направляющие косинусы аксиального среза.</summary>
    internal static readonly decimal[] Axial = [1m, 0m, 0m, 0m, 1m, 0m];

    /// <summary>
    /// Направляющие косинусы корональной серии: строка идёт влево, столбец —
    /// вниз, нормаль поэтому вдоль оси «перёд-зад», как у настоящей корональной.
    /// </summary>
    internal static readonly decimal[] Coronal = [1m, 0m, 0m, 0m, 0m, -1m];

    private const string MrSopClassUid = "1.2.840.10008.5.1.4.1.1.4";

    // Значения фантома для T1: ликвор темнее ткани.
    private const ushort Background = 0;
    private const ushort Tissue = 800;
    private const ushort CsfOnT1 = 100;

    /// <summary>
    /// Записывает срез серии.
    /// </summary>
    /// <param name="path">Полный путь файла.</param>
    /// <param name="studyUid">StudyInstanceUID.</param>
    /// <param name="seriesUid">SeriesInstanceUID.</param>
    /// <param name="patientId">PatientID.</param>
    /// <param name="patientName">PatientName.</param>
    /// <param name="seriesDescription">Описание серии.</param>
    /// <param name="acquisitionType">Значение MRAcquisitionType.</param>
    /// <param name="sliceThickness">Толщина среза, мм.</param>
    /// <param name="slicePosition">Смещение среза вдоль нормали, мм.</param>
    /// <param name="orientation">Направляющие косинусы строки и столбца.</param>
    /// <param name="phantom">Рисовать ли шар ликвора внутри шара ткани.</param>
    /// <param name="sliceIndex">Номер среза в серии; нужен фантому для положения по оси.</param>
    /// <param name="sliceCount">Число срезов в серии; нужно фантому для центра по оси.</param>
    internal static void WriteSlice(
        string path,
        string studyUid,
        string seriesUid,
        string patientId = "",
        string patientName = "",
        string seriesDescription = "T1 MPRAGE",
        string acquisitionType = "3D",
        decimal sliceThickness = 1.0m,
        decimal slicePosition = 0.0m,
        decimal[]? orientation = null,
        bool phantom = false,
        int sliceIndex = 0,
        int sliceCount = 1)
    {
        var cosines = orientation ?? Axial;

        // Срезы идут вдоль нормали, а не всегда вдоль оси Z: у корональной
        // серии нормаль смотрит вперёд-назад, и положение, записанное по Z,
        // дало бы серию с нулевым шагом, которую читатель объёма отвергнет.
        var normal = Cross(cosines);

        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, MrSopClassUid },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.StudyDate, "20240115" },
            { DicomTag.Modality, "MR" },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, patientName },
            { DicomTag.SeriesDescription, seriesDescription },
            { DicomTag.MRAcquisitionType, acquisitionType },
            { DicomTag.SliceThickness, sliceThickness },
            { DicomTag.PixelSpacing, 1.0m, 1.0m },
            {
                DicomTag.ImageOrientationPatient,
                cosines[0], cosines[1], cosines[2], cosines[3], cosines[4], cosines[5]
            },
            {
                DicomTag.ImagePositionPatient,
                normal[0] * slicePosition, normal[1] * slicePosition, normal[2] * slicePosition
            },
            { DicomTag.Rows, Size },
            { DicomTag.Columns, Size },
            { DicomTag.MagneticFieldStrength, 1.5m },

            // Без описания формата пикселей читатель объёма отказывается
            // разбирать серию, и измерение не выполняется вовсе.
            { DicomTag.SamplesPerPixel, (ushort)1 },
            { DicomTag.PhotometricInterpretation, "MONOCHROME2" },
            { DicomTag.BitsAllocated, (ushort)16 },
            { DicomTag.BitsStored, (ushort)16 },
            { DicomTag.HighBit, (ushort)15 },
            { DicomTag.PixelRepresentation, (ushort)0 },
        };

        // Пиксели пишутся через DicomPixelData, а не AddOrUpdate: у PixelData
        // тип OB, и массив ushort в него не кладётся.
        DicomPixelData
            .Create(dataset, newPixelData: true)
            .AddFrame(new MemoryByteBuffer(
                phantom ? Phantom(sliceIndex, sliceCount) : new byte[Size * Size * 2]));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DicomFile(dataset).Save(path);
    }

    /// <summary>
    /// Нормаль среза как векторное произведение направляющих косинусов.
    /// </summary>
    /// <param name="cosines">Шесть косинусов строки и столбца.</param>
    /// <returns>Три составляющие нормали.</returns>
    private static decimal[] Cross(decimal[] cosines) =>
    [
        (cosines[1] * cosines[5]) - (cosines[2] * cosines[4]),
        (cosines[2] * cosines[3]) - (cosines[0] * cosines[5]),
        (cosines[0] * cosines[4]) - (cosines[1] * cosines[3]),
    ];

    /// <summary>
    /// Рисует срез шара ликвора внутри шара ткани.
    /// </summary>
    /// <param name="sliceIndex">Номер среза.</param>
    /// <param name="sliceCount">Число срезов серии.</param>
    /// <returns>Отсчёты среза.</returns>
    /// <remarks>
    /// Шар, а не анатомия: проверяется, что сборка доходит до измерения,
    /// а не то, насколько метод похож на желудочки.
    /// </remarks>
    private static byte[] Phantom(int sliceIndex, int sliceCount)
    {
        // Байты, а не ushort: срез уходит в буфер как есть, в порядке
        // little-endian, которым объявлен синтаксис передачи.
        var pixels = new byte[Size * Size * 2];

        var centre = Size / 2;
        var depth = sliceIndex - (sliceCount / 2);

        for (var row = 0; row < Size; row++)
        {
            for (var column = 0; column < Size; column++)
            {
                var dc = column - centre;
                var dr = row - centre;

                var distance = Math.Sqrt((dc * dc) + (dr * dr) + (depth * depth));

                var value = distance > HeadRadius
                    ? Background
                    : distance <= VentricleRadius ? CsfOnT1 : Tissue;

                var offset = ((row * Size) + column) * 2;

                pixels[offset] = (byte)(value & 0xFF);
                pixels[offset + 1] = (byte)(value >> 8);
            }
        }

        return pixels;
    }
}
