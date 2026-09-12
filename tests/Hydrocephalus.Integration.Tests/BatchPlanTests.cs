using Hydrocephalus.BatchMeasure;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Integration.Tests;

/// <summary>
/// Правила пакетного замера, ошибку в которых по результату не увидеть.
///
/// Замер по исключённой папке даёт правдоподобные цифры по посторонним
/// исследованиям; вывод внутри репозитория попадает в git вместе с измерениями
/// пациентов; исследование, замеренное дважды, удваивает вес пациента в сводке.
/// </summary>
public sealed class BatchPlanTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hydrocephalus-batch-");

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_folder_excluded_by_the_data_owner_is_refused()
    {
        Assert.True(BatchPlan.IsExcludedSource(Path.Combine(this.root.FullName, "Головы")));
        Assert.True(BatchPlan.IsExcludedSource(Path.Combine(this.root.FullName, "Головы") + Path.DirectorySeparatorChar));
        Assert.False(BatchPlan.IsExcludedSource(Path.Combine(this.root.FullName, "МРТ иНТГ")));
    }

    [Fact]
    public void Output_inside_the_repository_is_refused()
    {
        // Каталог теста лежит внутри репозитория: сборка идёт из него.
        Assert.True(BatchPlan.IsInsideRepository(AppContext.BaseDirectory));
    }

    [Fact]
    public void Output_outside_any_repository_is_allowed()
    {
        Assert.False(BatchPlan.IsInsideRepository(this.root.FullName));
    }

    [Fact]
    public void A_nested_repository_marker_is_found_from_below()
    {
        Directory.CreateDirectory(Path.Combine(this.root.FullName, "other", ".git"));

        Assert.True(BatchPlan.IsInsideRepository(Path.Combine(this.root.FullName, "other", "out", "deeper")));
    }

    [Fact]
    public async Task A_study_in_two_sources_is_measured_once_from_the_fuller_copy()
    {
        // Каждый четвёртый пациент выборки лежит в нескольких папках, и выгрузки
        // бывают неполными по-разному.
        var partial = Path.Combine(this.root.FullName, "partial");
        var full = Path.Combine(this.root.FullName, "full");

        Write(partial, slices: 2);
        Write(full, slices: 5);

        var options = new DicomImportOptions { PseudonymSalt = "batch-plan-test-salt" };
        var scanner = new DicomStudyScanner(options);

        var plan = BatchPlan.ChooseOccurrences(
        [
            await scanner.ScanAsync(partial, CancellationToken.None),
            await scanner.ScanAsync(full, CancellationToken.None),
        ]);

        var chosen = Assert.Single(plan);

        Assert.Equal(2, chosen.Occurrences);
        Assert.Equal(5, Assert.Single(chosen.Study.Series).Geometry.Dimensions.Slices);
    }

    private static void Write(string directory, int slices)
    {
        for (var index = 0; index < slices; index++)
        {
            SyntheticStudyFiles.WriteSlice(
                Path.Combine(directory, index + ".dcm"),
                studyUid: "1.2.826.0.1.99.1",
                seriesUid: "1.2.826.0.1.99.1.1",
                patientId: "P-1",
                slicePosition: index);
        }
    }
}
