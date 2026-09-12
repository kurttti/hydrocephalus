using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Hydrocephalus.Application;
using Hydrocephalus.BatchMeasure;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Reporting;
using Hydrocephalus.Infrastructure.Volumes;

// Пакетный замер ретроспективной выборки тем же конвейером, что и приложение.
//
// Запускает его владелец данных на своей машине. Результат — измерения,
// привязанные к псевдонимам пациентов, то есть клинический набор данных:
// он пишется только в указанный каталог вне репозитория, а в консоль уходят
// одни агрегаты — числа исходов, причин отказа и исключений. Ни путей,
// ни псевдонимов, ни значений измерений консоль не видит: её вывод
// оказывается в журналах сборки и в переписке.
//
// Каждое исследование проходит через AnalyzeStudyUseCase.ExecuteAsync — тот же
// путь, который создаёт рабочую копию и уничтожает её в finally. Отдельный цикл
// «импортировать и измерить» стал бы единственным местом в коде, где рабочая
// копия может пережить сбой (ADR 0006).
const int UsageError = 2;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("Использование: Hydrocephalus.BatchMeasure --out <каталог вне репозитория> <источник> [<источник>...]");
    Console.WriteLine();
    Console.WriteLine("Замеряет каждое уникальное исследование источников конвейером приложения.");
    Console.WriteLine("В каталог вывода пишутся results.jsonl, отчёты и журнал аудита прогона.");
    Console.WriteLine("В консоль выводятся только агрегаты.");
    Console.WriteLine("--dry-run  разобрать источники и показать план, ничего не импортируя.");
    return args.Length == 0 ? UsageError : 0;
}

var outIndex = Array.IndexOf(args, "--out");

if (outIndex < 0 || outIndex == args.Length - 1)
{
    Console.Error.WriteLine("Не задан --out.");
    return UsageError;
}

var output = Path.GetFullPath(args[outIndex + 1]);
var dryRun = args.Contains("--dry-run");

var sources = args
    .Where((_, index) => index != outIndex && index != outIndex + 1)
    .Where(arg => !arg.StartsWith("--", StringComparison.Ordinal))
    .ToArray();

if (sources.Length == 0)
{
    Console.Error.WriteLine("Не задано ни одного источника.");
    return UsageError;
}

if (BatchPlan.IsInsideRepository(output))
{
    // Путь не печатается: он может лежать рядом с папками выборки.
    Console.Error.WriteLine("Каталог вывода находится внутри репозитория. Измерения пациентов в git не пишутся.");
    return UsageError;
}

if (sources.Any(BatchPlan.IsExcludedSource))
{
    Console.Error.WriteLine($"Источник «{BatchPlan.ExcludedSourceName}» исключён владельцем данных (docs/data/README.md).");
    return UsageError;
}

var missing = sources.Count(path => !Directory.Exists(path));

if (missing > 0)
{
    // Пути не печатаются: они называют папки с фамилиями.
    Console.Error.WriteLine($"Не найдено источников: {missing}.");
    return UsageError;
}

var applicationRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Hydrocephalus");

// Соль установочная: псевдонимы прогона совпадают с теми, что создаёт
// приложение и инвентаризация, и результаты можно сопоставить.
var salt = await PseudonymSaltStore.GetOrCreateAsync(
    Path.Combine(applicationRoot, "secrets", "pseudonym-salt.bin"),
    CancellationToken.None);

var importOptions = new DicomImportOptions
{
    PseudonymSalt = salt,

    // Лимит по умолчанию рассчитан на одно исследование, а здесь обходится
    // папка на много пациентов.
    MaxFileCount = 1_000_000,
};

var watch = Stopwatch.StartNew();
var scans = new List<DicomScanResult>();

for (var index = 0; index < sources.Length; index++)
{
    Console.WriteLine($"Разбор источника {index + 1} из {sources.Length}…");
    scans.Add(await new DicomStudyScanner(importOptions).ScanAsync(sources[index], CancellationToken.None));
}

var plan = BatchPlan.ChooseOccurrences(scans);

Console.WriteLine($"Уникальных исследований: {plan.Count}; в нескольких источниках: {plan.Count(item => item.Occurrences > 1)}.");
Console.WriteLine($"Разбор занял {watch.Elapsed.TotalMinutes:0.0} мин.");

if (dryRun)
{
    return 0;
}

Directory.CreateDirectory(output);

// Рабочие копии — в обычном каталоге приложения, под ACL текущей учётной
// записи: если прогон оборвётся, их уберёт та же уборка, что и за приложением.
var importer = new StudyImporter(
    importOptions,
    new WorkingCopyOptions { RootDirectory = Path.Combine(applicationRoot, "working-copies") });

var pipeline = new PipelineIdentity
{
    PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
    FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
    LabelMapVersion = PipelineIdentity.NotImplementedVersion,
    ApplicationCommitSha = BuildProvenance.CommitShaOf(Assembly.GetExecutingAssembly()),
};

using var auditLog = new HashChainAuditLog(Path.Combine(output, "audit", "audit.log"));

// Прогон ведётся от имени исследователя: сбор измерений по выборке — работа
// с выборкой, а не лечебная.
var actor = Actor.Create("batch-measure", ClinicalRole.Researcher);

var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
var failures = new Dictionary<string, int>(StringComparer.Ordinal);
var qualities = new Dictionary<string, int>(StringComparer.Ordinal);
var issues = new Dictionary<string, int>(StringComparer.Ordinal);
var measured = 0;
var outOfRange = 0;
var durations = new List<double>();

await using var results = new StreamWriter(Path.Combine(output, "results.jsonl"), append: false);

