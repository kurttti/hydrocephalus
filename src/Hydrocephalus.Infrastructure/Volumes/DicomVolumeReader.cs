using System.Buffers.Binary;
using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Сборка объёма из срезов серии рабочей копии.
///
/// Порядок срезов задаётся их положением вдоль нормали, а не именем файла и не
/// InstanceNumber: имена в рабочей копии — псевдонимы и лексикографически
/// произвольны, а InstanceNumber у части производителей не совпадает с
/// пространственным порядком. Собранный «вверх ногами» объём выглядит правдоподобно
/// и даёт зеркальные измерения.
/// </summary>
public static class DicomVolumeReader
{
    /// <summary>
    /// Наибольшее число вокселей, которое принимается к загрузке.
    /// Соответствует объёму 512×512×1024 и защищает от исчерпания памяти
    /// на подложенных метаданных (docs/security/README.md).
    /// </summary>
    public const long MaxVoxelCount = 512L * 512 * 1024;

    /// <summary>
    /// Загружает объём из каталога одной серии.
    /// </summary>
    /// <param name="seriesDirectory">Каталог серии в рабочей копии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Загруженный объём.</returns>
    /// <exception cref="InvalidDataException">Если срезы несовместимы между собой.</exception>
    /// <exception cref="NotSupportedException">Если формат пикселей не поддерживается.</exception>
    public static async Task<VoxelVolume> LoadAsync(string seriesDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesDirectory);

        var files = Directory.GetFiles(seriesDirectory, "*.dcm", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            throw new InvalidDataException("The series directory contains no DICOM files.");
        }

        var slices = new List<LoadedSlice>(files.Length);

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = await DicomFile.OpenAsync(path).ConfigureAwait(false);

