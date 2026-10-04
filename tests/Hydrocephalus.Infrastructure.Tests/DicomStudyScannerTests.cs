using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Контрактные тесты импорта. Случаи взяты не из воображения, а из аудита реальной
/// выборки, описанного в docs/data/README.md: разнородная структура папок, отсутствие
/// идентифицирующих полей и недостоверные значения технических тегов.
/// </summary>
public sealed class DicomStudyScannerTests : IDisposable
{
    private const string Salt = "test-salt-not-a-secret";

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    private static DicomImportOptions Options() => new() { PseudonymSalt = Salt };

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Flat_and_nested_layouts_group_identically_by_series_uid()
    {
        // Один и тот же набор срезов, разложенный двумя способами: плоским списком
        // и вложенной структурой PA*/ST*/SE*. Группировка идёт по SeriesInstanceUID,
        // поэтому результат обязан совпасть.
        const string StudyUid = "1.2.3.100";
        const string SeriesUid = "1.2.3.200";

        var flat = this.root.CreateSubdirectory("flat");
        var nested = this.root.CreateSubdirectory("nested");

        for (var index = 0; index < 3; index++)
        {
            SyntheticDicom.WriteSlice(
                Path.Combine(flat.FullName, $"IM{index}.dcm"),
                StudyUid,
                SeriesUid,
                patientId: "P-1",
                slicePosition: index);

            SyntheticDicom.WriteSlice(
                Path.Combine(nested.FullName, "PA0", "ST0", "SE0", $"IM{index}.dcm"),
                StudyUid,
                SeriesUid,
                patientId: "P-1",
                slicePosition: index);
        }

        var scanner = new DicomStudyScanner(Options());

        var flatResult = await scanner.ScanAsync(flat.FullName, CancellationToken.None);
        var nestedResult = await scanner.ScanAsync(nested.FullName, CancellationToken.None);

        var flatStudy = Assert.Single(flatResult.Studies);
        var nestedStudy = Assert.Single(nestedResult.Studies);

        Assert.Equal(flatStudy.PseudonymousStudyId, nestedStudy.PseudonymousStudyId);
        Assert.Equal(flatStudy.PseudonymousSubjectId, nestedStudy.PseudonymousSubjectId);

        var flatSeries = Assert.Single(flatStudy.Series);
        var nestedSeries = Assert.Single(nestedStudy.Series);

        Assert.Equal(flatSeries.PseudonymousSeriesId, nestedSeries.PseudonymousSeriesId);
        Assert.Equal(3, flatSeries.Geometry.Dimensions.Slices);
        Assert.Equal(3, nestedSeries.Geometry.Dimensions.Slices);
    }

