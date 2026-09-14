using System.Buffers.Binary;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Nifti;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Разбор заголовка NIfTI-1. Формат допускается только в исследовательском режиме,
/// но геометрия из него должна читаться так же строго, как из DICOM: неверно
/// прочитанный размер вокселя исказит все производные измерения.
/// </summary>
public sealed class NiftiHeaderReaderTests
{
    [Fact]
    public void Volume_header_yields_the_extended_tier()
    {
        var header = SyntheticDicom.BuildNiftiHeader(256, 256, 180, voxelZ: 1.0f);

        var geometry = NiftiHeaderReader.Read(header);

        Assert.Equal(MrAcquisitionType.ThreeDimensional, geometry.AcquisitionType);
        Assert.Equal(new VolumeDimensions(256, 256, 180), geometry.Dimensions);
        Assert.Equal(1.0, geometry.SliceThicknessMillimetres, precision: 6);
        Assert.Equal(AcquisitionTier.Extended, geometry.Tier);
    }

    [Fact]
    public void Single_slice_header_is_not_a_volume()
    {
        var header = SyntheticDicom.BuildNiftiHeader(256, 256, 1, voxelZ: 5.0f);

        var geometry = NiftiHeaderReader.Read(header);

        Assert.Equal(MrAcquisitionType.TwoDimensional, geometry.AcquisitionType);
        Assert.Equal(AcquisitionTier.Baseline, geometry.Tier);
    }

    [Fact]
    public void Big_endian_header_is_read_identically()
    {
        // Файлы приходят с обеих архитектур. Чтение в неверном порядке байтов дало бы
        // правдоподобный, но неверный размер вокселя — это молчаливая порча измерений.
        var little = NiftiHeaderReader.Read(SyntheticDicom.BuildNiftiHeader(64, 64, 32, 1.5f, 1.5f, 2.0f));
        var big = NiftiHeaderReader.Read(
            SyntheticDicom.BuildNiftiHeader(64, 64, 32, 1.5f, 1.5f, 2.0f, littleEndian: false));

        Assert.Equal(little.Dimensions, big.Dimensions);
        Assert.Equal(little.SliceThicknessMillimetres, big.SliceThicknessMillimetres, precision: 6);
        Assert.Equal(little.PixelSpacing, big.PixelSpacing);
    }

    [Fact]
    public void Truncated_header_is_rejected()
    {
        var truncated = new byte[100];

        Assert.Throws<InvalidDataException>(() => NiftiHeaderReader.Read(truncated));
    }

    [Fact]
    public void Header_that_is_not_nifti_is_rejected()
    {
        // Ни прямой, ни переставленный sizeof_hdr не равен 348.
        var garbage = new byte[NiftiHeaderReader.HeaderSizeBytes];
        garbage[0] = 0xAA;
        garbage[1] = 0xBB;

        Assert.Throws<InvalidDataException>(() => NiftiHeaderReader.Read(garbage));
    }

    [Fact]
    public async Task Header_can_be_read_from_a_stream()
    {
        using var stream = new MemoryStream(SyntheticDicom.BuildNiftiHeader(32, 32, 16));

        var geometry = await NiftiHeaderReader.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(new VolumeDimensions(32, 32, 16), geometry.Dimensions);
    }

    [Fact]
    public void A_sagittal_sform_yields_a_sagittal_series_in_patient_coordinates()
    {
        // Заголовок как у T1 из IXI: первый индекс идёт назад, второй вверх,
        // третий — к правой стороне пациента. Строки матрицы читались прежде
        // как направления, и серия выходила аксиальной.
        var header = SyntheticDicom.BuildNiftiHeader(256, 256, 150, 0.94f, 0.94f, 1.2f);
        WriteSform(header, [0f, 0f, 1.2f, -88f], [-0.93f, 0.12f, 0f, 116f], [0.12f, 0.93f, 0f, -112f]);

        var geometry = NiftiHeaderReader.Read(header);

        Assert.Equal(ImagingPlane.Sagittal, geometry.Plane);

        // RAS -> LPS: X и Y меняют знак.
        Assert.Equal(AnatomicalDirection.Posterior, PatientOrientation.Of(geometry.RowDirection));
        Assert.Equal(AnatomicalDirection.Superior, PatientOrientation.Of(geometry.ColumnDirection));
        Assert.Equal(1.0, geometry.RowDirection.Length, precision: 6);
    }

    [Fact]
    public void A_left_handed_file_is_read_with_its_slices_reversed()
    {
        // Третья ось файла ведёт к правой стороне, а произведение первых двух —
        // к левой. Срезы разворачиваются, и первым становится последний срез
        // файла: он и лежит на стороне, куда указывает нормаль.
        var header = SyntheticDicom.BuildNiftiHeader(256, 256, 150, 0.94f, 0.94f, 1.2f);
        WriteSform(header, [0f, 0f, 1.2f, -88f], [-0.93f, 0.12f, 0f, 116f], [0.12f, 0.93f, 0f, -112f]);

        var (geometry, reverses) = NiftiHeaderReader.ReadWithSliceOrder(header);

        Assert.True(reverses);
        Assert.Equal(AnatomicalDirection.Left, PatientOrientation.Of(geometry.SliceNormal));

        // Начало — последний срез файла: 88 мм в LPS минус 149 шагов по 1.2 мм вправо.
        Assert.Equal(88 - (149 * 1.2), geometry.Origin.X, precision: 3);
        Assert.Equal(-116, geometry.Origin.Y, precision: 3);
    }

    [Fact]
    public void Without_an_sform_the_quaternion_orientation_is_used()
    {
        var header = SyntheticDicom.BuildNiftiHeader(64, 64, 32, 1f, 1f, 2f);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(252, 2), 1);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(268, 4), 10f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(272, 4), 20f);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(276, 4), 30f);

        var (geometry, reverses) = NiftiHeaderReader.ReadWithSliceOrder(header);

        // Единичный кватернион в RAS: первый индекс — вправо пациента, второй — вперёд.
        Assert.Equal(AnatomicalDirection.Right, PatientOrientation.Of(geometry.RowDirection));
        Assert.Equal(AnatomicalDirection.Anterior, PatientOrientation.Of(geometry.ColumnDirection));
        Assert.Equal(new SpatialVector(-10, -20, 30), geometry.Origin);
        Assert.False(reverses);
    }

    [Fact]
    public void A_negative_qfac_reverses_the_slices()
    {
        var header = SyntheticDicom.BuildNiftiHeader(64, 64, 32, 1f, 1f, 2f);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(252, 2), 1);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(76, 4), -1f);

        var (geometry, reverses) = NiftiHeaderReader.ReadWithSliceOrder(header);

        Assert.True(reverses);
        Assert.Equal(-62, geometry.Origin.Z, precision: 6);
    }

    [Fact]
    public void A_header_without_orientation_keeps_the_file_axes()
    {
        var geometry = NiftiHeaderReader.Read(SyntheticDicom.BuildNiftiHeader(64, 64, 32));

        Assert.Equal(new SpatialVector(1, 0, 0), geometry.RowDirection);
        Assert.Equal(new SpatialVector(0, 1, 0), geometry.ColumnDirection);
    }

    private static void WriteSform(byte[] header, float[] x, float[] y, float[] z)
    {
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(254, 2), 1);

        for (var index = 0; index < 4; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(280 + (index * 4), 4), x[index]);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(296 + (index * 4), 4), y[index]);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(312 + (index * 4), 4), z[index]);
        }
    }
}
