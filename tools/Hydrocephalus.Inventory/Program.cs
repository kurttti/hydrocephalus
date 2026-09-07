using System.Globalization;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dicom;

// Инвентаризация ретроспективной выборки: сколько пациентов, исследований и серий
// доступно и в каком виде. Утилита требуется docs/data/README.md именно как
// переиспользуемая, а не разовый скрипт: состав манифеста обязан пересчитываться
// при каждом пополнении выборки, иначе он расходится с диском.
//
// Утилита только читает. Она не создаёт рабочих копий, ничего не пишет
// в источник и не печатает ни имён файлов, ни значений тегов — только агрегаты.
// Причина прямая: имена файлов и папок в этой выборке содержат фамилии
// пациентов, и вывод в консоль или в журнал сборки был бы утечкой.
if (args.Length == 0)
{
    Console.Error.WriteLine("Использование: Hydrocephalus.Inventory <каталог> [<каталог>...]");
    return 2;
}

var missing = args.Where(path => !Directory.Exists(path)).ToArray();

if (missing.Length > 0)
{
    // Пути не печатаются: они называют папки с фамилиями.
    Console.Error.WriteLine($"Не найдено каталогов: {missing.Length}.");
    return 2;
}

// Соль берётся установочная, та же, что у приложения. С другой солью псевдонимы
// инвентаризации не совпадут с теми, что создаёт импорт, и сопоставить одно
// с другим будет нечем.
var saltPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Hydrocephalus",
    "secrets",
    "pseudonym-salt.bin");

var salt = await PseudonymSaltStore.GetOrCreateAsync(saltPath, CancellationToken.None);

var options = new DicomImportOptions
{
    PseudonymSalt = salt,

    // Выборка пополняется; лимит по умолчанию рассчитан на одно исследование,
    // а здесь обходится весь диск.
    MaxFileCount = 1_000_000,
};

var scanner = new DicomStudyScanner(options);

var studiesBySource = new Dictionary<string, IReadOnlyList<ImagingStudy>>(StringComparer.Ordinal);
var rejections = new Dictionary<ImportRejectionCode, int>();
var findings = new Dictionary<string, int>(StringComparer.Ordinal);

// Замечаний может быть несколько на одну серию, поэтому доля затронутых серий
// считается по различным идентификаторам, а не по числу замечаний. Прежний
// снимок выборки складывал замечания и называл сумму числом серий.
var seriesWithGeometryFinding = new HashSet<string>(StringComparer.Ordinal);

// Ключ различает не только вид замечания, но и найденное объяснение: без этого
// «совпадающие положения» остались бы одним числом без разбора причин.
static string Describe(QualityIssue issue)
{
    var key = issue.Code.ToString();

    if (issue.Parameters.TryGetValue("reason", out var reason))
    {
        key += "/" + reason;
    }

    if (issue.Parameters.TryGetValue("stackSplit", out var split))
    {
        key += "/" + split;
    }

    return key;
}

foreach (var path in args)
{
    var label = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

    Console.WriteLine($"Разбор: {label}…");

    var result = await scanner.ScanAsync(path, CancellationToken.None);

    studiesBySource[label] = result.Studies;

    foreach (var rejection in result.Rejections)
    {
        rejections[rejection.Code] = rejections.GetValueOrDefault(rejection.Code) + 1;
    }

    foreach (var finding in result.Findings)
    {
        var key = Describe(finding.Issue);

        findings[key] = findings.GetValueOrDefault(key) + 1;

        if (finding.Issue.Code == QualityIssueCode.InconsistentGeometry)
        {
            seriesWithGeometryFinding.Add(finding.PseudonymousSeriesId);
        }
    }
}

var allStudies = studiesBySource.Values.SelectMany(studies => studies).ToArray();
var allSeries = allStudies.SelectMany(study => study.Series).ToArray();

Console.WriteLine();
Console.WriteLine("=== По источникам ===");

