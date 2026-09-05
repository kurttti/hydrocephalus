using FellowOakDicom;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Создание рабочей копии: разбор источника, деидентификация и запись.
///
/// Проверяется не только содержимое файлов, но и их имена: в исходном сборе
/// фамилии пациентов лежат в именах файлов и папок (docs/data/README.md),
/// то есть переименование входит в профиль наравне с очисткой тегов.
/// </summary>
public sealed class StudyImporterTests : IDisposable
{
    private const string Salt = "test-salt-not-a-secret";
    private const string Surname = "Ivanov";

    private readonly DirectoryInfo source = SyntheticDicom.CreateTempDirectory();
    private readonly DirectoryInfo workingCopy = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        Delete(this.source);
        Delete(this.workingCopy);
    }

    [Fact]
    public async Task Working_copy_carries_no_source_name_in_any_file_or_directory_name()
    {
        // Имя папки и имя файла содержат фамилию — так выглядит реальный экспорт.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, Surname + " I.I", Surname + "-t1.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "MRN-778899",
            patientName: "Ivanov^Ivan^Ivanovich");

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        var entries = Directory
            .EnumerateFileSystemEntries(this.workingCopy.FullName, "*", SearchOption.AllDirectories)
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.DoesNotContain(entries, entry => entry.Contains(Surname, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.Contains("778899", StringComparison.Ordinal));

        Assert.StartsWith(this.workingCopy.FullName, result.VolumeReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Written_files_are_readable_dicom_without_identifiers()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "MRN-778899",
            patientName: "Ivanov^Ivan");

        await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        var written = Assert.Single(Directory.EnumerateFiles(
            this.workingCopy.FullName,
            "*.dcm",
            SearchOption.AllDirectories));

        var file = await DicomFile.OpenAsync(written);

        Assert.False(file.Dataset.Contains(DicomTag.PatientName));
        Assert.False(file.Dataset.Contains(DicomTag.PatientID));
        Assert.Equal("MR", file.Dataset.GetSingleValue<string>(DicomTag.Modality));

        // Геометрия сохраняется: без неё рабочая копия непригодна для анализа.
        Assert.Equal(256, file.Dataset.GetSingleValue<ushort>(DicomTag.Rows));
    }

    [Fact]
    public async Task Working_copy_path_is_built_from_the_pseudonyms_of_the_domain_model()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        var expected = Path.Combine(
            this.workingCopy.FullName,
            result.Study.PseudonymousSubjectId,
            result.Study.PseudonymousStudyId);

        Assert.Equal(expected, result.VolumeReference);

        var seriesId = Assert.Single(result.Study.Series).PseudonymousSeriesId;

        Assert.True(Directory.Exists(Path.Combine(expected, seriesId)));
    }

    [Fact]
    public async Task All_instances_of_a_series_share_one_replaced_series_uid()
    {
        for (var index = 0; index < 3; index++)
        {
            SyntheticDicom.WriteSlice(
                Path.Combine(this.source.FullName, $"IM{index}.dcm"),
                studyUid: "1.2.3.1",
                seriesUid: "1.2.3.11",
                patientId: "P-1");
        }

        await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        var files = Directory
            .EnumerateFiles(this.workingCopy.FullName, "*.dcm", SearchOption.AllDirectories)
            .ToArray();

        Assert.Equal(3, files.Length);

        var seriesUids = new List<string>();
        var instanceUids = new List<string>();

        foreach (var path in files)
        {
            var file = await DicomFile.OpenAsync(path);

            seriesUids.Add(file.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID));
            instanceUids.Add(file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID));
        }

        Assert.Single(seriesUids.Distinct(StringComparer.Ordinal));
        Assert.Equal(3, instanceUids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Series_with_burned_in_annotation_is_kept_out_of_the_working_copy()
    {
        // Такая серия уходит в ручной контроль (ADR 0003), а не в рабочую копию.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "clean.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "annotated.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.12",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "YES"));

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Single(result.Study.Series);
        Assert.Single(Directory.EnumerateFiles(
            this.workingCopy.FullName,
            "*.dcm",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Study_whose_only_series_is_annotated_reaches_the_use_case_as_unusable()
    {
        // Исследование не исчезает молча: оно доходит до сценария анализа
        // с уровнем Unusable и отклоняется там, где отказ виден врачу.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "annotated.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.BurnedInAnnotation, "YES"));

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Empty(result.Study.Series);
        Assert.Equal(AcquisitionTier.Unusable, result.Study.BestAvailableTier);
    }

    [Fact]
    public async Task Source_with_more_than_one_study_is_refused()
    {
        // Контракт возвращает одну рабочую копию, поэтому неоднозначный источник —
        // отказ, а не молчаливый выбор одного из исследований.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "b.dcm"),
            studyUid: "1.2.3.2",
            seriesUid: "1.2.3.22",
            patientId: "P-2");

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => this.Importer().ImportAsync(this.source.FullName, CancellationToken.None));
    }

    [Fact]
    public async Task Source_without_a_readable_study_is_refused()
    {
        await File.WriteAllTextAsync(
            Path.Combine(this.source.FullName, "epicrisis.txt"),
            "не DICOM",
            CancellationToken.None);

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => this.Importer().ImportAsync(this.source.FullName, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_audit_stops_the_import_and_leaves_no_working_copy()
    {
        // Фамилия положена в тег, который профиль сохраняет намеренно: список
        // тегов описывает намерение, а гарантию даёт поиск исходных значений
        // в результате. Проверка выполняется в рабочем режиме, поэтому импорт
        // прекращается, а не пишет файл с пометкой в журнале.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientName: "Ivanov^Ivan",
            customize: dataset => dataset.AddOrUpdate(
                DicomTag.ManufacturerModelName,
                "Skyra Ivanov^Ivan"));

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => this.Importer().ImportAsync(this.source.FullName, CancellationToken.None));

        Assert.Empty(Directory.EnumerateFiles(
            this.workingCopy.FullName,
            "*.dcm",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Import_is_deterministic_for_one_salt()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var first = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);
        var again = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Equal(first.VolumeReference, again.VolumeReference);
        Assert.Equal(
            first.Study.Series[0].PseudonymousSeriesId,
            again.Study.Series[0].PseudonymousSeriesId);
    }

    private static void Delete(DirectoryInfo directory)
    {
        if (directory.Exists)
        {
            directory.Delete(recursive: true);
        }
    }

    private StudyImporter Importer() =>
        new(
            new DicomImportOptions { PseudonymSalt = Salt },
            new WorkingCopyOptions { RootDirectory = this.workingCopy.FullName });
}
