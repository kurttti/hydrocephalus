using System.Text.Json;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dataset;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Хранилище манифестов датасета.
/// </summary>
public sealed class FileDatasetManifestStoreTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 6, 10, 0, 0, TimeSpan.Zero);

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_manifest_is_written_and_can_be_read_back()
    {
        var path = await Store(Moment).WriteAsync([Study()], CancellationToken.None);

        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        Assert.Single(document.RootElement.GetProperty("series").EnumerateArray());
    }

    [Fact]
    public async Task Two_exports_do_not_overwrite_each_other()
    {
        // По прошлому манифесту уже могла быть обучена модель: подмена состава
        // выборки задним числом делает результат невоспроизводимым молча.
        await Store(Moment).WriteAsync([Study()], CancellationToken.None);
        await Store(Moment.AddSeconds(1)).WriteAsync([Study()], CancellationToken.None);

        Assert.Equal(2, Directory.GetFiles(this.root.FullName, "*.json").Length);
    }

    [Fact]
    public async Task Writing_twice_at_the_same_instant_is_refused()
    {
        var store = Store(Moment);

        await store.WriteAsync([Study()], CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(
            () => store.WriteAsync([Study()], CancellationToken.None));
    }

    [Fact]
    public async Task The_file_name_carries_no_identifier()
    {
        var path = await Store(Moment).WriteAsync([Study()], CancellationToken.None);

        var name = Path.GetFileName(path);

        Assert.StartsWith("manifest-", name, StringComparison.Ordinal);
        Assert.DoesNotContain("subject", name, StringComparison.OrdinalIgnoreCase);
    }

    private FileDatasetManifestStore Store(DateTimeOffset moment) =>
        new(this.root.FullName, new FixedTime(moment));

    private static ImagingStudy Study() => new()
    {
        PseudonymousStudyId = "study-1",
        PseudonymousSubjectId = "subject-1",
        Series =
        [
            new ImagingSeries
            {
                PseudonymousSeriesId = "series-1",
                Weighting = SeriesWeighting.T1,
                IsContrastEnhanced = false,
                Geometry = new SeriesGeometry
                {
                    AcquisitionType = MrAcquisitionType.ThreeDimensional,
                    SliceThicknessMillimetres = 1.0,
                    SliceSpacingMillimetres = 1.0,
                    PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                    Dimensions = new VolumeDimensions(256, 256, 176),
                    RowDirection = new SpatialVector(1, 0, 0),
                    ColumnDirection = new SpatialVector(0, 1, 0),
                    Origin = default,
                },
            },
        ],
    };

    private sealed class FixedTime(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment;
    }
}
