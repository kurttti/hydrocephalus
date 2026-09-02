using FellowOakDicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Построение синтетических DICOM-файлов для тестов. Реальных медицинских данных
/// в тестах нет и быть не может (tests/README.md): все фикстуры создаются здесь.
/// </summary>
internal static class SyntheticDicom
{
    internal const string MrSopClassUid = "1.2.840.10008.5.1.4.1.1.4";

    /// <summary>Создаёт временный каталог, удаляемый вызывающей стороной.</summary>
    internal static DirectoryInfo CreateTempDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hydro-" + Guid.NewGuid().ToString("N")));

    /// <summary>
    /// Записывает синтетический срез МРТ.
    /// </summary>
    /// <param name="path">Полный путь файла.</param>
    /// <param name="studyUid">StudyInstanceUID.</param>
    /// <param name="seriesUid">SeriesInstanceUID.</param>
    /// <param name="patientId">PatientID, может быть пустым.</param>
    /// <param name="patientName">PatientName, может быть пустым.</param>
    /// <param name="birthDate">PatientBirthDate, может быть пустым.</param>
    /// <param name="seriesDescription">Описание серии.</param>
    /// <param name="acquisitionType">Значение MRAcquisitionType.</param>
    /// <param name="sliceThickness">Толщина среза, мм.</param>
    /// <param name="fieldStrength">Значение MagneticFieldStrength; null — тег не пишется.</param>
    internal static void WriteSlice(
        string path,
        string studyUid,
        string seriesUid,
        string patientId = "",
        string patientName = "",
        string birthDate = "",
        string seriesDescription = "T1 MPRAGE",
        string acquisitionType = "3D",
        decimal sliceThickness = 1.0m,
        decimal? fieldStrength = 1.5m)
    {
        var dataset = new DicomDataset
        {
            { DicomTag.SOPClassUID, MrSopClassUid },
            { DicomTag.SOPInstanceUID, DicomUIDGenerator.GenerateDerivedFromUUID().UID },
            { DicomTag.StudyInstanceUID, studyUid },
            { DicomTag.SeriesInstanceUID, seriesUid },
            { DicomTag.Modality, "MR" },
            { DicomTag.PatientID, patientId },
            { DicomTag.PatientName, patientName },
            { DicomTag.PatientBirthDate, birthDate },
            { DicomTag.SeriesDescription, seriesDescription },
            { DicomTag.MRAcquisitionType, acquisitionType },
            { DicomTag.SliceThickness, sliceThickness },
            { DicomTag.PixelSpacing, 1.0m, 1.0m },
            { DicomTag.ImageOrientationPatient, 1.0m, 0.0m, 0.0m, 0.0m, 1.0m, 0.0m },
            { DicomTag.ImagePositionPatient, 0.0m, 0.0m, 0.0m },
            { DicomTag.Rows, (ushort)256 },
            { DicomTag.Columns, (ushort)256 },
        };

        if (fieldStrength is not null)
        {
            dataset.Add(DicomTag.MagneticFieldStrength, fieldStrength.Value);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        new DicomFile(dataset).Save(path);
    }

    /// <summary>Строит минимальный корректный заголовок NIfTI-1 с заданными размерами.</summary>
    internal static byte[] BuildNiftiHeader(
        short columns,
        short rows,
        short slices,
        float voxelX = 1.0f,
        float voxelY = 1.0f,
        float voxelZ = 1.0f,
        bool littleEndian = true)
    {
        var header = new byte[352];

        Write(header.AsSpan(0, 4), 348, littleEndian);

        // dim[0..3]: ранг и три пространственных измерения.
        WriteInt16(header.AsSpan(40, 2), 3, littleEndian);
        WriteInt16(header.AsSpan(42, 2), columns, littleEndian);
        WriteInt16(header.AsSpan(44, 2), rows, littleEndian);
        WriteInt16(header.AsSpan(46, 2), slices, littleEndian);

        // pixdim[1..3]: размер вокселя.
        WriteSingle(header.AsSpan(80, 4), voxelX, littleEndian);
        WriteSingle(header.AsSpan(84, 4), voxelY, littleEndian);
        WriteSingle(header.AsSpan(88, 4), voxelZ, littleEndian);

        // srow_x / srow_y: направляющие косинусы.
        WriteSingle(header.AsSpan(280, 4), 1.0f, littleEndian);
        WriteSingle(header.AsSpan(296 + 4, 4), 1.0f, littleEndian);

        return header;
    }

    private static void Write(Span<byte> target, int value, bool littleEndian)
    {
        if (littleEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(target, value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(target, value);
        }
    }

    private static void WriteInt16(Span<byte> target, short value, bool littleEndian)
    {
        if (littleEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(target, value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16BigEndian(target, value);
        }
    }

    private static void WriteSingle(Span<byte> target, float value, bool littleEndian)
    {
        if (littleEndian)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(target, value);
        }
        else
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(target, value);
        }
    }
}
