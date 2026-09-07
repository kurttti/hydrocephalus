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
    private const string RepeatedInstanceUid = "1.2.3.111";

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

    [Fact]
    public async Task A_file_repeated_in_the_export_is_not_a_second_slice()
    {
        // Одна и та же серия, лежащая в выгрузке дважды, — обычное дело.
        // Без сверки SOPInstanceUID повтор дал бы срезу пару с нулевым
        // расстоянием, и серия прочиталась бы как два набора срезов.
        this.WriteStack(positions: [0m, 1m, 2m, 3m], sliceThickness: 1.0m);

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "copy", "IM0.dcm"),
            StudyUid,
            SeriesUid,
            patientId: "P-1",
            sliceThickness: 1.0m,
            slicePosition: 4m,
            sopInstanceUid: RepeatedInstanceUid);

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "IM4.dcm"),
            StudyUid,
            SeriesUid,
            patientId: "P-1",
            sliceThickness: 1.0m,
            slicePosition: 4m,
            sopInstanceUid: RepeatedInstanceUid);

        var result = await this.ScanAsync();

        Assert.Equal(
            1,
            result.Rejections.Count(rejection => rejection.Code == ImportRejectionCode.DuplicateInstance));

        // Срезов пять, а не шесть: повтор не стал шестым срезом и не превратил
        // ровную серию в две наложенные.
        Assert.Equal(5, Assert.Single(Assert.Single(result.Studies).Series).Geometry.Dimensions.Slices);

        Assert.DoesNotContain(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry);
    }

    [Fact]
    public async Task Instances_without_a_position_are_named_as_such()
    {
        // Отсутствующий ImagePositionPatient читается как нулевое положение,
        // и без отдельной проверки такая серия выглядела бы как набор
        // совпадающих срезов — то есть диагноз был бы неверным.
        for (var index = 0; index < 4; index++)
        {
            this.Write(index, dataset => dataset.Remove(DicomTag.ImagePositionPatient), sliceThickness: 1.0m);
        }

        Assert.Equal("missingSlicePositions", await this.GeometryReasonAsync());
    }

    [Fact]
    public async Task A_series_of_several_planes_is_named_as_such_and_not_as_duplicate_positions()
    {
        // Обзорная серия из нескольких проекций под одним SeriesInstanceUID.
        // Проекция на одну нормаль сводит срезы разных плоскостей в одну точку,
        // поэтому «совпадающие положения» здесь были бы артефактом разбора.
        var planes = new[]
        {
            new[] { 1.0m, 0.0m, 0.0m, 0.0m, 1.0m, 0.0m },
            new[] { 0.0m, 1.0m, 0.0m, 0.0m, 0.0m, -1.0m },
        };

        for (var index = 0; index < 4; index++)
        {
            var plane = planes[index % planes.Length];
            var offset = 10.0m * index;

            this.Write(
                index,
                dataset =>
                {
                    dataset.AddOrUpdate(DicomTag.ImageOrientationPatient, plane);
                    dataset.AddOrUpdate(DicomTag.ImagePositionPatient, offset, offset, offset);
                },
                sliceThickness: 1.0m);
        }

        Assert.Equal("mixedOrientations", await this.GeometryReasonAsync());
    }

    [Fact]
    public async Task A_two_echo_stack_is_reported_as_splittable_by_echo()
    {
        // Два эха под одним SeriesInstanceUID: положения совпадают попарно,
        // и серия распадается на два полных набора. Это разделимо однозначно.
        WriteEchoStack(this, positions: [0m, 1m], echoes: [1, 2]);

        var issue = await this.GeometryIssueAsync();

        Assert.Equal("duplicateSlicePositions", issue.Parameters["reason"]);
        Assert.Equal("byEcho", issue.Parameters["stackSplit"]);
        Assert.Equal("2", issue.Parameters["distinctOffsets"]);
        Assert.Equal("2", issue.Parameters["distinctEchoes"]);
    }

    [Fact]
    public async Task Overlapping_slices_without_a_known_axis_stay_unexplained()
    {
        // Обратная проверка: без неё «byEcho» могло бы означать лишь то,
        // что разбор всегда находит объяснение.
        this.WriteStack(positions: [0m, 1m, 1m, 2m], sliceThickness: 1.0m);

        var issue = await this.GeometryIssueAsync();

        Assert.Equal("duplicateSlicePositions", issue.Parameters["reason"]);
        Assert.Equal("unexplained", issue.Parameters["stackSplit"]);
    }

    [Fact]
    public async Task An_incomplete_echo_stack_is_not_called_splittable()
    {
        // Три экземпляра на два положения и два эха: один набор неполон,
        // и разделение по эху было бы догадкой.
        WriteEchoStack(this, positions: [0m, 1m], echoes: [1, 2], skipLast: true);

        Assert.Equal("unexplained", (await this.GeometryIssueAsync()).Parameters["stackSplit"]);
    }

    [Fact]
    public async Task A_series_with_duplicate_positions_is_not_also_called_irregular()
    {
        // Нули в промежутках тянут медианный шаг вниз, поэтому серия
        // с совпадающими положениями почти всегда выглядит и неравномерной.
        // Два замечания на одну поломку удваивали бы счёт в инвентаризации.
        this.WriteStack(positions: [0m, 1m, 1m, 2m], sliceThickness: 1.0m);

        var result = await this.ScanAsync();

        Assert.Single(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry);
    }

    [Fact]
    public async Task A_multi_frame_instance_is_named_as_such()
    {
        // Многокадровый экземпляр — целый набор срезов в одном файле:
        // модель «файл — срез» на нём неверна целиком.
        this.Write(
            0,
            dataset => dataset.AddOrUpdate(DicomTag.NumberOfFrames, "24"),
            sliceThickness: 1.0m);

        Assert.Equal("multiFrameInstances", await this.GeometryReasonAsync());
    }

    private static void WriteEchoStack(
        SliceGeometryTests test,
        decimal[] positions,
        int[] echoes,
        bool skipLast = false)
    {
        var index = 0;

        foreach (var echo in echoes)
        {
            foreach (var position in positions)
            {
                var isLast = echo == echoes[^1] && position == positions[^1];

                if (skipLast && isLast)
                {
                    continue;
                }

                var current = echo;

                test.Write(
                    index++,
                    dataset =>
                    {
                        dataset.AddOrUpdate(
                            DicomTag.EchoNumbers,
                            current.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        dataset.AddOrUpdate(DicomTag.ImagePositionPatient, 0.0m, 0.0m, position);
                    },
                    sliceThickness: 1.0m);
            }
        }
    }

    private async Task<QualityIssue> GeometryIssueAsync()
    {
        var result = await this.ScanAsync();

        return Assert.Single(
            result.Findings,
            finding => finding.Issue.Code == QualityIssueCode.InconsistentGeometry).Issue;
    }

    private async Task<string> GeometryReasonAsync() =>
        (await this.GeometryIssueAsync()).Parameters["reason"];

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
