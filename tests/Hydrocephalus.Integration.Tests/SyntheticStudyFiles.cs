using FellowOakDicom;

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
    private const string MrSopClassUid = "1.2.840.10008.5.1.4.1.1.4";

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
    internal static void WriteSlice(
        string path,
        string studyUid,
        string seriesUid,
        string patientId = "",
        string patientName = "",
        string seriesDescription = "T1 MPRAGE",
        string acquisitionType = "3D",
        decimal sliceThickness = 1.0m,
        decimal slicePosition = 0.0m)
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
            { DicomTag.Rows, (ushort)256 },
            { DicomTag.Columns, (ushort)256 },
            { DicomTag.MagneticFieldStrength, 1.5m },
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DicomFile(dataset).Save(path);
    }
}
