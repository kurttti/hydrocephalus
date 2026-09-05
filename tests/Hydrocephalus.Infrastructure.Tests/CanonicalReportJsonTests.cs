using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Канонический JSON-слой отчёта.
///
/// Проверяется то, ради чего он канонический: один и тот же отчёт даёт побайтово
/// один и тот же результат, отказ не превращается в заключение, и переведённых
/// строк в нём нет — только коды.
/// </summary>
public sealed class CanonicalReportJsonTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 3, 14, 9, 26, 53, TimeSpan.FromHours(3));

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_same_report_serializes_byte_for_byte_identically()
    {
        // Golden-тесты сравнивают побайтово; расхождение обязано означать
        // изменение результата, а не перестановку полей или иную локаль.
        var report = RefusedReport();

        Assert.Equal(CanonicalReportJson.Serialize(report), CanonicalReportJson.Serialize(report));
    }

    [Fact]
    public void Parameter_keys_are_ordered_so_two_equal_reports_do_not_differ()
    {
        // Порядок обхода словаря не гарантирован: без сортировки два одинаковых
        // отчёта отличались бы побайтово.
        var first = RefusedReport(parameters: [("valueMm", "12"), ("parameter", "sliceThickness")]);
        var second = RefusedReport(parameters: [("parameter", "sliceThickness"), ("valueMm", "12")]);

        Assert.Equal(
            CanonicalReportJson.SerializeToString(first),
            CanonicalReportJson.SerializeToString(second));
    }

    [Fact]
    public void Timestamps_are_written_in_utc()
    {
        // Локальное время сделало бы отчёт зависящим от машины, на которой он собран.
        var json = Parse(RefusedReport());

        Assert.Equal("2026-03-14T06:26:53.0000000Z", json.GetProperty("createdAt").GetString());
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_no_probabilities()
    {
        // Отказ не должен читаться как отрицательное заключение — ни в домене,
        // ни в файле, который уходит из процесса.
        var json = Parse(RefusedReport());

        var outcome = json.GetProperty("outcome");

        Assert.Equal("refused", outcome.GetProperty("kind").GetString());
        Assert.Equal(
            nameof(RefusalCode.QualityControlFailed),
            outcome.GetProperty("refusal").GetProperty("code").GetString());

        Assert.False(outcome.TryGetProperty("prediction", out _));
    }

    [Fact]
    public void A_completed_analysis_carries_the_model_and_its_probabilities()
    {
        var json = Parse(CompletedReport());

        var prediction = json.GetProperty("outcome").GetProperty("prediction");

        Assert.Equal("completed", json.GetProperty("outcome").GetProperty("kind").GetString());
        Assert.Equal("0.2.0", prediction.GetProperty("model").GetProperty("version").GetString());

        var probabilities = prediction.GetProperty("probabilities").EnumerateArray().ToArray();

        Assert.Equal(2, probabilities.Length);
        Assert.Equal("inph", probabilities[0].GetProperty("class").GetString());
        Assert.Equal(0.72, probabilities[0].GetProperty("value").GetDouble(), precision: 10);
    }

    [Fact]
    public void Provenance_is_always_present()
    {
        // Без версий конвейера результат невоспроизводим, поэтому они пишутся
        // и в отказном отчёте тоже.
        var pipeline = Parse(RefusedReport()).GetProperty("pipeline");

        Assert.Equal("1.0.0", pipeline.GetProperty("preprocessingVersion").GetString());
        Assert.Equal(new string('a', 40), pipeline.GetProperty("applicationCommitSha").GetString());
    }

    [Fact]
    public void Acceptability_is_derived_and_not_copied_from_the_input()
    {
        var json = Parse(RefusedReport());

        Assert.False(json.GetProperty("quality").GetProperty("acceptable").GetBoolean());
    }

    [Fact]
    public void Only_codes_are_stored_and_not_translated_text()
    {
        // Локализация происходит на слое представления: перевод внутри отчёта
        // сделал бы файл непригодным для сравнения версий и разбора ошибок.
        var text = CanonicalReportJson.SerializeToString(RefusedReport());

        Assert.Contains(nameof(QualityIssueCode.UnsupportedVoxelGeometry), text, StringComparison.Ordinal);
        Assert.DoesNotContain("Толщина", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_writes_the_canonical_bytes()
    {
        var report = RefusedReport();
        var store = new JsonReportStore(this.root.FullName);

        await store.StoreAsync(report, CancellationToken.None);

        var path = store.PathFor(report);

        Assert.True(File.Exists(path));
        Assert.Equal(
            CanonicalReportJson.Serialize(report),
            await File.ReadAllBytesAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task Storing_the_same_report_twice_changes_nothing()
    {
        // Перезапись сохранённого отчёта — потеря того, что уже могло уйти врачу.
        // Но повторная запись побайтово того же содержимого ничего не теряет,
        // и объявлять её ошибкой значило бы ломать повторный запуск на ровном месте.
        var report = RefusedReport();
        var store = new JsonReportStore(this.root.FullName);

        await store.StoreAsync(report, CancellationToken.None);
        await store.StoreAsync(report, CancellationToken.None);

        var file = Assert.Single(
            Directory.GetFiles(this.root.FullName, "*.json", SearchOption.AllDirectories));

        Assert.Equal(
            CanonicalReportJson.Serialize(report),
            await File.ReadAllBytesAsync(file, CancellationToken.None));
    }

    [Fact]
    public async Task Two_different_reports_of_one_study_at_one_instant_both_survive()
    {
        // Системные часы Windows идут шагами около 15мс, поэтому два разбора
        // подряд получают одинаковую отметку времени. Имя, зависящее только
        // от неё, потеряло бы второй отчёт.
        var store = new JsonReportStore(this.root.FullName);

        await store.StoreAsync(RefusedReport(), CancellationToken.None);
        await store.StoreAsync(CompletedReport(), CancellationToken.None);

        Assert.Equal(
            2,
            Directory.GetFiles(this.root.FullName, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Two_analyses_of_one_study_do_not_collide()
    {
        var store = new JsonReportStore(this.root.FullName);

        await store.StoreAsync(RefusedReport(), CancellationToken.None);
        await store.StoreAsync(RefusedReport(moment: Moment.AddMinutes(5)), CancellationToken.None);

        Assert.Equal(
            2,
            Directory.GetFiles(this.root.FullName, "*.json", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Report_path_contains_no_source_identifier()
    {
        var store = new JsonReportStore(this.root.FullName);

        var path = store.PathFor(RefusedReport());

        Assert.Contains("study-0001", path, StringComparison.Ordinal);
        Assert.DoesNotContain("Ivanov", path, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Parse(AnalysisReport report) =>
        JsonDocument.Parse(Encoding.UTF8.GetString(CanonicalReportJson.Serialize(report))).RootElement;

    private static PipelineIdentity Pipeline() => new()
    {
        PreprocessingVersion = "1.0.0",
        FeatureSchemaVersion = "1.0.0",
        LabelMapVersion = "1.0.0",
        ApplicationCommitSha = new string('a', 40),
    };

    private static AnalysisReport RefusedReport(
        (string Key, string Value)[]? parameters = null,
        DateTimeOffset? moment = null)
    {
        var issue = new QualityIssue
        {
            Code = QualityIssueCode.UnsupportedVoxelGeometry,
            Severity = QualityIssueSeverity.Blocking,
            Parameters = (parameters ?? [("parameter", "sliceThickness"), ("valueMm", "12")])
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
        };

        var quality = new QualityAssessment { Issues = [issue] };

        return new AnalysisReport
        {
            PseudonymousStudyId = "study-0001",
            CreatedAt = moment ?? Moment,
            Quality = quality,
            Outcome = new AnalysisOutcome.Refused
            {
                Reason = RefusalReason.FromFailedQualityControl(quality),
            },
            Pipeline = Pipeline(),
        };
    }

    private static AnalysisReport CompletedReport()
    {
        var inph = new DiagnosticClass("inph");
        var other = new DiagnosticClass("non_inph");

        var model = new ModelIdentity
        {
            Name = "hydrocephalus-classifier",
            Version = "0.2.0",
            PackageSha256 = new string('0', 64),
            SupportedClasses = [inph, other],
            CalibrationVersion = "1.0.0",
        };

        var quality = QualityAssessment.Clean();

        var prediction = Prediction.Create(
            quality,
            model,
            [new ClassProbability(inph, 0.72), new ClassProbability(other, 0.28)],
            new Uncertainty { LowerBound = 0.6, UpperBound = 0.84, ConfidenceLevel = 0.95 });

        return new AnalysisReport
        {
            PseudonymousStudyId = "study-0001",
            CreatedAt = Moment,
            Quality = quality,
            Outcome = new AnalysisOutcome.Completed { Prediction = prediction },
            Pipeline = Pipeline(),
        };
    }
}