            slices.Add(ReadSlice(file));
        }

        var reference = slices[0];

        EnsureConsistent(slices, reference);

        var normal = reference.RowDirection.Cross(reference.ColumnDirection).Normalized();

        // Сортировка по проекции на нормаль, а не по координате Z: на корональных
        // и косых сериях Z у всех срезов может совпадать.
        var ordered = slices
            .OrderBy(slice => normal.Length == 0 ? 0 : slice.Position.Dot(normal))
            .ToArray();

        var geometry = BuildGeometry(reference, ordered);

        return new VoxelVolume(geometry, Combine(ordered, reference), ReadWindow(reference.Dataset));
    }

    private static LoadedSlice ReadSlice(DicomFile file)
    {
        var dataset = file.Dataset;
        var syntax = dataset.InternalTransferSyntax;

        if (syntax.IsEncapsulated)
        {
            // Сжатые синтаксисы требуют кодеков, которых в офлайн-поставке нет.
            // Отказ с названием синтаксиса полезнее молчаливой порчи значений.
            throw new NotSupportedException(
                $"Compressed transfer syntax {syntax.UID.UID} is not supported by the volume reader.");
        }

        var bitsAllocated = dataset.GetSingleValueOrDefault<ushort>(DicomTag.BitsAllocated, 0);

        if (bitsAllocated is not (8 or 16))
        {
            throw new NotSupportedException(
                $"BitsAllocated {bitsAllocated.ToString(CultureInfo.InvariantCulture)} is not supported.");
        }

        var samples = dataset.GetSingleValueOrDefault<ushort>(DicomTag.SamplesPerPixel, 1);

        if (samples != 1)
        {
            // Цветные серии в конвейер не подаются: признаки считаются
            // по интенсивности одного канала.
            throw new NotSupportedException("Only single-sample grayscale series are supported.");
        }

        var pixelData = DicomPixelData.Create(dataset);

        return new LoadedSlice
        {
            Columns = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Columns, 0),
            Rows = dataset.GetSingleValueOrDefault<ushort>(DicomTag.Rows, 0),
            BitsAllocated = bitsAllocated,
            IsSigned = dataset.GetSingleValueOrDefault<ushort>(DicomTag.PixelRepresentation, 0) == 1,
            IsBigEndian = syntax.Endian == FellowOakDicom.IO.Endian.Big,
            RescaleSlope = ReadDouble(dataset, DicomTag.RescaleSlope, 1.0),
            RescaleIntercept = ReadDouble(dataset, DicomTag.RescaleIntercept, 0.0),
            Position = DicomGeometryReader.ReadPosition(dataset),
            RowDirection = ReadDirection(dataset, 0),
            ColumnDirection = ReadDirection(dataset, 3),
            Dataset = dataset,
            Frame = pixelData.GetFrame(0).Data,
        };
    }

    private static void EnsureConsistent(List<LoadedSlice> slices, LoadedSlice reference)
    {
        if (reference.Columns == 0 || reference.Rows == 0)
        {
            throw new InvalidDataException("The series does not declare image dimensions.");
        }

        var voxels = (long)reference.Columns * reference.Rows * slices.Count;

        if (voxels > MaxVoxelCount)
        {
            throw new InvalidDataException(
                $"The series declares {voxels.ToString(CultureInfo.InvariantCulture)} voxels, above the accepted limit.");
        }

        foreach (var slice in slices)
        {
            if (slice.Columns != reference.Columns || slice.Rows != reference.Rows)
            {
                // Срезы разного размера означают, что под одним идентификатором
                // серии лежит больше одного набора: собирать из них объём нельзя.
                throw new InvalidDataException("Slices of the series differ in image dimensions.");
            }

            if (slice.BitsAllocated != reference.BitsAllocated || slice.IsSigned != reference.IsSigned)
            {
                throw new InvalidDataException("Slices of the series differ in pixel format.");
            }

            var expected = slice.Columns * slice.Rows * (slice.BitsAllocated / 8);

            if (slice.Frame.Length < expected)
            {
                throw new InvalidDataException("A slice carries less pixel data than its dimensions declare.");
            }
        }
    }

    private static SeriesGeometry BuildGeometry(LoadedSlice reference, LoadedSlice[] ordered)
    {
        var geometry = DicomGeometryReader.Read(reference.Dataset, ordered.Length, sliceSpacingMillimetres: 0);

        var positioning = SlicePositions.Analyse(
            [.. ordered.Select(slice => slice.Position)],
            geometry.RowDirection,
            geometry.ColumnDirection,
            geometry.SliceThicknessMillimetres);

        return geometry with
        {
            SliceSpacingMillimetres = positioning.SpacingMillimetres,
            Origin = ordered[0].Position,
        };
    }

    private static float[] Combine(LoadedSlice[] ordered, LoadedSlice reference)
    {
        var perSlice = reference.Columns * reference.Rows;
        var voxels = new float[perSlice * ordered.Length];

        for (var index = 0; index < ordered.Length; index++)
        {
            var slice = ordered[index];
            var target = voxels.AsSpan(index * perSlice, perSlice);

            // Наклон и сдвиг применяются по значениям самого среза: у части
            // производителей они меняются от среза к срезу, и общий коэффициент
            // серии дал бы ступеньку яркости посреди объёма.
            Decode(slice, target);
        }

        return voxels;
    }

    private static void Decode(LoadedSlice slice, Span<float> target)
    {
        var source = slice.Frame.AsSpan();
        var slope = (float)slice.RescaleSlope;
        var intercept = (float)slice.RescaleIntercept;

        if (slice.BitsAllocated == 8)
        {
            for (var index = 0; index < target.Length; index++)
            {
                float raw = slice.IsSigned ? (sbyte)source[index] : source[index];
                target[index] = (raw * slope) + intercept;
            }

            return;
        }

        for (var index = 0; index < target.Length; index++)
        {
            var pair = source.Slice(index * 2, 2);

            float raw = slice.IsSigned
                ? slice.IsBigEndian
                    ? BinaryPrimitives.ReadInt16BigEndian(pair)
                    : BinaryPrimitives.ReadInt16LittleEndian(pair)
                : slice.IsBigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(pair)
                    : BinaryPrimitives.ReadUInt16LittleEndian(pair);

            target[index] = (raw * slope) + intercept;
        }
    }

    private static SpatialVector ReadDirection(DicomDataset dataset, int offset)
    {
        if (!dataset.TryGetValues<string>(DicomTag.ImageOrientationPatient, out var raw) || raw is null)
        {
            return default;
        }

        return new SpatialVector(
            Parse(raw, offset),
            Parse(raw, offset + 1),
            Parse(raw, offset + 2));
    }

    private static double Parse(string[] values, int index) =>
        index < values.Length
            && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    /// <summary>
    /// Читает окно и уровень из тегов серии.
    ///
    /// Теги допускают несколько значений — набор предустановок; берётся первое.
    /// Достоверность здесь не проверяется: она зависит от значений объёма
    /// и решается при выборе окна.
    /// </summary>
    private static WindowLevel? ReadWindow(DicomDataset dataset)
    {
        var center = ReadFirstDouble(dataset, DicomTag.WindowCenter);
        var width = ReadFirstDouble(dataset, DicomTag.WindowWidth);

        return center is null || width is null ? null : new WindowLevel(center.Value, width.Value);
    }

    private static double? ReadFirstDouble(DicomDataset dataset, DicomTag tag)
    {
        if (!dataset.TryGetValues<string>(tag, out var raw) || raw is null || raw.Length == 0)
        {
            return null;
        }

        return double.TryParse(raw[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double ReadDouble(DicomDataset dataset, DicomTag tag, double fallback) =>
        dataset.TryGetSingleValue<decimal>(tag, out var value) ? (double)value : fallback;

    private sealed record LoadedSlice
    {
        public required int Columns { get; init; }

        public required int Rows { get; init; }

        public required ushort BitsAllocated { get; init; }

        public required bool IsSigned { get; init; }

        public required bool IsBigEndian { get; init; }

        public required double RescaleSlope { get; init; }

        public required double RescaleIntercept { get; init; }

        public required SpatialVector Position { get; init; }

        public required SpatialVector RowDirection { get; init; }

        public required SpatialVector ColumnDirection { get; init; }

        public required DicomDataset Dataset { get; init; }

        public required byte[] Frame { get; init; }
    }
}