    [Fact]
    public async Task Records_without_any_identifying_field_never_merge_into_one_subject()
    {
        // Найдено на реальных данных: примерно у половины файлов PatientID, PatientName
        // и PatientBirthDate пусты одновременно. Наивное хеширование пустой строки
        // склеило бы разных пациентов в одного и разрушило patient-level split.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11");

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "b.dcm"),
            studyUid: "1.2.3.2",
            seriesUid: "1.2.3.22");

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var subjects = result.Studies.Select(study => study.PseudonymousSubjectId).ToArray();

        Assert.Equal(2, subjects.Length);
        Assert.Equal(2, subjects.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Records_sharing_one_identifying_field_belong_to_the_same_subject()
    {
        // Обратная сторона правила: недо-объединение допустимо, но при совпадающем
        // непустом идентификаторе записи обязаны сойтись в одного пациента.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-42");

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "b.dcm"),
            studyUid: "1.2.3.2",
            seriesUid: "1.2.3.22",
            patientId: "P-42");

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(2, result.Studies.Count);
        Assert.Single(result.Studies.Select(study => study.PseudonymousSubjectId).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Implausible_field_strength_is_flagged_rather_than_used()
    {
        // Встречалось на реальных данных: значение, завышенное на порядки
        // из-за путаницы единиц у отдельных производителей.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1",
            fieldStrength: 15000m);

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var finding = Assert.Single(result.Findings);

        Assert.Equal(QualityIssueCode.ImplausibleMetadata, finding.Issue.Code);
        Assert.Equal("MagneticFieldStrength", finding.Issue.Parameters["tag"]);
    }

    [Fact]
    public async Task Plausible_field_strength_produces_no_finding()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1",
            fieldStrength: 3.0m);

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task Non_dicom_files_are_rejected_with_a_code_and_do_not_stop_the_import()
    {
        // В одном из источников клинические документы лежали вперемешку с папками снимков.
        await File.WriteAllTextAsync(
            Path.Combine(this.root.FullName, "epicrisis.txt"),
            "не DICOM",
            CancellationToken.None);

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Single(result.Studies);
        Assert.Equal(ImportRejectionCode.NotADicomFile, Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public async Task Presentation_states_are_rejected_and_never_become_a_series()
    {
        // Найдено на реальных данных: в двух папках выборки рядом со срезами лежат
        // объекты Grayscale Softcopy Presentation State — настройки просмотра, без
        // пикселей. Каждый в своей серии, поэтому без проверки они пополняли
        // исследование сериями размером 0 на 0.
        const string StudyUid = "1.2.3.300";

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "IM0.dcm"),
            StudyUid,
            seriesUid: "1.2.3.301",
            patientId: "P-1");

        SyntheticDicom.WritePresentationState(
            Path.Combine(this.root.FullName, "PR0.dcm"),
            StudyUid,
            seriesUid: "1.2.3.302",
            patientId: "P-1");

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(ImportRejectionCode.NotAnImage, Assert.Single(result.Rejections).Code);

        var series = Assert.Single(Assert.Single(result.Studies).Series);

        Assert.Equal(256, series.Geometry.Dimensions.Columns);
        Assert.Equal(1, series.Geometry.Dimensions.Slices);
    }

    [Fact]
    public async Task A_presentation_state_inside_an_image_series_does_not_replace_its_geometry()
    {
        // В выборке служебные объекты лежат в отдельных сериях, но опираться на это
        // нельзя: стандарт не запрещает общий SeriesInstanceUID. Геометрия и
        // взвешенность серии берутся из первого отданного обходом файла, поэтому
        // служебный объект, пришедший первым, обнулил бы объёмную T1 без следа.
        //
        // Служебный объект обязан прийти первым, иначе случай не проверяется, а
        // порядок имён такой гарантии не даёт: обход зовёт DirectoryInfo.GetFiles,
        // и её порядок зависит от файловой системы. Гарантия берётся из устройства
        // обхода: он идёт в ширину, поэтому файлы корня выдаются раньше файлов
        // подкаталога. Служебный объект — в корне, срезы — уровнем ниже.
        const string StudyUid = "1.2.3.400";
        const string SeriesUid = "1.2.3.401";

        SyntheticDicom.WritePresentationState(
            Path.Combine(this.root.FullName, "state.dcm"),
            StudyUid,
            SeriesUid,
            patientId: "P-1");

        for (var index = 1; index <= 3; index++)
        {
            SyntheticDicom.WriteSlice(
                Path.Combine(this.root.FullName, "slices", $"IM{index}.dcm"),
                StudyUid,
                SeriesUid,
                patientId: "P-1",
                slicePosition: index);
        }

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var series = Assert.Single(Assert.Single(result.Studies).Series);

        Assert.Equal(SeriesWeighting.T1, series.Weighting);
        Assert.Equal(256, series.Geometry.Dimensions.Columns);
        Assert.Equal(256, series.Geometry.Dimensions.Rows);
        Assert.Equal(3, series.Geometry.Dimensions.Slices);
        Assert.Equal(1.0, series.Geometry.PixelSpacing.RowMillimetres);
    }

    [Fact]
    public async Task Computed_tomography_is_refused_with_a_named_reason()
    {
        // Решение владельца данных от 2026-09-28: ориентир только на МРТ. Прежде
        // КТ проходила молча — распознавание взвешенности читает теги импульсной
        // последовательности, которых у КТ нет, серия получала Unknown, и папка
        // выглядела принятой при отсутствующем измерении.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "CT0.dcm"),
            studyUid: "1.2.3.500",
            seriesUid: "1.2.3.501",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.Modality, "CT"));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Studies);
        Assert.Equal(ImportRejectionCode.NotMagneticResonance, Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public async Task A_study_of_another_body_part_is_refused_with_a_named_reason()
    {
        // Цель — головной мозг. Прежде абдоминальная МРТ принималась как
        // Baseline/T1 и отвергалась лишь сегментацией, с причиной «желудочки
        // неправдоподобно малы»: формально верной, по существу вводящей в
        // заблуждение, потому что желудочков в животе нет.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "AB0.dcm"),
            studyUid: "1.2.3.600",
            seriesUid: "1.2.3.601",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.BodyPartExamined, "ABDOMEN"));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Studies);
        Assert.Equal(ImportRejectionCode.NotHead, Assert.Single(result.Rejections).Code);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("BRAIN")]
    [InlineData("HEAD ")]
    [InlineData("")]
    public async Task A_head_study_is_accepted_however_the_body_part_is_spelled(string part)
    {
        // Пустое значение принимается намеренно: в наборе тег пуст у 312 серий
        // из 385. Дополняющий пробел VR CS значения не меняет.
        //
        // Строчное написание не проверяется: VR CS допускает только прописные,
        // и такое значение в DICOM не записать. Сравнение в коде всё равно идёт
        // без учёта регистра — на случай снимка, собранного в обход стандарта.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "HD0.dcm"),
            studyUid: "1.2.3.610",
            seriesUid: "1.2.3.611",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.BodyPartExamined, part));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Rejections);
        Assert.Single(result.Studies);
    }

    [Fact]
    public async Task A_slice_without_a_declared_modality_is_still_accepted()
    {
        // Снисходительность намеренная: пустой тег означает неполный экспорт, а не
        // другой аппарат. КТ всегда объявляет себя, поэтому пропустить её это не
        // даёт, а снимок МРТ с неполными тегами сохраняет.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "IM0.dcm"),
            studyUid: "1.2.3.600",
            seriesUid: "1.2.3.601",
            patientId: "P-1",
            customize: dataset => dataset.Remove(DicomTag.Modality));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Rejections);
        Assert.Single(Assert.Single(result.Studies).Series);
    }

    [Fact]
    public async Task A_folder_holding_both_modalities_keeps_only_the_magnetic_resonance()
    {
        // Смешанная папка не отвергается целиком: в выборке КТ и МРТ одного
        // пациента встречаются рядом, и отказ по одной серии не повод терять другую.
        const string StudyUid = "1.2.3.700";

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "IM0.dcm"),
            StudyUid,
            seriesUid: "1.2.3.701",
            patientId: "P-1");

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "CT0.dcm"),
            StudyUid,
            seriesUid: "1.2.3.702",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.Modality, "CT"));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(ImportRejectionCode.NotMagneticResonance, Assert.Single(result.Rejections).Code);

        var series = Assert.Single(Assert.Single(result.Studies).Series);

        Assert.Equal(SeriesWeighting.T1, series.Weighting);
    }

    [Fact]
    public async Task The_refusal_names_why_the_folder_did_not_fit()
    {
        // Без причин папка компьютерной томографии отвергается теми же словами, что
        // папка без снимков вообще, и «не тот аппарат» не отличить от «здесь ничего
        // нет». Коды и числа — можно, пути — нет: в них фамилии.
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "CT0.dcm"),
            studyUid: "1.2.3.800",
            seriesUid: "1.2.3.801",
            patientId: "P-1",
            customize: dataset => dataset.AddOrUpdate(DicomTag.Modality, "CT"));

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);
        var message = StudyImporter.NoReadableStudyMessage(result);

        Assert.Contains("NotMagneticResonance 1", message, StringComparison.Ordinal);
        Assert.DoesNotContain("CT0", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_does_not_expose_the_file_path()
    {
        // Имена файлов в исходном сборе содержат фамилии пациентов, поэтому путь
        // не должен попадать в результат импорта и далее в журнал.
        const string SurnameLikeName = "Ivanov-diagnosis.txt";

        await File.WriteAllTextAsync(
            Path.Combine(this.root.FullName, SurnameLikeName),
            "не DICOM",
            CancellationToken.None);

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var rejection = Assert.Single(result.Rejections);

        Assert.DoesNotContain("Ivanov", rejection.OpaqueFileReference, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".txt", rejection.OpaqueFileReference, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Files_over_the_size_limit_are_rejected_with_a_code()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var options = new DicomImportOptions { PseudonymSalt = Salt, MaxFileSizeBytes = 16 };

        var result = await new DicomStudyScanner(options).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Studies);
        Assert.Equal(ImportRejectionCode.FileTooLarge, Assert.Single(result.Rejections).Code);
    }

    [Fact]
    public async Task Directories_nested_past_the_limit_are_rejected_with_a_code()
    {
        var deep = Path.Combine(this.root.FullName, "a", "b", "c", "d");

        SyntheticDicom.WriteSlice(
            Path.Combine(deep, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var options = new DicomImportOptions { PseudonymSalt = Salt, MaxDirectoryDepth = 2 };

        var result = await new DicomStudyScanner(options).ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Empty(result.Studies);
        Assert.Contains(result.Rejections, item => item.Code == ImportRejectionCode.DirectoryTooDeep);
    }

    [Fact]
    public async Task Geometry_drives_the_acquisition_tier()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "thin.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1",
            acquisitionType: "3D",
            sliceThickness: 1.0m);

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "thick.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.12",
            patientId: "P-1",
            acquisitionType: "2D",
            sliceThickness: 5.0m);

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var study = Assert.Single(result.Studies);
        var tiers = study.Series.Select(series => series.Tier).ToArray();

        Assert.Contains(AcquisitionTier.Extended, tiers);
        Assert.Contains(AcquisitionTier.Baseline, tiers);
        Assert.Equal(AcquisitionTier.Extended, study.BestAvailableTier);
    }

    [Fact]
    public async Task Pseudonymous_ids_are_stable_for_one_salt_and_differ_across_salts()
    {
        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "P-1");

        var first = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);
        var again = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);

        var other = await new DicomStudyScanner(new DicomImportOptions { PseudonymSalt = "another-salt" })
            .ScanAsync(this.root.FullName, CancellationToken.None);

        Assert.Equal(
            first.Studies[0].PseudonymousSubjectId,
            again.Studies[0].PseudonymousSubjectId);

        Assert.NotEqual(
            first.Studies[0].PseudonymousSubjectId,
            other.Studies[0].PseudonymousSubjectId);
    }

    [Fact]
    public async Task Raw_identifiers_do_not_survive_into_the_result()
    {
        const string RawStudyUid = "1.2.826.0.1.3680043.9.7133.1.1";

        SyntheticDicom.WriteSlice(
            Path.Combine(this.root.FullName, "a.dcm"),
            studyUid: RawStudyUid,
            seriesUid: "1.2.3.11",
            patientId: "P-SECRET");

        var result = await new DicomStudyScanner(Options()).ScanAsync(this.root.FullName, CancellationToken.None);
        var study = Assert.Single(result.Studies);

        Assert.NotEqual(RawStudyUid, study.PseudonymousStudyId);
        Assert.DoesNotContain("SECRET", study.PseudonymousSubjectId, StringComparison.OrdinalIgnoreCase);
    }
}
