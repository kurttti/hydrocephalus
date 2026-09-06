using System.Text.Json;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dataset;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Договор о манифесте между приложением и ML-контуром.
///
/// `ml/tests/data/example-manifest.json` — общий эталон: его читает тест
/// Python-пакета, и с ним же сверяется этот. Схема — единственное место, где
/// два языка обязаны совпадать, и разойтись они могут молча: приложение
/// продолжит писать, пакет продолжит читать, а поля будут значить разное.
///
/// Сравнение структурное, а не побайтовое: отступы и порядок ключей у двух
/// сериализаторов различаются законно, а состав полей и их типы — нет.
/// </summary>
public sealed class DatasetManifestContractTests
{
    [Fact]
    public void The_writer_and_the_shared_example_declare_the_same_schema_version()
    {
        Assert.Equal(
            DatasetManifestWriter.SchemaVersion,
            Example().GetProperty("schema_version").GetString());
    }

    [Fact]
    public void The_writer_produces_exactly_the_fields_the_example_declares()
    {
        // Лишнее поле пакет отвергнет не сразу, а недостающее — сразу и молча
        // изменит состав датасета. Проверяются оба направления.
        var expected = FieldsOf(Example().GetProperty("series").EnumerateArray().First());
        var actual = FieldsOf(Written().GetProperty("series").EnumerateArray().First());

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_field_keeps_the_kind_of_value_the_example_shows()
    {
        var expected = Example().GetProperty("series").EnumerateArray().First();
        var actual = Written().GetProperty("series").EnumerateArray().First();

        foreach (var field in FieldsOf(expected))
        {
            Assert.Equal(
                expected.GetProperty(field).ValueKind,
                actual.GetProperty(field).ValueKind);
        }
    }

    [Fact]
    public void Voxel_spacing_holds_three_numbers_on_both_sides()
    {
        var expected = Example().GetProperty("series").EnumerateArray().First()
            .GetProperty("voxel_spacing_mm").EnumerateArray().Count();

        var actual = Written().GetProperty("series").EnumerateArray().First()
            .GetProperty("voxel_spacing_mm").EnumerateArray().Count();

        Assert.Equal(3, expected);
        Assert.Equal(3, actual);
    }

    [Fact]
    public void The_tier_names_of_the_example_are_names_the_domain_model_uses()
    {
        // Уровень пишется именем перечисления; если перечисление переименуют,
        // эталон и пакет должны это заметить.
        foreach (var record in Example().GetProperty("series").EnumerateArray())
        {
            var tier = record.GetProperty("tier").GetString();

            Assert.True(
                Enum.TryParse<AcquisitionTier>(tier, ignoreCase: false, out _),
                $"Tier '{tier}' is not a name of AcquisitionTier.");
        }
    }

    [Fact]
    public void The_weighting_names_of_the_example_are_names_the_domain_model_uses()
    {
        foreach (var record in Example().GetProperty("series").EnumerateArray())
        {
            var weighting = record.GetProperty("weighting").GetString();

            Assert.True(
                Enum.TryParse<SeriesWeighting>(weighting, ignoreCase: false, out _),
                $"Weighting '{weighting}' is not a name of SeriesWeighting.");
        }
    }

    private static string[] FieldsOf(JsonElement record) =>
        [.. record.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];

    private static JsonElement Written()
    {
        var study = new ImagingStudy
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
                        PixelSpacing = new InPlaneSpacing(0.9, 0.9),
                        Dimensions = new VolumeDimensions(256, 256, 176),
                        RowDirection = new SpatialVector(1, 0, 0),
                        ColumnDirection = new SpatialVector(0, 1, 0),
                        Origin = default,
                    },
                },
            ],
        };

        return JsonDocument.Parse(DatasetManifestWriter.SerializeToString([study])).RootElement;
    }

    private static JsonElement Example()
    {
        var path = Path.Combine(RepositoryRoot(), "ml", "tests", "data", "example-manifest.json");

        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hydrocephalus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
