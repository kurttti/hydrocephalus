using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Models;
using Hydrocephalus.Infrastructure.Volumes;

// Парная проверка на FLAIR: три пути сегментации на одной и той же серии.
//
// Зачем. У восемнадцати исследований выборки аксиальна только серия FLAIR, и
// автоматического эталона для них не существует: единственная T1 у них
// сагиттальная или корональная толстым срезом, то есть мерить по ней ширину
// передних рогов нельзя. Проверить способ на самих этих восемнадцати тоже
// нельзя — он окажется подогнан под ту выборку, на которой потом считается
// разделение групп.
//
// Опора — пара: исследование, где есть и измеренная моделью серия T1, и серия
// FLAIR. Пациент один, значит истинный индекс один. Эталон — значение модели по
// T1 из уже сделанного прогона; проверяемое — значение, полученное по FLAIR.
//
// Эталон при этом сам помечен «сомнительно» и с линейкой не сверялся, поэтому
// согласие говорит о согласованности двух контрастов, а не о точности. Ошибки
// выбора плоскости, общие обоим путям, проходят через такую проверку насквозь.
//
// Утилита читает набор и пишет только в --out. Ни имён файлов, ни имён папок
// она не печатает.
if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(
        "Использование: Hydrocephalus.FlairPairCheck --out <каталог вне репозитория> "
        + "--reference <results.jsonl прогона> <источник> [<источник>...]");
    Console.WriteLine();
    Console.WriteLine("Эталон берётся из прогона: значение индекса там, где разобрана серия T1.");
    Console.WriteLine("--limit N  взять только первые N пар (для пробы).");

    return args.Length == 0 ? 2 : 0;
}

var outIndex = Array.IndexOf(args, "--out");
var referenceIndex = Array.IndexOf(args, "--reference");
var limitIndex = Array.IndexOf(args, "--limit");

if (outIndex < 0 || outIndex == args.Length - 1)
{
    Console.Error.WriteLine("Не задан --out.");

    return 2;
}

if (referenceIndex < 0 || referenceIndex == args.Length - 1)
{
    Console.Error.WriteLine("Не задан --reference.");

    return 2;
}

var output = Path.GetFullPath(args[outIndex + 1]);
var referencePath = args[referenceIndex + 1];

var limit = limitIndex >= 0 && limitIndex < args.Length - 1
    ? int.Parse(args[limitIndex + 1], CultureInfo.InvariantCulture)
    : int.MaxValue;

// Результат — измерения, привязанные к псевдонимам пациентов: в репозиторий он
// не попадает ни при каких условиях, и проверка идёт по расположению.
for (var current = new DirectoryInfo(output); current is not null; current = current.Parent)
{
    if (Directory.Exists(Path.Combine(current.FullName, ".git"))
        || File.Exists(Path.Combine(current.FullName, ".git")))
    {
        Console.Error.WriteLine("Каталог вывода лежит внутри репозитория.");

        return 2;
    }
}

if (!File.Exists(referencePath))
{
    Console.Error.WriteLine("Выгрузка прогона с эталонными значениями не найдена.");

    return 2;
}

var skip = new HashSet<int> { outIndex, outIndex + 1, referenceIndex, referenceIndex + 1 };

if (limitIndex >= 0)
{
    skip.Add(limitIndex);
    skip.Add(limitIndex + 1);
}

var sources = args
    .Where((_, index) => !skip.Contains(index))
    .Where(argument => !argument.StartsWith("--", StringComparison.Ordinal))
    .ToArray();

if (sources.Length == 0)
{
    Console.Error.WriteLine("Не задан ни один источник.");

    return 2;
}

var reference = ReadReference(referencePath);

Console.WriteLine($"Эталонных значений по T1 в прогоне: {reference.Count}.");

var applicationRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Hydrocephalus");

var salt = await PseudonymSaltStore
    .GetOrCreateAsync(
        Path.Combine(applicationRoot, "secrets", "pseudonym-salt.bin"),
        CancellationToken.None);

var importOptions = new DicomImportOptions
{
    PseudonymSalt = salt,
    MaxFileCount = 1_000_000,
};

