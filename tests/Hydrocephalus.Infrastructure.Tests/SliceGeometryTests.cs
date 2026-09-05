using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Проверка геометрии на синтетических сериях с известным ответом.
///
/// Шаг между срезами вычисляется по ImagePositionPatient, а не берётся из
/// SliceThickness: это разные величины, и рутинные 2D-серии выборки идут
/// с зазором штатно. Ошибка здесь не проявляется как сбой — она проявляется
/// как правдоподобный, но неверный объём.
/// </summary>
public sealed class SliceGeometryTests : IDisposable
{
    private const string Salt = "test-salt-not-a-secret";
    private const string StudyUid = "1.2.3.1";
    private const string SeriesUid = "1.2.3.11";

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Spacing_comes_from_positions_and_not_from_slice_thickness()
    {
        // Толщина 5мм при шаге 6мм: миллиметр ткани между срезами не получен.
        // Объём, посчитанный по толщине, был бы занижен на эту долю.
        this.WriteStack(positions: [0m, 6m, 12m, 18m], sliceThickness: 5.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(6.0, series.Geometry.SliceSpacingMillimetres, precision: 6);
        Assert.Equal(5.0, series.Geometry.SliceThicknessMillimetres, precision: 6);
        Assert.True(series.Geometry.HasSliceGap);
        Assert.Equal(6.0, series.Geometry.EffectiveSliceSamplingMillimetres, precision: 6);
    }

    [Fact]
    public async Task Contiguous_series_has_no_gap()
    {
        this.WriteStack(positions: [0m, 1m, 2m, 3m], sliceThickness: 1.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(1.0, series.Geometry.SliceSpacingMillimetres, precision: 6);
        Assert.False(series.Geometry.HasSliceGap);
    }

    [Fact]
    public async Task Order_of_files_in_the_directory_does_not_change_the_geometry()
    {
        // Порядок обхода каталога произволен, а положения срезов — нет.
        this.WriteStack(positions: [6m, 0m, 18m, 12m], sliceThickness: 5.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(6.0, series.Geometry.SliceSpacingMillimetres, precision: 6);
        Assert.Equal(4, series.Geometry.Dimensions.Slices);
    }

    [Fact]
    public async Task Oblique_series_spacing_is_measured_along_its_own_normal()
    {
        // Косой срез под 45°: разность координат Z дала бы 2.12мм вместо 3мм,
        // то есть занижение шага почти в полтора раза на каждой такой серии.
        const decimal Cosine = 0.70710678m;
        const decimal Step = 3.0m;

        var orientation = new[] { 1.0m, 0.0m, 0.0m, 0.0m, Cosine, Cosine };

        for (var index = 0; index < 4; index++)
        {
            var offset = Step * index;

            this.Write(
                index,
                dataset =>
                {
                    dataset.AddOrUpdate(DicomTag.ImageOrientationPatient, orientation);
                    dataset.AddOrUpdate(
                        DicomTag.ImagePositionPatient,
                        0.0m,
                        -offset * Cosine,
                        offset * Cosine);
                },
                sliceThickness: 3.0m);
        }

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(3.0, series.Geometry.SliceSpacingMillimetres, precision: 4);
        Assert.False(series.Geometry.HasSliceGap);
    }

    [Fact]
    public async Task Coronal_series_spacing_is_measured_along_its_own_normal()
    {
        // Нормаль корональной серии идёт вдоль Y, и координата Z у всех срезов
        // одинакова: шаг по Z был бы равен нулю.
        var orientation = new[] { 1.0m, 0.0m, 0.0m, 0.0m, 0.0m, -1.0m };

        for (var index = 0; index < 4; index++)
        {
            var position = 2.0m * index;

            this.Write(
                index,
                dataset =>
                {
                    dataset.AddOrUpdate(DicomTag.ImageOrientationPatient, orientation);
                    dataset.AddOrUpdate(DicomTag.ImagePositionPatient, 0.0m, position, 0.0m);
                },
                sliceThickness: 2.0m);
        }

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(2.0, series.Geometry.SliceSpacingMillimetres, precision: 6);
    }

    [Fact]
    public async Task Missing_slice_in_the_middle_is_reported()
    {
        // Пропущенный срез не виден ни по числу файлов, ни по толщине:
        // он проявляется только как промежуток, выпадающий из шага.
        this.WriteStack(positions: [0m, 1m, 3m, 4m], sliceThickness: 1.0m);

        var result = await this.ScanAsync();

        Assert.Contains(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry
                && finding.Issue.Severity == QualityIssueSeverity.Blocking);
    }

    [Fact]
    public async Task Duplicate_slice_positions_are_reported()
    {
        // Под одним SeriesInstanceUID лежит больше одного набора срезов.
        // Какой из них строить, метаданные не говорят.
        this.WriteStack(positions: [0m, 1m, 1m, 2m], sliceThickness: 1.0m);

        var result = await this.ScanAsync();

        Assert.Contains(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry
                && finding.Issue.Severity == QualityIssueSeverity.Blocking);
    }

    [Fact]
    public async Task Uniform_series_produces_no_geometry_finding()
    {
        // Обратная проверка: без неё «замечаний нет» может означать лишь то,
        // что проверка ничего не ищет.
        this.WriteStack(positions: [0m, 1m, 2m, 3m], sliceThickness: 1.0m);

        var result = await this.ScanAsync();

        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry);
    }

    [Fact]
    public async Task Single_slice_series_falls_back_to_the_slice_thickness()
    {
        // Шаг между срезами у серии из одного среза не определён.
        this.WriteStack(positions: [0m], sliceThickness: 5.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(5.0, series.Geometry.SliceSpacingMillimetres, precision: 6);
        Assert.False(series.Geometry.HasSliceGap);
    }

    [Fact]
    public async Task Thin_slices_with_a_large_gap_are_not_the_extended_tier()
    {
        // 3D-серия с тонкими срезами, но шагом 5мм не даёт данных для объёмных
        // признаков: уровень входа определяется фактической плотностью выборки.
        this.WriteStack(positions: [0m, 5m, 10m, 15m], sliceThickness: 1.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(AcquisitionTier.Baseline, series.Tier);
    }

    [Fact]
    public async Task Contiguous_thin_three_dimensional_series_is_the_extended_tier()
    {
        this.WriteStack(positions: [0m, 1m, 2m, 3m], sliceThickness: 1.0m);

        var series = await this.ScanSingleSeriesAsync();

        Assert.Equal(AcquisitionTier.Extended, series.Tier);
    }

    private void WriteStack(decimal[] positions, decimal sliceThickness)
    {
        for (var index = 0; index < positions.Length; index++)
        {
            SyntheticDicom.WriteSlice(
                Path.Combine(this.root.FullName, $"IM{index}.dcm"),
                StudyUid,
                SeriesUid,
                patientId: "P-1",
                sliceThickness: sliceThickness,
                slicePosition: positions[index]);
        }
    }

    private void Write(int index, Action<DicomDataset> customize, decimal sliceThickness) =>
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, $"IM{index}.dcm"),
            StudyUid,
            SeriesUid,
            patientId: "P-1",
            sliceThickness: sliceThickness,
            customize: customize);

    private Task<DicomScanResult> ScanAsync() =>
        new DicomStudyScanner(new DicomImportOptions { PseudonymSalt = Salt })
            .ScanAsync(this.root.FullName, CancellationToken.None);

    private async Task<ImagingSeries> ScanSingleSeriesAsync()
    {
        var result = await this.ScanAsync();

        return Assert.Single(Assert.Single(result.Studies).Series);
    }
}
