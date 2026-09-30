using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Reporting;

// Сведение ручных измерений по группам сравнения.
//
// Отвечает на вопрос, ради которого собиралась выборка: различаются ли группы по
// индексу Эванса. И на второй, не менее нужный: насколько автоматический индекс
// сходится с измеренным врачом — на тех исследованиях, где есть оба.
//
// Чтение набора и чтение отчётов живут в инфраструктуре: тем же кодом ведёт
// рабочий список измерений просмотрщик, и две копии правил разошлись бы молча.
//
// Печатаются только агрегаты. Ни имён, ни путей источника.
if (args.Contains("--help"))
{
    Console.WriteLine("Использование: Hydrocephalus.MeasurementSummary [--dataset <каталог>] [--reports <каталог>]");

    return 0;
}

var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

var datasetRoot = Argument("--dataset") ?? Path.Combine(local, "Hydrocephalus", "dataset");
var reportRoot = Argument("--reports") ?? Path.Combine(local, "Hydrocephalus", "reports");

var manifest = await DatasetManifest.ReadAsync(datasetRoot, CancellationToken.None);

if (manifest is null)
{
    Console.Error.WriteLine("Манифеста набора нет: сначала соберите набор утилитой DatasetBuild.");

    return 2;
}

var readout = await ManualMeasurementIndex.ReadAsync(reportRoot, CancellationToken.None);

Console.WriteLine($"в наборе исследований: {manifest.Studies.Count}");
Console.WriteLine($"исследований с ручным измерением: {readout.Manual.Count}");
Console.WriteLine($"исследований с автоматическим индексом: {readout.Automatic.Count}");

if (readout.UnreadableReports > 0)
{
    Console.WriteLine($"отчётов не прочитано: {readout.UnreadableReports}");
}

// Измерение привязывается к исследованию набора по любому из двух его
// псевдонимов: с оригинала отчёт ложится под исходным, из набора — под
// производным (docs/data/README.md).
var rows = manifest.Studies
    .Select(study => new Row(
        study,
        Find<ManualMeasurement>(study, key => readout.Manual.TryGetValue(key, out var found) ? found : null),
        Find<double>(study, key => readout.Automatic.TryGetValue(key, out var found) ? found : null)))
    .Where(row => row.Measurement is not null)
    .ToList();

var outside = readout.Manual.Count - rows.Count;

if (outside > 0)
{
    // Измерение по исследованию, которого нет в наборе: группа неизвестна,
    // и молча приписать его к какой-нибудь нельзя.
    Console.WriteLine($"измерений вне набора (группа неизвестна): {outside}");
}

Console.WriteLine();
Console.WriteLine("=== Индекс Эванса по группам ===");
Console.WriteLine("группа            n   медиана   P25     P75     мин     макс    ≥0,30");

foreach (var group in rows
    .GroupBy(row => row.Study.Group, StringComparer.Ordinal)
    .OrderBy(group => group.Key, StringComparer.Ordinal))
{
    // Пациент, а не исследование: у одного больного исследований бывает
    // несколько, и счёт по исследованиям удвоил бы группу.
    var perSubject = group
        .GroupBy(row => row.Study.PseudonymousSubjectId, StringComparer.Ordinal)
        .Select(subject => subject
            .OrderByDescending(row => row.Measurement!.Value.RecordedAt)
            .First()
            .Measurement!.Value.EvansIndex)
        .OrderBy(value => value)
        .ToList();

    Console.WriteLine(
        $"{group.Key,-16} {perSubject.Count,3}   "
        + $"{Quantile(perSubject, 0.50),-8:0.000}{Quantile(perSubject, 0.25),-8:0.000}"
        + $"{Quantile(perSubject, 0.75),-8:0.000}{perSubject[0],-8:0.000}"
        + $"{perSubject[^1],-8:0.000}{perSubject.Count(value => value >= 0.30),3}");
}

var rotations = rows
    .Select(row => row.Measurement!.Value.HeadRotationDegrees)
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

var pairs = rows
    .Where(row => row.Automatic is not null)
    .Select(row => (Manual: row.Measurement!.Value.EvansIndex, Automatic: row.Automatic!.Value))
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

static T? Find<T>(DatasetStudy study, Func<string, T?> lookup)
    where T : struct
{
    foreach (var key in study.ReportKeys())
    {
        if (lookup(key) is { } found)
        {
            return found;
        }
    }

    return null;
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

/// <summary>Исследование набора вместе с тем, что по нему измерено.</summary>
internal readonly record struct Row(
    DatasetStudy Study,
    ManualMeasurement? Measurement,
    double? Automatic);