var watch = Stopwatch.StartNew();

// Исследования сводятся по псевдониму: один и тот же лежит в нескольких
// источниках, и мерить его дважды незачем.
var planned = new Dictionary<string, (DicomScanResult Scan, ImagingStudy Study)>(StringComparer.Ordinal);

for (var index = 0; index < sources.Length; index++)
{
    Console.WriteLine($"Разбор источника {index + 1} из {sources.Length}…");

    var scan = await new DicomStudyScanner(importOptions)
        .ScanAsync(sources[index], CancellationToken.None);

    foreach (var study in scan.Studies)
    {
        _ = planned.TryAdd(study.PseudonymousStudyId, (scan, study));
    }
}

Console.WriteLine(
    $"Уникальных исследований: {planned.Count}; разбор занял {watch.Elapsed.TotalMinutes:0.0} мин.");

// Модель берётся так же, как её берёт приложение: действующий пакет из того же
// каталога, проверенный тем же доверенным ключом. Разница одна — модели здесь
// разрешена взвешенность FLAIR, и это видно в строке происхождения.
var trustKeyPath = Path.Combine(applicationRoot, "secrets", "model-trust.pub.pem");
var activePackage = new InstalledModelStore(Path.Combine(applicationRoot, "models")).ActivePackagePath();

if (activePackage is null || !File.Exists(trustKeyPath))
{
    Console.Error.WriteLine("Действующего пакета модели или доверенного ключа нет: сверять нечем.");

    return 2;
}

using var packageReader = new SignedModelPackageReader(
    File.ReadAllText(trustKeyPath),
    Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
    [VolumeConforming.PreprocessingVersion],
    [OnnxVentricleSegmentation.LabelMapVersion]);

var opened = packageReader.Open(activePackage);

if (opened.Rejection is not null || opened.Weights is not { } weights || opened.Manifest is not { } manifest)
{
    Console.Error.WriteLine($"Пакет модели проверку не прошёл: {opened.Rejection}.");

    return 2;
}

var model = new ModelVentricleSegmentation(
    new OnnxVentricleSegmentation(weights),
    manifest.ModelVersion,
    options: null,
    acceptedWeightings: [SeriesWeighting.T1, SeriesWeighting.Flair]);

var threshold = new ThresholdVentricleSegmentation();

// Починка правила глубины: по умолчанию выключена, здесь включена ради сверки
// трёх путей на одних и тех же сериях.
var trimmed = new ThresholdVentricleSegmentation(
    new BaselineSegmentationOptions { TrimDepthByVoxel = true });

Console.WriteLine($"Пути: {threshold.Provenance}, он же с TrimDepthByVoxel, {model.Provenance}.");
Console.WriteLine();

Directory.CreateDirectory(output);

var importer = new StudyImporter(
    importOptions,
    new WorkingCopyOptions { RootDirectory = Path.Combine(applicationRoot, "working-copies") });

var volumes = new WorkingCopyVolumeSource(importer);
var rows = new List<Row>();
var noFlair = 0;
var notPaired = 0;

watch.Restart();

