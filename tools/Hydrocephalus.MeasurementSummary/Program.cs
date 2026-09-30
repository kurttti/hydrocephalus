using System.Globalization;
using System.Text.Json;

// Сведение ручных измерений по группам сравнения.
//
// Отвечает на вопрос, ради которого собиралась выборка: различаются ли группы по
// индексу Эванса. И на второй, не менее нужный: насколько автоматический индекс
// сходится с измеренным врачом — на тех исследованиях, где есть оба.
//
// Группа берётся из манифеста набора, а не выводится заново. Причина в том, что
// заново её вывести нельзя: обезличивание переписывает UID, поэтому разбор копии
// даёт псевдоним от псевдонима, а при пустом PatientID — отдельного «пациента»
// на каждое исследование (docs/data/README.md).
//
// Последнее ручное измерение исследования ищется **перебором всех версий отчёта**,
// а не в последней: повторное открытие исследования заново запускает анализ и
// сохраняет отчёт без ручной отметки, поэтому самая свежая версия её может не
// содержать. Тем же перебором поглощается случайное повторное нажатие.
//
// Печатаются только агрегаты и псевдонимы. Ни имён, ни путей источника.
if (args.Contains("--help"))
{
    Console.WriteLine("Использование: Hydrocephalus.MeasurementSummary [--dataset <каталог>] [--reports <каталог>]");

    return 0;
}

var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

var datasetRoot = Argument("--dataset") ?? Path.Combine(local, "Hydrocephalus", "dataset");
var reportRoot = Argument("--reports") ?? Path.Combine(local, "Hydrocephalus", "reports");

var manifestPath = Path.Combine(datasetRoot, "manifest.json");

if (!File.Exists(manifestPath))
{
    Console.Error.WriteLine("Манифеста набора нет: сначала соберите набор утилитой DatasetBuild.");

    return 2;
}

// Группа и пациент по псевдониму исследования — из манифеста.
var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
var subjectOf = new Dictionary<string, string>(StringComparer.Ordinal);

using (var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath)))
{
    foreach (var study in manifest.RootElement.GetProperty("Studies").EnumerateArray())
    {
        var id = study.GetProperty("PseudonymousStudyId").GetString()!;

        groupOf[id] = study.GetProperty("Group").GetString()!;
        subjectOf[id] = study.GetProperty("PseudonymousSubjectId").GetString()!;
    }
}

Console.WriteLine($"в наборе исследований: {groupOf.Count}");

if (!Directory.Exists(reportRoot))
{
    Console.Error.WriteLine("Каталога отчётов нет: измерений ещё не было.");

    return 2;
}

var manual = new Dictionary<string, Measurement>(StringComparer.Ordinal);
var automatic = new Dictionary<string, double>(StringComparer.Ordinal);

foreach (var studyDirectory in Directory.EnumerateDirectories(reportRoot))
{
    var studyId = Path.GetFileName(studyDirectory);

    // Версии перебираются по времени, записанному внутри отчёта, а не по имени
    // файла: имя начинается с той же отметки, но полагаться на это незачем.
    foreach (var path in Directory.EnumerateFiles(studyDirectory, "*.json"))
    {
        Report? report;

        try
        {
            report = ReadReport(await File.ReadAllBytesAsync(path));
        }
        catch (JsonException)
        {
            Console.Error.WriteLine($"отчёт не разобран: {studyId[..8]}…");
            continue;
        }

        if (report is null)
        {
            continue;
        }

        if (report.EvansManual is { } value
            && (!manual.TryGetValue(studyId, out var known) || report.CreatedAt > known.CreatedAt))
        {
            manual[studyId] = new Measurement(value, report.HeadRotation, report.CreatedAt);
        }

        if (report.EvansAutomatic is { } auto)
        {
            automatic[studyId] = auto;
        }
    }
}

Console.WriteLine($"исследований с ручным измерением: {manual.Count}");
Console.WriteLine($"исследований с автоматическим индексом: {automatic.Count}");

var measured = manual
    .Where(entry => groupOf.ContainsKey(entry.Key))
    .GroupBy(entry => groupOf[entry.Key], StringComparer.Ordinal)
    .OrderBy(group => group.Key, StringComparer.Ordinal)
    .ToList();

var outside = manual.Count(entry => !groupOf.ContainsKey(entry.Key));

if (outside > 0)
{
    // Измерение по исследованию, которого нет в наборе: группа неизвестна,
    // и молча приписать его к какой-нибудь нельзя.
    Console.WriteLine($"измерений вне набора (группа неизвестна): {outside}");
}

