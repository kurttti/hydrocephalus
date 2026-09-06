using System.Text.Json;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dataset;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Запись манифеста датасета.
///
/// Манифест — граница между приложением и исследовательским контуром. Проверяется
/// и то, что схема совпадает с ожидаемой на стороне Python, и то, что через эту
/// границу не проходит ничего лишнего.
/// </summary>
public sealed class DatasetManifestWriterTests : IDisposable
{
    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_manifest_matches_the_schema_the_python_package_reads()
    {
        var json = Parse(Study());

        Assert.Equal("1.0.0", json.GetProperty("schema_version").GetString());

        var record = json.GetProperty("series").EnumerateArray().Single();

        foreach (var field in new[]
        {
            "series_id",
            "study_id",
            "subject_id",
            "tier",
            "weighting",
            "contrast_enhanced",
            "slice_count",
            "voxel_spacing_mm",
        })
        {
            Assert.True(record.TryGetProperty(field, out _), $"Field {field} is missing.");
        }
    }

    [Fact]
    public void Tier_and_weighting_use_the_names_of_the_domain_model()
    {
        // Перевод названий здесь завёл бы второй словарь, который однажды
        // разойдётся с первым.
        var record = Single(Study());

        Assert.Equal(nameof(AcquisitionTier.Extended), record.GetProperty("tier").GetString());
        Assert.Equal(nameof(SeriesWeighting.T1), record.GetProperty("weighting").GetString());
    }

    [Fact]
    public void Voxel_spacing_is_column_row_slice_and_uses_the_slice_step()
    {
        // Толщина среза и шаг — разные величины: при зазоре объём, посчитанный
        // по толщине, занижен ровно на долю неполученной ткани.
        var record = Single(Study(sliceThickness: 1.0, sliceSpacing: 3.0));

        var spacing = record.GetProperty("voxel_spacing_mm").EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();

        Assert.Equal([0.7, 0.9, 3.0], spacing);
    }

    [Fact]
    public void Every_series_of_every_study_appears_once()
    {
        var json = Parse(Study(seriesCount: 3), Study(studyId: "study-2", seriesCount: 2));

        var records = json.GetProperty("series").EnumerateArray().ToArray();

        Assert.Equal(5, records.Length);
        Assert.Equal(
            5,
            records.Select(record => record.GetProperty("series_id").GetString()).Distinct().Count());
    }

    [Fact]
    public void The_manifest_carries_no_identifying_value()
    {
        // Манифест уходит в исследовательский контур: исходных UID и имён
        // в нём быть не может.
        var text = DatasetManifestWriter.SerializeToString([Study()]);

        Assert.DoesNotContain("Ivanov", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1.2.826", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PatientName", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_manifest_carries_no_reference_diagnosis()
    {
        // Метка живёт во внешнем защищённом реестре и соединяется по пациенту.
        var record = Single(Study());

        Assert.False(record.TryGetProperty("label", out _));
        Assert.False(record.TryGetProperty("diagnosis", out _));
    }

    [Fact]
    public async Task The_manifest_is_written_to_a_file()
    {
        var path = Path.Combine(this.root.FullName, "dataset", "manifest.json");

        await DatasetManifestWriter.WriteAsync([Study()], path, CancellationToken.None);

        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        Assert.Single(document.RootElement.GetProperty("series").EnumerateArray());
    }

    [Fact]
    public void An_empty_collection_yields_an_empty_series_list()
    {
        // Пустой манифест — это корректный ответ «данных нет», а не ошибка:
        // ошибку в этом случае поднимает сборка датасета.
        var json = Parse();

        Assert.Empty(json.GetProperty("series").EnumerateArray());
    }

    private static JsonElement Single(ImagingStudy study) =>
        Parse(study).GetProperty("series").EnumerateArray().First();

    private static JsonElement Parse(params ImagingStudy[] studies) =>
        JsonDocument.Parse(DatasetManifestWriter.SerializeToString(studies)).RootElement;

    private static ImagingStudy Study(
        string studyId = "study-1",
        int seriesCount = 1,
        double sliceThickness = 1.0,
        double sliceSpacing = 1.0) =>
        new()
        {
            PseudonymousStudyId = studyId,
            PseudonymousSubjectId = "subject-1",
            Series = [.. Enumerable.Range(0, seriesCount).Select(index => new ImagingSeries
            {
                PseudonymousSeriesId = $"{studyId}-series-{index}",
                Weighting = SeriesWeighting.T1,
                IsContrastEnhanced = false,
                Geometry = new SeriesGeometry
                {
                    AcquisitionType = MrAcquisitionType.ThreeDimensional,
                    SliceThicknessMillimetres = sliceThickness,
                    SliceSpacingMillimetres = sliceSpacing,
                    PixelSpacing = new InPlaneSpacing(0.9, 0.7),
                    Dimensions = new VolumeDimensions(256, 256, 176),
                    RowDirection = new SpatialVector(1, 0, 0),
                    ColumnDirection = new SpatialVector(0, 1, 0),
                    Origin = default,
                },
            })],
        };
}
