using FellowOakDicom;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Volumes;

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

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        var file = Assert.Single(await ReadAllAsync(importer, result));

        Assert.False(file.Dataset.Contains(DicomTag.PatientName));
        Assert.False(file.Dataset.Contains(DicomTag.PatientID));
        Assert.Equal("MR", file.Dataset.GetSingleValue<string>(DicomTag.Modality));

        // Геометрия сохраняется: без неё рабочая копия непригодна для анализа.
        Assert.Equal(256, file.Dataset.GetSingleValue<ushort>(DicomTag.Rows));
    }

    [Fact]
    public async Task The_working_copy_directory_name_says_nothing_about_the_study()
    {
        // Имя каталога, выведенное из псевдонима, само связывало бы копию
        // с исследованием — на диске это лишняя связь.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        var name = Path.GetFileName(result.VolumeReference);

        Assert.DoesNotContain(result.Study.PseudonymousStudyId, name, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Study.PseudonymousSubjectId, name, StringComparison.Ordinal);
        Assert.True(Directory.Exists(result.VolumeReference));
    }

    [Fact]
    public async Task Nothing_readable_as_dicom_reaches_the_disk()
    {
        // Пункт плана проверки ADR 0006: сканирование рабочего каталога
        // не находит читаемых DICOM-сигнатур.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        foreach (var path in Directory.EnumerateFiles(
            this.workingCopy.FullName,
            "*",
            SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, CancellationToken.None);

            Assert.DoesNotContain(
                "DICM",
                System.Text.Encoding.ASCII.GetString(bytes),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Releasing_the_working_copy_removes_it()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        await importer.ReleaseAsync(result.VolumeReference);

        Assert.False(Directory.Exists(result.VolumeReference));

        // Обращение к уничтоженному сеансу — чтение данных, которых уже нет.
        Assert.Throws<InvalidOperationException>(() => importer.SessionFor(result.VolumeReference));
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
                patientId: "P-1",
                slicePosition: index);
        }

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        var files = await ReadAllAsync(importer, result);

        Assert.Equal(3, files.Count);

        Assert.Single(files
            .Select(file => file.Dataset.GetSingleValue<string>(DicomTag.SeriesInstanceUID))
            .Distinct(StringComparer.Ordinal));

        Assert.Equal(
            3,
            files
                .Select(file => file.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID))
                .Distinct(StringComparer.Ordinal)
                .Count());
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

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Single(result.Study.Series);
        Assert.Single(await ReadAllAsync(importer, result));

        // Отброшенная серия описывается, а не исчезает: врач, видящий одну
        // серию, иначе не отличит «серия была одна» от «было две, одну убрали».
        var excluded = Assert.Single(result.ExcludedSeries);

        Assert.Equal(
            QualityIssueCode.BurnedInAnnotation,
            Assert.Single(excluded.Issues).Code);

        Assert.NotEqual(
            result.Study.Series[0].PseudonymousSeriesId,
            excluded.Series.PseudonymousSeriesId);
    }

    [Fact]
    public async Task A_study_without_dropped_series_says_so_with_an_empty_list()
    {
        // Пустой список — утверждение «ничего не отброшено», а не отсутствие
        // сведений: иначе чистое исследование выглядело бы так же, как то,
        // о котором ничего не известно.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "clean.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var result = await this.Importer().ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Empty(result.ExcludedSeries);
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

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Empty(result.Study.Series);
        Assert.Equal(AcquisitionTier.Unusable, result.Study.BestAvailableTier);

        // Каталог существует, но данных в нём нет: ссылка на рабочую копию
        // должна разрешаться, а пустая рабочая копия — верное описание такого
        // исследования. Ключ и отметка времени в нём есть — по ним работает уборка.
        Assert.True(Directory.Exists(result.VolumeReference));
        Assert.Empty(await ReadAllAsync(importer, result));
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
    public async Task A_file_exported_twice_does_not_break_the_import()
    {
        // В реальной выборке повтор уже принятого экземпляра — 2805 файлов:
        // одна и та же серия лежит в выгрузке дважды. Разбор отбрасывает повтор
        // по SOPInstanceUID, и запись обязана делать то же, иначе она насчитает
        // срезов больше, чем нашёл разбор, и объявит рабочую копию неполной.
        for (var index = 0; index < 3; index++)
        {
            var uid = SyntheticDicom.WriteSlice(
                Path.Combine(this.source.FullName, "first", index + ".dcm"),
                studyUid: "1.2.3.1",
                seriesUid: "1.2.3.11",
                patientId: "P-1",
                slicePosition: index);

            SyntheticDicom.WriteSlice(
                Path.Combine(this.source.FullName, "copy", index + ".dcm"),
                studyUid: "1.2.3.1",
                seriesUid: "1.2.3.11",
                patientId: "P-1",
                slicePosition: index,
                sopInstanceUid: uid);
        }

        var importer = this.Importer();
        var result = await importer.ImportAsync(this.source.FullName, CancellationToken.None);

        Assert.Equal(3, Assert.Single(result.Study.Series).Geometry.Dimensions.Slices);
        Assert.Equal(3, (await ReadAllAsync(importer, result)).Count);
    }

    [Fact]
    public async Task One_study_is_imported_from_a_source_holding_several()
    {
        // Ретроспективная выборка лежит папками на много пациентов. Импорт
        // выбранного исследования берёт только его серии и не читает источник
        // заново: повторный обход на каждое из 136 исследований превратил бы
        // пакетный замер в сутки чтения диска.
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

        var options = new DicomImportOptions { PseudonymSalt = Salt };
        var scan = await new DicomStudyScanner(options).ScanAsync(this.source.FullName, CancellationToken.None);

        var importer = this.Importer();
        var wanted = scan.Studies[1];

        var result = await importer.ImportStudyAsync(scan, wanted.PseudonymousStudyId, CancellationToken.None);

        Assert.Equal(wanted.PseudonymousStudyId, result.Study.PseudonymousStudyId);
        Assert.Equal(
            Assert.Single(wanted.Series).PseudonymousSeriesId,
            Assert.Single(result.Study.Series).PseudonymousSeriesId);

        // Файл соседнего исследования в рабочую копию не попал.
        Assert.Single(await ReadAllAsync(importer, result));
    }

    [Fact]
    public async Task A_study_absent_from_the_scan_is_refused()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.source.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var scan = await new DicomStudyScanner(new DicomImportOptions { PseudonymSalt = Salt })
            .ScanAsync(this.source.FullName, CancellationToken.None);

        await Assert.ThrowsAsync<DomainRuleViolationException>(
            () => this.Importer().ImportStudyAsync(scan, "no-such-study", CancellationToken.None));
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

        // Псевдонимы детерминированы при одной соли, а каталог рабочей копии —
        // нет: сеанс у каждого импорта свой, иначе два потребителя делили бы
        // один срок жизни.
        Assert.Equal(
            first.Study.PseudonymousStudyId,
            again.Study.PseudonymousStudyId);

        Assert.Equal(
            first.Study.Series[0].PseudonymousSeriesId,
            again.Study.Series[0].PseudonymousSeriesId);

        Assert.NotEqual(first.VolumeReference, again.VolumeReference);
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
            new WorkingCopyOptions
            {
                RootDirectory = this.workingCopy.FullName,

                // ACL выключен: временный каталог теста живёт в общем
                // расположении, ограничивать его учётной записью незачем.
                RestrictAccessToCurrentUser = false,
            });

    /// <summary>Читает все файлы рабочей копии через её сеанс.</summary>
    private static async Task<IReadOnlyList<DicomFile>> ReadAllAsync(
        StudyImporter importer,
        WorkingCopy workingCopy)
    {
        var session = importer.SessionFor(workingCopy.VolumeReference);
        var files = new List<DicomFile>();

        foreach (var series in workingCopy.Study.Series)
        {
            foreach (var path in session.Enumerate(series.PseudonymousSeriesId))
            {
                var content = await session.ReadAsync(path, CancellationToken.None);

                using var stream = new MemoryStream(content);

                files.Add(await DicomFile.OpenAsync(stream));
            }
        }

        return files;
    }
}