foreach (var (studyId, (scan, _)) in planned.OrderBy(item => item.Key, StringComparer.Ordinal))
{
    if (rows.Count >= limit)
    {
        break;
    }

    if (!reference.TryGetValue(studyId, out var expected))
    {
        // Эталона нет: либо исследование разобрано не по T1, либо индекс по T1
        // не вышел. Парой такое исследование не является — ровно эти случаи и
        // образуют те восемнадцать, которые проверить нечем.
        notPaired++;
        continue;
    }

    WorkingCopy? copy = null;

    try
    {
        copy = await importer.ImportStudyAsync(scan, studyId, CancellationToken.None);

        var flair = ChooseFlair(copy.Study);

        if (flair is null)
        {
            noFlair++;
            continue;
        }

        var volume = await volumes.LoadAsync(
            copy.VolumeReference,
            flair.PseudonymousSeriesId,
            CancellationToken.None);

        // Та же модель по серии T1 того же исследования: отрезки двух
        // контрастов должны быть измерены одним кодом в одном прогоне.
        ArmResult? onT1 = null;

        if (ChooseT1(copy.Study) is { } t1)
        {
            var t1Volume = await volumes.LoadAsync(
                copy.VolumeReference,
                t1.PseudonymousSeriesId,
                CancellationToken.None);

            onT1 = Arm(t1Volume, model, t1.Weighting);
        }

        var row = new Row
        {
            StudyId = studyId,
            SubjectId = copy.Study.PseudonymousSubjectId,
            Reference = expected,
            Slices = flair.Geometry.Dimensions.Slices,
            SliceSpacingMillimetres = flair.Geometry.SliceSpacingMillimetres,
            CoverageMillimetres = flair.Geometry.CoverageMillimetres,
            Threshold = Arm(volume, threshold, flair.Weighting),
            Trimmed = Arm(volume, trimmed, flair.Weighting),
            Model = Arm(volume, model, flair.Weighting),
            ModelOnT1 = onT1,
        };

        rows.Add(row);

        Console.WriteLine(
            $"{rows.Count,3}. {studyId[..8]}  эталон {expected:0.0000}  "
            + $"порог {Show(row.Threshold)}  с починкой {Show(row.Trimmed)}  модель {Show(row.Model)}"
            + $"  T1 {(row.ModelOnT1 is null ? "нет серии" : Show(row.ModelOnT1))}"
            + $"  рога {Millimetres(row.Model.WidthMillimetres)}/{Millimetres(row.ModelOnT1?.WidthMillimetres)}"
            + $"  череп {Millimetres(row.Model.DiameterMillimetres)}/{Millimetres(row.ModelOnT1?.DiameterMillimetres)}");
    }
    catch (Exception error) when (error is IOException or InvalidOperationException
        or DomainRuleViolationException)
    {
        Console.WriteLine($"     {studyId[..8]}  не измерено: {error.GetType().Name}");
    }
    finally
    {
        if (copy is not null)
        {
            await importer.ReleaseAsync(copy.VolumeReference);
        }
    }
}

Console.WriteLine();
Console.WriteLine($"Замер занял {watch.Elapsed.TotalMinutes:0.0} мин.");
Console.WriteLine($"пар разобрано: {rows.Count}; без серии FLAIR: {noFlair}; без эталона по T1: {notPaired}.");
Console.WriteLine();

await File.WriteAllLinesAsync(
    Path.Combine(output, "results.jsonl"),
    rows.Select(row => JsonSerializer.Serialize(row)),
    CancellationToken.None);

Summarise("пороговый путь как есть", rows, row => row.Threshold);
Summarise("он же с TrimDepthByVoxel", rows, row => row.Trimmed);
Summarise("модель на FLAIR", rows, row => row.Model);

Console.WriteLine($"Пофайловый итог: {Path.Combine(output, "results.jsonl")}");

return 0;

// Допуск на согласие 0,05 по индексу взят из парного прогона IXI 7 октября:
// согласие там 0,001–0,013, срыв 0,243. Взят до счёта и из чужих чисел.
static void Summarise(string title, List<Row> rows, Func<Row, ArmResult> arm)
{
    const double Tolerance = 0.05;

    var measured = rows.Where(row => arm(row).Value is not null).ToList();
    var agreed = measured.Where(row => Math.Abs(arm(row).Value!.Value - row.Reference) <= Tolerance).ToList();
    var missed = measured.Except(agreed).ToList();

    Console.WriteLine($"=== {title}");
    Console.WriteLine($"  измерено {measured.Count} из {rows.Count}; "
        + $"сошлось (допуск {Tolerance:0.00}) {agreed.Count}; разошлось {missed.Count}");

    if (measured.Count > 0)
    {
        var differences = measured
            .Select(row => arm(row).Value!.Value - row.Reference)
            .Order()
            .ToList();

        var median = differences.Count % 2 == 1
            ? differences[differences.Count / 2]
            : (differences[(differences.Count / 2) - 1] + differences[differences.Count / 2]) / 2;

        Console.WriteLine($"  разность с эталоном: медиана {median:+0.0000;-0.0000}, "
            + $"от {differences[0]:+0.0000;-0.0000} до {differences[^1]:+0.0000;-0.0000}");
    }

    foreach (var row in missed.OrderByDescending(row => Math.Abs(arm(row).Value!.Value - row.Reference)))
    {
        Console.WriteLine($"  промах {row.StudyId[..8]}: {arm(row).Value:0.0000} при эталоне "
            + $"{row.Reference:0.0000} ({arm(row).Value!.Value - row.Reference:+0.0000;-0.0000})");
    }

    var refusals = rows
        .Where(row => arm(row).Value is null)
        .GroupBy(row => arm(row).Refusal ?? "неизвестно", StringComparer.Ordinal)
        .OrderByDescending(group => group.Count());

    foreach (var refusal in refusals)
    {
        Console.WriteLine($"  без значения — {refusal.Key}: {refusal.Count()}");
    }

    Console.WriteLine();
}