foreach (var (label, studies) in studiesBySource.OrderByDescending(entry => entry.Value.Count))
{
    var subjects = studies.Select(study => study.PseudonymousSubjectId).Distinct(StringComparer.Ordinal).Count();

    Console.WriteLine(
        $"{label,-45} пациентов={subjects,-6} исследований={studies.Count,-6} серий={studies.Sum(study => study.Series.Count)}");
}

Console.WriteLine();
Console.WriteLine("=== Пересечение источников ===");

// Один пациент, попавший в несколько независимо собранных папок, — главная
// опасность этой выборки: без дедупликации он окажется сразу в нескольких
// частях split, и метрики будут завышены (docs/data/README.md).
var sourcesBySubject = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

foreach (var (label, studies) in studiesBySource)
{
    foreach (var study in studies)
    {
        if (!sourcesBySubject.TryGetValue(study.PseudonymousSubjectId, out var sources))
        {
            sources = new HashSet<string>(StringComparer.Ordinal);
            sourcesBySubject[study.PseudonymousSubjectId] = sources;
        }

        sources.Add(label);
    }
}

var shared = sourcesBySubject.Count(entry => entry.Value.Count > 1);

Console.WriteLine($"пациентов более чем в одном источнике: {shared}");

Console.WriteLine();
Console.WriteLine("=== Итого ===");

Console.WriteLine($"уникальных пациентов: {sourcesBySubject.Count}");
Console.WriteLine($"исследований: {allStudies.Length}");
Console.WriteLine($"серий: {allSeries.Length}");

Console.WriteLine();
Console.WriteLine("=== Уровень входа (по сериям) ===");

foreach (var tier in Enum.GetValues<AcquisitionTier>())
{
    Console.WriteLine($"{tier,-12} {allSeries.Count(series => series.Tier == tier)}");
}

Console.WriteLine();
Console.WriteLine("=== Взвешенность (по сериям) ===");

foreach (var weighting in Enum.GetValues<SeriesWeighting>())
{
    Console.WriteLine($"{weighting,-12} {allSeries.Count(series => series.Weighting == weighting)}");
}

Console.WriteLine();
Console.WriteLine("=== Пригодность для обучения ===");

// Тот же отбор, что делает конвейер: постконтрастные серии не подаются
// в MRI-only модель ни на одном уровне входа.
var eligible = allStudies
    .Where(study => study.Series.Any(series =>
        !series.IsContrastEnhanced && series.Tier == AcquisitionTier.Extended))
    .Select(study => study.PseudonymousSubjectId)
    .Distinct(StringComparer.Ordinal)
    .Count();

var withAnyExtended = allStudies
    .Where(study => study.Series.Any(series => series.Tier == AcquisitionTier.Extended))
    .Select(study => study.PseudonymousSubjectId)
    .Distinct(StringComparer.Ordinal)
    .Count();

Console.WriteLine($"пациентов с неконтрастной 3D-серией: {eligible}");
Console.WriteLine($"пациентов с любой 3D-серией: {withAnyExtended}");
Console.WriteLine($"постконтрастных серий: {allSeries.Count(series => series.IsContrastEnhanced)}");

Console.WriteLine();
Console.WriteLine("=== Отклонённые файлы ===");

foreach (var (code, count) in rejections.OrderByDescending(entry => entry.Value))
{
    Console.WriteLine($"{code,-28} {count}");
}

Console.WriteLine();
Console.WriteLine("=== Замечания к сериям ===");

foreach (var (code, count) in findings.OrderByDescending(entry => entry.Value))
{
    Console.WriteLine($"{code,-56} {count}");
}

Console.WriteLine();
Console.WriteLine(
    $"серий с блокирующим замечанием геометрии: {seriesWithGeometryFinding.Count} из {allSeries.Length}");

Console.WriteLine();
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"Соль: установочная, из профиля пользователя. Псевдонимы совпадают с теми, что создаёт импорт."));

return 0;