Console.WriteLine();
Console.WriteLine("=== Индекс Эванса по группам ===");
Console.WriteLine("группа            n   медиана   P25     P75     мин     макс    ≥0,30");

foreach (var group in measured)
{
    // Пациент, а не исследование: у одного пациента исследований бывает
    // несколько, и считать их как разных больных значило бы удвоить группу.
    var perSubject = group
        .GroupBy(entry => subjectOf[entry.Key], StringComparer.Ordinal)
        .Select(subject => subject.OrderByDescending(entry => entry.Value.CreatedAt).First().Value.Index)
        .OrderBy(value => value)
        .ToList();

    Console.WriteLine(
        $"{group.Key,-16} {perSubject.Count,3}   "
        + $"{Quantile(perSubject, 0.50),-8:0.000}{Quantile(perSubject, 0.25),-8:0.000}"
        + $"{Quantile(perSubject, 0.75),-8:0.000}{perSubject[0],-8:0.000}"
        + $"{perSubject[^1],-8:0.000}{perSubject.Count(value => value >= 0.30),3}");
}

var rotations = manual.Values
    .Select(item => item.RotationDegrees)
    .Where(value => value is not null)
    .Select(value => value!.Value)
    .OrderBy(value => value)
    .ToList();

if (rotations.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("=== Поворот головы при измерении, градусы ===");
    Console.WriteLine(
        $"n {rotations.Count}, медиана {Quantile(rotations, 0.50):0.0}, "
        + $"P95 {Quantile(rotations, 0.95):0.0}, максимум {rotations[^1]:0.0}");
}

var pairs = manual
    .Where(entry => automatic.ContainsKey(entry.Key))
    .Select(entry => (Manual: entry.Value.Index, Automatic: automatic[entry.Key]))
    .ToList();

Console.WriteLine();
Console.WriteLine("=== Автоматический против ручного ===");

if (pairs.Count == 0)
{
    Console.WriteLine("пар нет: ни на одном исследовании не сошлись оба измерения.");
}
else
{
    var differences = pairs.Select(pair => pair.Automatic - pair.Manual).ToList();
    var absolute = differences.Select(Math.Abs).OrderBy(value => value).ToList();

    Console.WriteLine(
        $"пар {pairs.Count}; средняя разность {differences.Average():+0.0000;-0.0000}, "
        + $"средняя по модулю {absolute.Average():0.0000}, наибольшая {absolute[^1]:0.0000}");

    Console.WriteLine(
        "Это первая оценка точности автоматического индекса: ручное измерение — эталон, "
        + "против которого он и не был валидирован.");
}

return 0;

string? Argument(string name)
{
    var index = Array.IndexOf(args, name);

    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static double Quantile(IReadOnlyList<double> sorted, double fraction)
{
    if (sorted.Count == 1)
    {
        return sorted[0];
    }

    var position = fraction * (sorted.Count - 1);
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);

    return sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
}

static Report? ReadReport(byte[] content)
{
    using var document = JsonDocument.Parse(content);
    var root = document.RootElement;

    if (!root.TryGetProperty("createdAt", out var createdAt)
        || !DateTimeOffset.TryParse(
            createdAt.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var moment))
    {
        return null;
    }

    double? manual = null;
    double? auto = null;
    double? rotation = null;

    if (root.TryGetProperty("biomarkers", out var biomarkers))
    {
        foreach (var biomarker in biomarkers.EnumerateArray())
        {
            var code = biomarker.GetProperty("code").GetString();
            var value = biomarker.GetProperty("value").GetDouble();

            // Значение вне диапазона правдоподобия в сведение не идёт: запись
            // такого сейчас и не проходит, но отчёты прежних версий остаются.
            if (biomarker.TryGetProperty("outOfRange", out var flagged) && flagged.GetBoolean())
            {
                continue;
            }

            switch (code)
            {
                case "evans-index-manual":
                    manual = value;
                    break;
                case "evans-index":
                    auto = value;
                    break;
                case "head-rotation-in-plane":
                    rotation = value;
                    break;
                default:
                    break;
            }
        }
    }

    return new Report(moment, manual, auto, rotation);
}

/// <summary>Отчёт в том виде, в каком он нужен сведению.</summary>
internal sealed record Report(
    DateTimeOffset CreatedAt,
    double? EvansManual,
    double? EvansAutomatic,
    double? HeadRotation);

/// <summary>Ручное измерение одного исследования.</summary>
internal sealed record Measurement(double Index, double? RotationDegrees, DateTimeOffset CreatedAt);