static string Show(ArmResult result) => result.Value is { } value
    ? value.ToString("0.0000", CultureInfo.InvariantCulture)
    : "—";

static string Millimetres(double? value) => value is { } millimetres
    ? millimetres.ToString("0.0", CultureInfo.InvariantCulture)
    : "—";

/// <summary>Считает индекс одним путём на уже загруженном объёме.</summary>
static ArmResult Arm(IVoxelVolume volume, IVentricleSegmentation segmentation, SeriesWeighting weighting)
{
    var mask = segmentation.Segment(volume, weighting, "pair-check", CancellationToken.None);
    var measured = AutomaticEvansIndex.Measure(volume, mask, weighting, CancellationToken.None);

    var grid = volume.Grid;
    var voxel = grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres
        * grid.SliceSpacingMillimetres / 1000.0;
    var selected = 0L;

    for (var slice = 0; slice < grid.Dimensions.Slices; slice++)
    {
        for (var row = 0; row < grid.Dimensions.Rows; row++)
        {
            for (var column = 0; column < grid.Dimensions.Columns; column++)
            {
                if (mask.Mask[column, row, slice] != 0)
                {
                    selected++;
                }
            }
        }
    }

    var millilitres = selected * voxel;

    // Сами отрезки, а не только их отношение: индекс — дробь, и занижение
    // дроби может идти и от числителя, и от знаменателя. Пока записано одно
    // отношение, различить их нельзя.
    var width = measured.Segments is { } segments
        ? PatientSpace.DistanceMillimetres(volume, segments.FrontalHornFirst, segments.FrontalHornSecond)
        : (double?)null;

    var diameter = measured.Segments is { } found
        ? PatientSpace.DistanceMillimetres(volume, found.InnerSkullFirst, found.InnerSkullSecond)
        : (double?)null;

    return new ArmResult
    {
        Value = measured.Biomarker?.Value,
        Refusal = measured.Refusal?.ToString(),
        MaskMillilitres = millilitres,
        WidthMillimetres = width,
        DiameterMillimetres = diameter,
        PlaneIndex = measured.Segments?.PlaneIndex,
        Issues = [.. mask.Issues.Select(issue => issue.Code + "/" + issue.Severity
            + (issue.Parameters.TryGetValue("reason", out var reason) ? "/" + reason : string.Empty))],
    };
}

/// <summary>
/// Выбирает серию FLAIR для проверки.
///
/// Требования те же, что у самого измерения: аксиальная плоскость, покрытие
/// желудочков, без контраста. Из подходящих берётся самая полная по покрытию,
/// при равном — с наименьшим шагом.
/// </summary>
static ImagingSeries? ChooseFlair(ImagingStudy study) => study.Series
    .Where(series => series.Weighting == SeriesWeighting.Flair)
    .Where(series => !series.IsContrastEnhanced)
    .Where(series => series.Geometry.Plane == ImagingPlane.Axial)
    .Where(series => series.Geometry.CoversVentricles)
    .OrderByDescending(series => series.Geometry.CoverageMillimetres)
    .ThenBy(series => series.Geometry.SliceSpacingMillimetres)
    .FirstOrDefault();

