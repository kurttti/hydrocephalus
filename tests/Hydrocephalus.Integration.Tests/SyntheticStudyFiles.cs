using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Запись синтетических DICOM-файлов для сценарных тестов.
///
/// Свой экземпляр, а не общий с тестами инфраструктуры: тестовые проекты не должны
/// зависеть друг от друга, иначе правка фикстуры одного слоя ломает тесты другого.
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

    private const string MrSopClassUid = "1.2.840.10008.5.1.4.1.1.4";

    // Значения фантома для T1: ликвор темнее ткани. Взяты те же, что
    // в модульных тестах сегментации, — фантом, не соответствующий
    // взвешенности серии, даёт пустую маску, и отладка уходит в конвейер,
    // где всё исправно.
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
    /// <param name="phantom">
    /// Рисовать ли шар ликвора внутри шара ткани. По умолчанию срез пустой:
    /// пиксели нужны лишь там, где проверяется измерение, а на каждом срезе
    /// это 128 КБ, которые остальные тесты писали бы впустую.
    /// </param>
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
        bool phantom = false,
        int sliceIndex = 0,
        int sliceCount = 1)
    {
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
            { DicomTag.ImageOrientationPatient, 1.0m, 0.0m, 0.0m, 0.0m, 1.0m, 0.0m },
            { DicomTag.ImagePositionPatient, 0.0m, 0.0m, slicePosition },
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
    /// Рисует срез шара ликвора внутри шара ткани.
    /// </summary>
    /// <param name="sliceIndex">Номер среза.</param>
    /// <param name="sliceCount">Число срезов серии.</param>
    /// <returns>Отсчёты среза.</returns>
    /// <remarks>
    /// Шар, а не анатомия: проверяется, что конвейер доходит до измерения
    /// и отдаёт положительный объём с честной пометкой, а не то, насколько
    /// метод похож на желудочки.
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
