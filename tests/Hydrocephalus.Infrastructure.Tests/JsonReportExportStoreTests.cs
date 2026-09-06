using System.Text.Json;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Запись экспортированного отчёта.
///
/// Пункт плана проверки ADR 0005: обезличенный экспорт не содержит ни одного
/// идентифицирующего поля — проверка автоматическая, а не ручной осмотр.
/// Файл уходит наружу, и отозвать его нельзя.
/// </summary>
public sealed class JsonReportExportStoreTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly Actor Clinician = Actor.Create("doctor-1", ClinicalRole.Clinician);

    private const string Surname = "Иванов Иван Иванович";
    private const string RecordNumber = "MRN-778899";

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_deidentified_export_contains_no_identifying_value()
    {
        // Поиск идёт по декодированным значениям, а не по сырому файлу.
        // Сериализатор экранирует кириллицу, и проверка по тексту файла
        // не нашла бы фамилию, даже если бы та в нём была: тест выглядел бы
        // пройденным и не проверял ничего.
        var path = await this.Store().WriteAsync(Deidentified(), CancellationToken.None);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        var values = StringValues(document.RootElement).ToArray();

        Assert.DoesNotContain(values, value => value.Contains(Surname, StringComparison.Ordinal));
        Assert.DoesNotContain(values, value => value.Contains(RecordNumber, StringComparison.Ordinal));
        Assert.DoesNotContain(values, value => value.Contains("1955-11-03", StringComparison.Ordinal));

        Assert.DoesNotContain(
            PropertyNames(document.RootElement),
            name => name == JsonReportExportStore.PatientProperty);
    }

    [Fact]
    public async Task The_leak_check_would_notice_a_leak()
    {
        // Обратная проверка: тот же способ поиска на клиническом варианте
        // обязан фамилию найти. Без неё «утечки нет» может означать лишь то,
        // что искать не умеют.
        var path = await this.Store().WriteAsync(Clinical(), CancellationToken.None);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        Assert.Contains(
            StringValues(document.RootElement),
            value => value.Contains(Surname, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clinical_export_carries_the_identifiers_it_is_named_for()
    {
        var path = await this.Store().WriteAsync(Clinical(), CancellationToken.None);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        var patient = document.RootElement.GetProperty(JsonReportExportStore.PatientProperty);

        Assert.Equal(Surname, patient.GetProperty("fullName").GetString());
        Assert.Equal(RecordNumber, patient.GetProperty("medicalRecordNumber").GetString());
        Assert.Equal("1955-11-03", patient.GetProperty("birthDate").GetString());
    }

    [Fact]
    public void Both_variants_state_the_limitations_in_the_file_itself()
    {
        // Файл переживает диалог, в котором его показали: предупреждение
        // печатается в нём, а не только в интерфейсе.
        foreach (var prepared in new[] { Deidentified(), Clinical() })
        {
            using var document = JsonDocument.Parse(JsonReportExportStore.Serialize(prepared));

            Assert.Contains(
                "Не является автономной диагностикой",
                document.RootElement.GetProperty("limitations").GetString(),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Both_variants_carry_the_refusal_and_its_reason()
    {
        // Обязательный элемент обоих вариантов (ADR 0005): факт и причина
        // отказа вместо вероятностей.
        foreach (var prepared in new[] { Deidentified(), Clinical() })
        {
            using var document = JsonDocument.Parse(JsonReportExportStore.Serialize(prepared));

            var outcome = document.RootElement.GetProperty("report").GetProperty("outcome");

            Assert.Equal("refused", outcome.GetProperty("kind").GetString());
            Assert.Equal(
                nameof(RefusalCode.ModelPackageUnusable),
                outcome.GetProperty("refusal").GetProperty("code").GetString());
        }
    }

    [Fact]
    public void Both_variants_carry_the_pipeline_provenance()
    {
        foreach (var prepared in new[] { Deidentified(), Clinical() })
        {
            using var document = JsonDocument.Parse(JsonReportExportStore.Serialize(prepared));

            Assert.Equal(
                new string('a', 40),
                document.RootElement.GetProperty("report")
                    .GetProperty("pipeline")
                    .GetProperty("applicationCommitSha")
                    .GetString());
        }
    }

    [Fact]
    public async Task The_file_name_says_which_variant_it_is()
    {
        // Отличать клинический файл от обезличенного по содержимому поздно:
        // к этому моменту его уже открыли или переслали.
        var deidentified = await this.Store().WriteAsync(Deidentified(), CancellationToken.None);
        var clinical = await this.Store().WriteAsync(Clinical(), CancellationToken.None);

        Assert.EndsWith("-deidentified.json", deidentified, StringComparison.Ordinal);
        Assert.EndsWith("-clinical.json", clinical, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_export_records_who_produced_it()
    {
        var path = await this.Store().WriteAsync(Clinical(), CancellationToken.None);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path, CancellationToken.None));

        Assert.Equal("doctor-1", document.RootElement.GetProperty("exportedBy").GetString());
    }

    [Fact]
    public async Task Two_variants_of_one_study_do_not_collide()
    {
        var store = this.Store();

        await store.WriteAsync(Deidentified(), CancellationToken.None);
        await store.WriteAsync(Clinical(), CancellationToken.None);

        Assert.Equal(
            2,
            Directory.GetFiles(this.root.FullName, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task An_existing_export_is_not_overwritten()
    {
        var store = this.Store();

        await store.WriteAsync(Clinical(), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(
            () => store.WriteAsync(Clinical(), CancellationToken.None));
    }

    /// <summary>Все строковые значения документа, уже декодированные.</summary>
    private static IEnumerable<string> StringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in StringValues(property.Value))
                    {
                        yield return value;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray().SelectMany(StringValues))
                {
                    yield return value;
                }

                break;

            default:
                break;
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            yield return property.Name;

            foreach (var nested in PropertyNames(property.Value))
            {
                yield return nested;
            }
        }
    }

    private JsonReportExportStore Store() => new(this.root.FullName);

    private static ReportExport Deidentified() =>
        ReportExport.Deidentified(Report(), Clinician, Moment);

    private static ReportExport Clinical() =>
        ReportExport.Clinical(
            Report(),
            new PatientIdentity
            {
                FullName = Surname,
                MedicalRecordNumber = RecordNumber,
                BirthDate = new DateOnly(1955, 11, 3),
            },
            Clinician,
            Moment);

    private static AnalysisReport Report() => new()
    {
        PseudonymousStudyId = "study-1",
        CreatedAt = Moment,
        Quality = QualityAssessment.Clean(),
        Outcome = new AnalysisOutcome.Refused
        {
            Reason = new RefusalReason { Code = RefusalCode.ModelPackageUnusable },
        },
        Pipeline = new PipelineIdentity
        {
            PreprocessingVersion = "1.0.0",
            FeatureSchemaVersion = "1.0.0",
            LabelMapVersion = "1.0.0",
            ApplicationCommitSha = new string('a', 40),
        },
    };
}
