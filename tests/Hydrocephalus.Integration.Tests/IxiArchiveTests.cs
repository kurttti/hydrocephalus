using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.SegmentationCheck;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Разбор имён архива IXI.
///
/// Взвешенность выводится из имени файла, и ошибка здесь направляет порог
/// в обратную сторону: T2, прочитанный как T1, выглядел бы как отказ метода,
/// которого на самом деле нет.
/// </summary>
public sealed class IxiArchiveTests
{
    [Theory]
    [InlineData("IXI002-Guys-0828-T1.nii.gz", SeriesWeighting.T1)]
    [InlineData("IXI012-HH-1211-T2.nii.gz", SeriesWeighting.T2)]
    [InlineData("IXI012-HH-1211-PD.nii.gz", SeriesWeighting.Unknown)]
    [InlineData("./IXI-T1/IXI013-IOP-0839-T1.nii.gz", SeriesWeighting.T1)]
    [InlineData("readme.txt", SeriesWeighting.Unknown)]
    public void Weighting_comes_from_the_suffix_only(string name, SeriesWeighting expected) =>
        Assert.Equal(expected, IxiArchive.WeightingOf(name));

    [Theory]
    [InlineData("IXI002-Guys-0828-T1.nii.gz", "Guys")]
    [InlineData("IXI012-HH-1211-T2.nii.gz", "HH")]
    [InlineData("IXI013-IOP-0839-T1.nii.gz", "IOP")]
    [InlineData("odd.nii.gz", "?")]
    public void The_site_is_read_from_the_name(string name, string expected) =>
        Assert.Equal(expected, IxiArchive.SiteOf(name));

    [Fact]
    public void Percentiles_use_the_nearest_rank()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(5, IxiArchive.Percentile(sorted, 50));
        Assert.Equal(1, IxiArchive.Percentile(sorted, 5));
        Assert.Equal(10, IxiArchive.Percentile(sorted, 95));
        Assert.Equal(0, IxiArchive.Percentile([], 50));
    }
}
