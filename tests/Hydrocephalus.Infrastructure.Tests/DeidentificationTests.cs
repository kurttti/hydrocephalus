using System.Globalization;
using System.Text.RegularExpressions;
using FellowOakDicom;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Деидентификация набора тегов по профилю ADR 0003.
///
/// Главная проверка здесь — не «такие-то теги удалены», а «ни одно исходное
/// значение не встречается в результате». Проверка по списку тегов проходит и
/// тогда, когда то же значение уцелело в соседнем теге или во вложенной
/// последовательности, то есть описывает намерение, а не гарантию.
/// </summary>
public sealed class DeidentificationTests
{
    private const string Salt = "test-salt-not-a-secret";
    private const string Subject = "pseudonymous-subject-1";
    private const string Surname = "Ivanov^Ivan^Ivanovich";

    private static DicomImportOptions Options(string salt = Salt) =>
        new() { PseudonymSalt = salt };

    private static DicomDeidentifier Deidentifier(string salt = Salt) =>
        new(Options(salt));

    [Fact]
    public void No_source_value_survives_anywhere_in_the_result()
    {
        // Идентификаторы разложены по разным местам, включая вложенную
        // последовательность: именно так они и прячутся в реальных файлах.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "MRN-778899",
            patientName: Surname,
            birthDate: "19551103",
            customize: dataset =>
            {
                dataset.AddOrUpdate(DicomTag.InstitutionName, "Городская больница №7");
                dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, "Petrov^Petr");
                dataset.AddOrUpdate(DicomTag.AccessionNumber, "ACC-4477");
                dataset.AddOrUpdate(DicomTag.StationName, "MR-ROOM-2");
                dataset.Add(new DicomSequence(
                    DicomTag.RequestAttributesSequence,
                    new DicomDataset { { DicomTag.RequestedProcedureID, "RP-Ivanov-1" } }));
            });

        var result = Deidentifier().Deidentify(source, Subject);

        var violations = DeidentificationAudit.Inspect(
            result.Dataset,
            fileMetaInfo: null,
            result.SourceSecrets,
            relativePath: "series/instance.dcm");

        Assert.Empty(violations);

        var values = SyntheticDicom.AllStringValues(result.Dataset).ToArray();

        Assert.DoesNotContain(values, value => value.Contains("Ivanov", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(values, value => value.Contains("778899", StringComparison.Ordinal));
        Assert.DoesNotContain(values, value => value.Contains("19551103", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_reports_the_source_dataset_it_was_built_from()
    {
        // Обратная проверка: без неё «нарушений нет» может означать лишь то,
        // что проверка ничего не ищет.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientId: "MRN-778899",
            patientName: Surname);

        var result = Deidentifier().Deidentify(source, Subject);

        var violations = DeidentificationAudit.Inspect(
            source,
            fileMetaInfo: null,
            result.SourceSecrets,
            relativePath: "series/instance.dcm");

        Assert.NotEmpty(violations);
        Assert.Contains(
            violations,
            violation => violation.Code == DeidentificationViolationCode.ResidualIdentifyingTag);
    }

    [Fact]
    public void Audit_reports_a_source_value_left_in_the_path()
    {
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientName: Surname);

        var result = Deidentifier().Deidentify(source, Subject);

        var violations = DeidentificationAudit.Inspect(
            result.Dataset,
            fileMetaInfo: null,
            result.SourceSecrets,
            relativePath: Path.Combine("series", Surname + ".dcm"));

        Assert.Contains(
            violations,
            violation => violation.Code == DeidentificationViolationCode.ResidualSourceValueInPath);
    }

    [Fact]
    public void Private_and_overlay_tags_are_removed()
    {
        // Приватные теги удаляются по умолчанию (ADR 0003). Оверлейный слой —
        // такой же носитель вписанного текста, как и пиксели, а тег
        // BurnedInAnnotation его не описывает.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            customize: dataset =>
            {
                dataset.Add(new DicomLongString(new DicomTag(0x0009, 0x1001), "SIEMENS PRIVATE"));
                dataset.Add(new DicomLongString(new DicomTag(0x6000, 0x0022), "OVERLAY TEXT"));
            });

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.DoesNotContain(result.Dataset, item => (item.Tag.Group & 1) == 1);
        Assert.DoesNotContain(result.Dataset, item => item.Tag.Group == 0x6000);
    }

    [Fact]
    public void Original_attributes_sequence_is_removed()
    {
        // OriginalAttributesSequence хранит значения до предыдущей модификации,
        // то есть может нести исходные идентификаторы дословно.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            customize: dataset => dataset.Add(new DicomSequence(
                new DicomTag(0x0400, 0x0561),
                new DicomDataset { { DicomTag.PatientName, Surname } })));

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.False(result.Dataset.Contains(new DicomTag(0x0400, 0x0561)));
        Assert.DoesNotContain(
            SyntheticDicom.AllStringValues(result.Dataset),
            value => value.Contains("Ivanov", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Device_identity_is_retained()
    {
        // Производитель, модель и напряжённость поля нужны подгрупповой
        // отчётности (docs/validation/README.md) и сохраняются по ADR 0003.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            fieldStrength: 3.0m,
            customize: dataset =>
            {
                dataset.AddOrUpdate(DicomTag.Manufacturer, "SIEMENS");
                dataset.AddOrUpdate(DicomTag.ManufacturerModelName, "Skyra");
            });

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.Equal("SIEMENS", result.Dataset.GetSingleValue<string>(DicomTag.Manufacturer));
        Assert.Equal("Skyra", result.Dataset.GetSingleValue<string>(DicomTag.ManufacturerModelName));
        Assert.Equal(3.0m, result.Dataset.GetSingleValue<decimal>(DicomTag.MagneticFieldStrength));
    }

    [Fact]
    public void Device_serial_number_and_station_name_are_removed()
    {
        // ADR 0003 называет опцию Retain Device Identity, но перечисляет ровно
        // три атрибута. Реализован перечень: серийный номер и имя станции
        // связывают серию с конкретным рабочим местом.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            customize: dataset =>
            {
                dataset.AddOrUpdate(DicomTag.DeviceSerialNumber, "SN-12345");
                dataset.AddOrUpdate(DicomTag.StationName, "MR-ROOM-2");
            });

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.False(result.Dataset.Contains(DicomTag.DeviceSerialNumber));
        Assert.False(result.Dataset.Contains(DicomTag.StationName));
    }

    [Fact]
    public void Intervals_between_studies_of_one_subject_are_preserved()
    {
        // Retain Longitudinal Temporal Information: пред/послеоперационное
        // сравнение опирается на интервал между исследованиями, а не на дату.
        var first = Deidentifier().Deidentify(
            SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11", studyDate: "20240115"),
            Subject);

        var second = Deidentifier().Deidentify(
            SyntheticDicom.BuildSlice("1.2.3.2", "1.2.3.22", studyDate: "20240424"),
            Subject);

        var firstDate = ReadDate(first.Dataset, DicomTag.StudyDate);
        var secondDate = ReadDate(second.Dataset, DicomTag.StudyDate);

        Assert.Equal(100, (secondDate - firstDate).Days);
        Assert.NotEqual(new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Unspecified), firstDate);
    }

    [Fact]
    public void Every_date_element_is_shifted_by_the_same_offset()
    {
        // Сдвинуть StudyDate и оставить SeriesDate или AcquisitionDateTime —
        // значит раскрыть настоящую дату исследования.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            studyDate: "20240115",
            customize: dataset =>
            {
                dataset.AddOrUpdate(DicomTag.SeriesDate, "20240115");
                dataset.AddOrUpdate(DicomTag.ContentDate, "20240115");
                dataset.AddOrUpdate(DicomTag.AcquisitionDateTime, "20240115103000.000000");
            });

        var result = Deidentifier().Deidentify(source, Subject);

        var studyDate = result.Dataset.GetSingleValue<string>(DicomTag.StudyDate);

        Assert.Equal(studyDate, result.Dataset.GetSingleValue<string>(DicomTag.SeriesDate));
        Assert.Equal(studyDate, result.Dataset.GetSingleValue<string>(DicomTag.ContentDate));

        var acquisition = result.Dataset.GetSingleValue<string>(DicomTag.AcquisitionDateTime);

        Assert.StartsWith(studyDate, acquisition, StringComparison.Ordinal);

        // Время суток сохраняется: сдвиг всегда кратен суткам.
        Assert.Contains("103000", acquisition, StringComparison.Ordinal);
    }

    [Fact]
    public void Different_subjects_get_different_date_shifts()
    {
        var first = Deidentifier().Deidentify(
            SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11", studyDate: "20240115"),
            "subject-a");

        var second = Deidentifier().Deidentify(
            SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11", studyDate: "20240115"),
            "subject-b");

        Assert.NotEqual(
            first.Dataset.GetSingleValue<string>(DicomTag.StudyDate),
            second.Dataset.GetSingleValue<string>(DicomTag.StudyDate));
    }

    [Fact]
    public void Replacement_uids_are_syntactically_valid()
    {
        // Псевдоним в шестнадцатеричном виде записать в поле с VR UI нельзя:
        // UID состоит только из цифр и точек и ограничен 64 символами.
        var source = SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11");

        var result = Deidentifier().Deidentify(source, Subject);

        foreach (var tag in new[]
        {
            DicomTag.StudyInstanceUID,
            DicomTag.SeriesInstanceUID,
            DicomTag.SOPInstanceUID,
        })
        {
            var value = result.Dataset.GetSingleValue<string>(tag);

            Assert.Matches(new Regex(@"^2\.25\.[1-9][0-9]*$", RegexOptions.None, TimeSpan.FromSeconds(1)), value);
            Assert.True(value.Length <= 64, "UID must not exceed 64 characters.");
        }
    }

    [Fact]
    public void Class_and_transfer_syntax_uids_are_preserved()
    {
        // Замена SOPClassUID сделала бы файл нечитаемым и ничего не скрыла.
        var source = SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11");

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.Equal(
            SyntheticDicom.MrSopClassUid,
            result.Dataset.GetSingleValue<string>(DicomTag.SOPClassUID));
    }

    [Fact]
    public void Uid_replacement_keeps_references_inside_the_study_consistent()
    {
        // Исходные UID прячутся во вложенных последовательностях. Замена только
        // верхнего уровня и оставила бы утечку, и разорвала бы ссылку.
        const string InstanceUid = "1.2.826.0.1.3680043.9.7133.1.1";

        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            sopInstanceUid: InstanceUid,
            customize: dataset => dataset.Add(new DicomSequence(
                DicomTag.ReferencedImageSequence,
                new DicomDataset
                {
                    { DicomTag.ReferencedSOPClassUID, SyntheticDicom.MrSopClassUid },
                    { DicomTag.ReferencedSOPInstanceUID, InstanceUid },
                })));

        var result = Deidentifier().Deidentify(source, Subject);

        var replaced = result.Dataset.GetSingleValue<string>(DicomTag.SOPInstanceUID);

        var referenced = result.Dataset
            .GetSequence(DicomTag.ReferencedImageSequence)
            .Items[0]
            .GetSingleValue<string>(DicomTag.ReferencedSOPInstanceUID);

        Assert.NotEqual(InstanceUid, replaced);
        Assert.Equal(replaced, referenced);

        // Класс SOP во вложенной последовательности замещаться не должен.
        Assert.Equal(
            SyntheticDicom.MrSopClassUid,
            result.Dataset
                .GetSequence(DicomTag.ReferencedImageSequence)
                .Items[0]
                .GetSingleValue<string>(DicomTag.ReferencedSOPClassUID));
    }

    [Fact]
    public void Replacement_is_stable_for_one_salt_and_differs_across_salts()
    {
        var source = SyntheticDicom.BuildSlice("1.2.3.1", "1.2.3.11");

        var first = Deidentifier().Deidentify(source, Subject);
        var again = Deidentifier().Deidentify(source, Subject);
        var other = Deidentifier("another-salt").Deidentify(source, Subject);

        Assert.Equal(
            first.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
            again.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID));

        Assert.NotEqual(
            first.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID),
            other.Dataset.GetSingleValue<string>(DicomTag.StudyInstanceUID));
    }

    [Fact]
    public void Free_text_series_description_does_not_reach_the_working_copy()
    {
        // Взвешенность и постконтрастность определяются на исходном наборе тегов
        // до деидентификации, а результат живёт в доменной модели, поэтому
        // свободный текст производителя не обязан попадать в рабочую копию.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            seriesDescription: "T1 MPRAGE Ivanov");

        var result = Deidentifier().Deidentify(source, Subject);

        Assert.False(result.Dataset.Contains(DicomTag.SeriesDescription));
    }

    [Fact]
    public void Source_dataset_is_not_modified()
    {
        // Очистка идёт по копии: исходный набор тегов ещё нужен разбору,
        // и порядок «сначала очистить, потом писать» опирается на это.
        var source = SyntheticDicom.BuildSlice(
            studyUid: "1.2.3.1",
            seriesUid: "1.2.3.11",
            patientName: Surname);

        _ = Deidentifier().Deidentify(source, Subject);

        Assert.Equal(Surname, source.GetSingleValue<string>(DicomTag.PatientName));
    }

    private static DateTime ReadDate(DicomDataset dataset, DicomTag tag) =>
        DateTime.ParseExact(
            dataset.GetSingleValue<string>(tag),
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);
}