watch.Restart();

for (var index = 0; index < plan.Count; index++)
{
    var planned = plan[index];
    var adapter = new ScannedStudyImporter(importer, planned.Scan);

    var useCase = new AnalyzeStudyUseCase(
        adapter,
        importer,
        new BaselineMeasurementEngine(new InputQualityControl(), new WorkingCopyVolumeSource(importer), pipeline),
        new JsonReportStore(Path.Combine(output, "reports")),
        auditLog,
        TimeProvider.System);

    var studyWatch = Stopwatch.StartNew();
    var record = new Dictionary<string, object?>
    {
        ["pseudonymousStudyId"] = planned.Study.PseudonymousStudyId,
        ["pseudonymousSubjectId"] = planned.Study.PseudonymousSubjectId,
        ["occurrences"] = planned.Occurrences,
    };

    try
    {
        var report = await useCase.ExecuteAsync(
            planned.Study.PseudonymousStudyId,
            actor,
            progress: null,
            CancellationToken.None);

        var outcome = report.Outcome switch
        {
            AnalysisOutcome.Refused refused => "Refused/" + refused.Reason,
            AnalysisOutcome.Completed => "Completed",
            _ => "Unknown",
        };

        Count(outcomes, outcome);

        record["outcome"] = outcome;
        record["qualityIssues"] = report.Quality.Issues.Select(issue => issue.Code.ToString()).ToArray();

        foreach (var issue in report.Quality.Issues)
        {
            Count(issues, issue.Code + "/" + issue.Severity);
        }

        var analysed = adapter.LastImported is { } copy
            ? AnalyzeStudyUseCase.SelectAnalysableSeries(copy.Study)
            : null;

        record["analysedSeries"] = analysed is null
            ? null
            : new
            {
                analysed.PseudonymousSeriesId,
                Tier = analysed.Tier.ToString(),
                Weighting = analysed.Weighting.ToString(),
                analysed.Geometry.Dimensions.Slices,
            };

        record["excludedSeries"] = adapter.LastImported?.ExcludedSeries.Count ?? 0;

        record["biomarkers"] = report.Biomarkers.Select(biomarker => new
        {
            Method = biomarker.Method.ToString(),
            biomarker.Value,
            Unit = biomarker.Unit.ToString(),
            Quality = biomarker.Quality.ToString(),
            biomarker.IsOutOfRange,
        }).ToArray();

        if (report.Biomarkers.Count > 0)
        {
            measured++;
        }

        foreach (var biomarker in report.Biomarkers)
        {
            Count(qualities, biomarker.Method + "/" + biomarker.Quality);

            if (biomarker.IsOutOfRange)
            {
                outOfRange++;
            }
        }
    }
    catch (Exception exception)
    {
        // Сохраняется тип, а не текст: текст исключения ввода-вывода содержит
        // путь к источнику, а в пути — фамилия пациента.
        var type = exception.GetType().Name;

        Count(failures, type);
        record["outcome"] = "Failed";
        record["exceptionType"] = type;
    }

    durations.Add(studyWatch.Elapsed.TotalSeconds);
    record["seconds"] = Math.Round(studyWatch.Elapsed.TotalSeconds, 2);

    await results.WriteLineAsync(JsonSerializer.Serialize(record));

    if ((index + 1) % 10 == 0 || index + 1 == plan.Count)
    {
        Console.WriteLine($"Замерено {index + 1} из {plan.Count}…");
    }
}

Console.WriteLine();
Console.WriteLine("=== Исходы ===");
Print(outcomes);
Console.WriteLine($"Failed: {failures.Values.Sum()}");

Console.WriteLine();
Console.WriteLine("=== Исключения (по типу) ===");
Print(failures);

Console.WriteLine();
Console.WriteLine("=== Измерения ===");
Console.WriteLine($"исследований с измерением: {measured} из {plan.Count}");
Console.WriteLine($"значений вне правдоподобного диапазона: {outOfRange}");
Print(qualities);

Console.WriteLine();
Console.WriteLine("=== Замечания контроля качества (по коду и важности) ===");
Print(issues);

durations.Sort();

Console.WriteLine();
Console.WriteLine("=== Время ===");
Console.WriteLine($"замер: {watch.Elapsed.TotalMinutes:0.0} мин; медиана на исследование {Median(durations):0.0} с; максимум {(durations.Count == 0 ? 0 : durations[^1]):0.0} с");

return failures.Count == 0 ? 0 : 1;

static void Count(Dictionary<string, int> counts, string key) =>
    counts[key] = counts.GetValueOrDefault(key) + 1;

static void Print(Dictionary<string, int> counts)
{
    foreach (var (key, value) in counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal))
    {
        Console.WriteLine($"{key,-60} {value.ToString(CultureInfo.InvariantCulture)}");
    }
}

static double Median(List<double> sorted) =>
    sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];

/// <summary>
/// Импорт выбранного исследования из уже разобранного источника в контракте
/// <see cref="IStudyImporter"/>: ссылка на источник здесь — псевдоним
/// исследования. Запоминает рабочую копию, чтобы прогон мог описать, какая
/// серия пошла в анализ.
/// </summary>
internal sealed class ScannedStudyImporter(StudyImporter importer, DicomScanResult scan) : IStudyImporter
{
    public WorkingCopy? LastImported { get; private set; }

    public async Task<WorkingCopy> ImportAsync(string sourceReference, CancellationToken cancellationToken)
    {
        this.LastImported = await importer.ImportStudyAsync(scan, sourceReference, cancellationToken)
            .ConfigureAwait(false);

        return this.LastImported;
    }
}
