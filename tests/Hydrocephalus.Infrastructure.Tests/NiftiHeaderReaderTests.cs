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
}