/// <summary>
/// Выбирает серию T1 того же исследования — ту, по которой считался эталон.
///
/// Требования те же: аксиальная плоскость, покрытие желудочков, без контраста.
/// Нужна, чтобы отрезки обоих контрастов были измерены одним кодом в одном
/// прогоне: сравнивать ширину из прошлой выгрузки с шириной из нынешней нельзя.
/// </summary>
static ImagingSeries? ChooseT1(ImagingStudy study) => study.Series
    .Where(series => series.Weighting == SeriesWeighting.T1)
    .Where(series => !series.IsContrastEnhanced)
    .Where(series => series.Geometry.Plane == ImagingPlane.Axial)
    .Where(series => series.Geometry.CoversVentricles)
    .OrderByDescending(series => series.Geometry.Tier)
    .ThenByDescending(series => series.Geometry.CoverageMillimetres)
    .FirstOrDefault();

/// <summary>
/// Читает эталонные значения из выгрузки прогона: индекс там, где разобрана
/// серия T1. Значение по FLAIR эталоном быть не может — его и проверяем.
/// </summary>
static Dictionary<string, double> ReadReference(string path)
{
    var reference = new Dictionary<string, double>(StringComparer.Ordinal);

    foreach (var line in File.ReadLines(path))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        if (!root.TryGetProperty("pseudonymousStudyId", out var studyId)
            || !root.TryGetProperty("analysedSeries", out var series)
            || series.ValueKind != JsonValueKind.Object
            || !series.TryGetProperty("Weighting", out var weighting)
            || !string.Equals(weighting.GetString(), "T1", StringComparison.Ordinal)
            || !root.TryGetProperty("biomarkers", out var biomarkers))
        {
            continue;
        }

        foreach (var biomarker in biomarkers.EnumerateArray())
        {
            if (biomarker.TryGetProperty("Method", out var method)
                && string.Equals(method.GetString(), "evans-index", StringComparison.Ordinal)
                && biomarker.TryGetProperty("Value", out var value)
                && studyId.GetString() is { } key)
            {
                reference[key] = value.GetDouble();
            }
        }
    }

    return reference;
}

/// <summary>Итог одного пути на одной серии.</summary>
internal sealed record ArmResult
{
    /// <summary>Индекс Эванса либо <see langword="null"/> при отказе.</summary>
    public double? Value { get; init; }

    /// <summary>Причина отказа либо <see langword="null"/>.</summary>
    public string? Refusal { get; init; }

    /// <summary>Объём маски, мл.</summary>
    public double MaskMillilitres { get; init; }

    /// <summary>Ширина передних рогов, мм; <see langword="null"/> при отказе.</summary>
    public double? WidthMillimetres { get; init; }

    /// <summary>Внутренний диаметр черепа на той же плоскости, мм.</summary>
    public double? DiameterMillimetres { get; init; }

    /// <summary>Номер плоскости, на которой посчитан индекс.</summary>
    public int? PlaneIndex { get; init; }

    /// <summary>Замечания сегментации.</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>Одна пара: эталон по T1 и три пути по FLAIR.</summary>
internal sealed record Row
{
    /// <summary>Псевдоним исследования.</summary>
    public required string StudyId { get; init; }

    /// <summary>Псевдоним пациента.</summary>
    public required string SubjectId { get; init; }

    /// <summary>Эталонное значение: модель по серии T1.</summary>
    public required double Reference { get; init; }

    /// <summary>Срезов в серии FLAIR.</summary>
    public required int Slices { get; init; }

    /// <summary>Шаг между срезами, мм.</summary>
    public required double SliceSpacingMillimetres { get; init; }

    /// <summary>Покрытие по оси срезов, мм.</summary>
    public required double CoverageMillimetres { get; init; }

    /// <summary>Пороговый путь как есть.</summary>
    public required ArmResult Threshold { get; init; }

    /// <summary>Пороговый путь с починкой правила глубины.</summary>
    public required ArmResult Trimmed { get; init; }

    /// <summary>Модель.</summary>
    public required ArmResult Model { get; init; }

    /// <summary>
    /// Та же модель по серии T1 того же исследования либо <see langword="null"/>,
    /// если пригодной аксиальной T1 в рабочей копии не нашлось.
    /// </summary>
    public ArmResult? ModelOnT1 { get; init; }
}
