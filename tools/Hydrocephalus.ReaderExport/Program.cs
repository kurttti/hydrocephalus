using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Dicom;

// Выгрузка замороженного набора для внешнего чтения нейрорадиологом.
//
// Зачем отдельная утилита. Специалист не получает приложение — решение владельца
// данных: исходников у него нет, стек другой, а разметка нужна независимая.
// Значит ему уезжают снимки и таблица, и ровно от того, как они уезжают,
// зависит, будет ли разметка эталоном или вторым мнением с оглядкой на ответ.
//
// Три свойства выгрузки обязательны, и каждое сделано здесь, а не в инструкции:
//
// 1. Ослепление. Код исследования выдаётся по случайной перестановке с
//    записанным зерном, а не по порядку в наборе: в наборе исследования лежат
//    группами, и любой возрастающий номер выдал бы группу. Группа и псевдонимы
//    остаются в ключе, который в выгрузку не попадает.
// 2. Отсутствие подсказок. В выгрузку не идёт ничего, что говорит об ожидаемом
//    диагнозе: ни группа, ни наши измерения, ни причины исключения.
// 3. Опись серий. Специалисту нужно знать, на какой серии он мерит: в одном
//    исследовании их бывает десяток.
//
// Взвешенность и плоскость берутся обходом файлов, а не из манифеста. Это не
// перестраховка: в манифесте 60 серий из 379 значатся Unknown и лишь 20 —
// Flair, тогда как обход находит FLAIR почти у всех исследований. Манифест
// писался по источнику, где описания серий беднее, а разбор копии подключает
// параметры получения. Отдать таблицу, где FLAIR назван T2, значит получить
// пустой столбец Фазекаса и не узнать почему.
//
// Возраст и пол выгрузка подставляет в регистр сама. Из файлов эти теги удалены
// при обезличивании (ADR 0003), и прочитать их в просмотрщике специалист не
// может — а без возраста разницу между группами не отделить от разницы в
// возрасте.
//
// По умолчанию утилита ничего не копирует: печатает, что будет сделано. Копия
// идёт только с --apply.
if (args.Length == 0)
{
    Console.Error.WriteLine(
        """
        Использование:
          Hydrocephalus.ReaderExport --out <каталог вне репозитория>
              [--dataset <корень набора>] [--seed <число>] [--key <файл ключа>]
              [--limit <число исследований>] [--apply] [--tables-only]

          Без --apply печатает состав выгрузки и ничего не пишет.
          Зерно перестановки печатается всегда: без него выгрузку не повторить.
        """);

    return 2;
}

var outIndex = Array.IndexOf(args, "--out");

if (outIndex < 0 || outIndex == args.Length - 1)
{
    Console.Error.WriteLine("Не указан --out.");

    return 2;
}

var output = Path.GetFullPath(args[outIndex + 1]);

// Выгрузка — это снимки пациентов: в репозиторий она не попадает ни при каких
// условиях, и проверка идёт по расположению, а не по доверию к доводу.
for (var current = new DirectoryInfo(output); current is not null; current = current.Parent)
{
    if (Directory.Exists(Path.Combine(current.FullName, ".git"))
        || File.Exists(Path.Combine(current.FullName, ".git")))
    {
        Console.Error.WriteLine("Каталог выгрузки лежит внутри репозитория.");

        return 2;
    }
}

var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var applicationRoot = Path.Combine(localData, "Hydrocephalus");
var datasetRoot = Value(args, "--dataset") ?? Path.Combine(applicationRoot, "dataset");
var apply = args.Contains("--apply", StringComparer.Ordinal);

// Таблицы правятся чаще снимков: перекопировать из-за опечатки в заголовке
// три с половиной гигабайта незачем.
var tablesOnly = args.Contains("--tables-only", StringComparer.Ordinal);
var today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

var keyPath = Value(args, "--key") ?? Path.Combine(
    applicationRoot,
    "diagnosis-draft",
    $"ключ-чтения-НЕ-ПЕРЕДАВАТЬ-{today}.csv");

// Ключ связывает код чтения с группой. Внутри выгрузки он обессмыслил бы
// ослепление, поэтому путь проверяется, а не описывается в инструкции.
if (Path.GetFullPath(keyPath).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Файл ключа лежит внутри выгрузки: ослепление тогда бессмысленно.");

    return 2;
}

var seed = Value(args, "--seed") is { } given
    ? int.Parse(given, CultureInfo.InvariantCulture)
    : Random.Shared.Next();

var limit = Value(args, "--limit") is { } count
    ? int.Parse(count, CultureInfo.InvariantCulture)
    : int.MaxValue;

var manifest = await DatasetManifest.ReadAsync(datasetRoot, CancellationToken.None).ConfigureAwait(false);

if (manifest is null)
{
    Console.Error.WriteLine("Манифест набора не найден: " + Path.Combine(datasetRoot, DatasetManifest.FileName));

    return 2;
}

var salt = await PseudonymSaltStore
    .GetOrCreateAsync(Path.Combine(applicationRoot, "secrets", "pseudonym-salt.bin"), CancellationToken.None)
    .ConfigureAwait(false);

var importOptions = new DicomImportOptions { PseudonymSalt = salt, MaxFileCount = 1_000_000 };
var scanner = new DicomStudyScanner(importOptions);

// Перестановка воспроизводима по зерну и не зависит от порядка записей в
// манифесте. Но она **зависит от состава**: добавится исследование — и
// перемешается весь список, то есть коды сдвинутся у всех. Поэтому в ключ
// пишется отпечаток состава, а пополнение набора после выдачи выгрузки означает
// отдельную выгрузку с новыми кодами, а не ту же самую.
var shuffled = manifest.Studies
    .OrderBy(study => study.PseudonymousStudyId, StringComparer.Ordinal)
    .ToList();

var random = new Random(seed);

for (var index = shuffled.Count - 1; index > 0; index--)
{
    var swap = random.Next(index + 1);
    (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
}

var cases = new List<ReadingCase>();
var undescribed = 0;

Console.WriteLine($"Обход серий: {manifest.Studies.Sum(study => study.Series.Count)}…");

for (var index = 0; index < shuffled.Count && cases.Count < limit; index++)
{
    var study = shuffled[index];
    var directory = study.DirectoryIn(datasetRoot);
    var series = new List<ReadingSeries>();

    for (var number = 0; number < study.Series.Count; number++)
    {
        var source = study.Series[number];
        var folder = Path.Combine(directory, source.PseudonymousSeriesId);

        // Разбор по одной серии: в нашей раскладке папка серии и есть серия, и
        // так взвешенность с плоскостью приходят от того же кода, которым их
        // определяет приложение, а не от копии правила в утилите.
        var scanned = Directory.Exists(folder)
            ? (await scanner.ScanAsync(folder, CancellationToken.None).ConfigureAwait(false))
                .Studies.SelectMany(item => item.Series).FirstOrDefault()
            : null;

        if (scanned is null)
        {
            undescribed++;
        }

        series.Add(new ReadingSeries
        {
            Code = string.Create(CultureInfo.InvariantCulture, $"SER{number + 1:D2}"),
            Source = source,
            Scanned = scanned,
            Folder = folder,
            Scanner = Device(folder),
            Bytes = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).Sum(file => new FileInfo(file).Length)
                : 0,
        });
    }

    cases.Add(new ReadingCase
    {
        Code = string.Create(CultureInfo.InvariantCulture, $"S-{cases.Count + 1:D4}"),
        Study = study,
        Series = series,
    });
}

// Печать — только количества и коды. Имена папок набора псевдонимны, но группа
// в них есть, и в вывод она не идёт.
Console.WriteLine();
Console.WriteLine($"набор: {manifest.Studies.Count} исследований, собран {manifest.BuiltAt:yyyy-MM-dd}");
Console.WriteLine($"пациентов: {manifest.Studies.Select(study => study.PseudonymousSubjectId).Distinct(StringComparer.Ordinal).Count()}");
Console.WriteLine($"в выгрузку войдёт исследований: {cases.Count}");
Console.WriteLine($"серий, которые разбор не описал: {undescribed}");
Console.WriteLine($"зерно перестановки: {seed}");
Console.WriteLine();

var repeated = manifest.Studies
    .GroupBy(study => study.PseudonymousSubjectId, StringComparer.Ordinal)
    .Count(group => group.Count() > 1);

if (repeated > 0)
{
    Console.WriteLine(
        $"ВНИМАНИЕ: пациентов с более чем одним исследованием в наборе: {repeated}. Они получат "
        + "разные коды, и специалист прочитает их как разные случаи. Для сравнения групп это "
        + "зависимые наблюдения: решение, оставлять ли оба, за владельцем данных.");
    Console.WriteLine();
}

// Проверка выполнимости задания до того, как специалист потратит на него время:
// задание требует четырёх видов серий, и чего-то может не оказаться.
var axial = cases.Count(item => item.Series.Any(line =>
    line.Plane == ImagingPlane.Axial && line.Weighting is SeriesWeighting.T2 or SeriesWeighting.Flair));
var covering = cases.Count(item => item.Series.Any(line =>
    line.Plane == ImagingPlane.Axial
    && line.Weighting is SeriesWeighting.T2 or SeriesWeighting.Flair
    && line.Scanned?.Geometry.CoversVentricles == true));
var coronal = cases.Count(item => item.Series.Any(line =>
    line.Plane == ImagingPlane.Coronal && line.Weighting is SeriesWeighting.T1 or SeriesWeighting.T2));
var flair = cases.Count(item => item.Series.Any(line => line.Weighting == SeriesWeighting.Flair));
// Объёмная T1 считается пригодной не по одному уровню: задание требует
// неконтрастную серию, покрывающую желудочки целиком, иначе реформат по AC-PC
// делать не на чем.
var volumetric = cases.Count(item => item.Series.Any(line =>
    line.Scanned?.Tier == AcquisitionTier.Extended
    && line.Weighting == SeriesWeighting.T1
    && line.Scanned.IsContrastEnhanced == false
    && line.Scanned.Geometry.CoversVentricles));

Console.WriteLine("из скольких исследований выполнимы разделы задания:");
Console.WriteLine($"   аксиальная T2 или FLAIR (индекс, рога, III желудочек): {axial} из {cases.Count}");
Console.WriteLine($"   из них с охватом желудочков целиком:                   {covering} из {cases.Count}");
Console.WriteLine($"   корональная T1 или T2 (угол, MTA, щели):               {coronal} из {cases.Count}");
Console.WriteLine($"   FLAIR любой плоскости (Фазекас):                       {flair} из {cases.Count}");
Console.WriteLine($"   объёмная T1 (необязательный раздел):                   {volumetric} из {cases.Count}");
Console.WriteLine();

var bytes = cases.Sum(item => item.Series.Sum(line => line.Bytes));

Console.WriteLine($"серий к копированию: {cases.Sum(item => item.Series.Count)}");
Console.WriteLine($"объём копии: {bytes / 1024.0 / 1024 / 1024:F1} ГБ");
Console.WriteLine();

if (!apply && !tablesOnly)
{
    Console.WriteLine("Это только разбор. Копия пойдёт с --apply; зерно выше нужно сохранить.");

    return 0;
}

Directory.CreateDirectory(output);

var images = Path.Combine(output, "снимки");
var copied = 0L;

foreach (var item in tablesOnly ? [] : cases)
{
    foreach (var line in item.Series)
    {
        if (!Directory.Exists(line.Folder))
        {
            continue;
        }

        var target = Path.Combine(images, item.Code, line.Code);

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(line.Folder))
        {
            CopyWithoutDevice(file, Path.Combine(target, Path.GetFileName(file)));
            copied++;
        }
    }
}

await WriteSeriesAsync(Path.Combine(output, "серии.csv"), cases).ConfigureAwait(false);
await WriteRegisterAsync(Path.Combine(output, "регистр.csv"), cases).ConfigureAwait(false);
await WriteReadMeAsync(Path.Combine(output, "как-читать.txt"), cases.Count, seed).ConfigureAwait(false);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyPath))!);

// Ключ — единственное, что связывает коды чтения с группами. Перезапись молча
// обессмыслила бы уже выданную выгрузку: та же дата, другая перестановка, и
// прочитанное не с чем сводить.
if (File.Exists(keyPath))
{
    Console.Error.WriteLine("Ключ уже существует: " + keyPath);
    Console.Error.WriteLine("Перезапись отказана. Задайте другой --key либо уберите прежний ключ сами.");

    return 2;
}

await WriteKeyAsync(keyPath, cases, seed, datasetRoot).ConfigureAwait(false);

Console.WriteLine($"скопировано файлов: {copied}");
Console.WriteLine("записаны: снимки, серии.csv, регистр.csv, как-читать.txt");
Console.WriteLine("ключ записан отдельно от выгрузки: " + keyPath);
Console.WriteLine("Ключ не передаётся вместе со снимками — в нём группы.");

return 0;

static string? Value(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);

    return index >= 0 && index < arguments.Length - 1 ? arguments[index + 1] : null;
}

/// <summary>
/// Копирует файл серии, убирая теги аппарата.
///
/// Полного обезличивания от этого не прибавляется: вид снимка при 0,19 Тесла ни
/// с чем не спутать, а название последовательности выдаёт производителя. Но
/// явная подпись «SIEMENS Symphony 1,5 Т» в свойствах исследования — это
/// готовый ответ, который виден, не глядя на анатомию, и его убрать можно.
/// </summary>
static void CopyWithoutDevice(string source, string target)
{
    DicomTag[] device =
    [
        DicomTag.Manufacturer,
        DicomTag.ManufacturerModelName,
        DicomTag.MagneticFieldStrength,
        DicomTag.StationName,
        DicomTag.DeviceSerialNumber,
        DicomTag.SoftwareVersions,
    ];

    try
    {
        var file = DicomFile.Open(source);

        file.Dataset.Remove(device);
        file.Save(target);
    }
    catch (DicomDataException)
    {
        // Файл, который не разбирается, копируется как есть: потерять срез
        // хуже, чем оставить в нём подпись аппарата.
        File.Copy(source, target, overwrite: true);
    }
}

/// <summary>Аппарат и поле: нужны ключу, в выгрузку не идут.</summary>
static string Device(string folder)
{
    var first = Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder).OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault()
        : null;

    if (first is null)
    {
        return string.Empty;
    }

    try
    {
        var dataset = DicomFile.Open(first, FileReadOption.ReadLargeOnDemand).Dataset;
        var maker = dataset.GetSingleValueOrDefault(DicomTag.Manufacturer, string.Empty)?.Trim() ?? string.Empty;
        var model = dataset.GetSingleValueOrDefault(DicomTag.ManufacturerModelName, string.Empty)?.Trim() ?? string.Empty;
        var field = dataset.GetSingleValueOrDefault(DicomTag.MagneticFieldStrength, 0.0);
        var parts = new List<string>();

        if (maker.Length > 0)
        {
            parts.Add(maker);
        }

        if (model.Length > 0)
        {
            parts.Add(model);
        }

        if (field > 0)
        {
            parts.Add(field.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) + " Т");
        }

        return string.Join(' ', parts);
    }
    catch (DicomDataException)
    {
        return string.Empty;
    }
    catch (IOException)
    {
        return string.Empty;
    }
}

static async Task WriteSeriesAsync(string path, List<ReadingCase> cases)
{
    var culture = CultureInfo.GetCultureInfo("ru-RU");

    await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));

    await writer.WriteLineAsync(string.Join(
        ';',
        "код исследования",
        "код серии",
        "взвешенность",
        "плоскость",
        "срезов",
        "толщина, мм",
        "шаг, мм",
        "охват, мм",
        "уровень")).ConfigureAwait(false);

    foreach (var item in cases)
    {
        foreach (var line in item.Series)
        {
            var geometry = line.Scanned?.Geometry;

            await writer.WriteLineAsync(string.Join(
                ';',
                item.Code,
                line.Code,
                Weighting(line.Weighting),
                Plane(line.Plane),
                (geometry?.Dimensions.Slices ?? line.Source.Slices).ToString(CultureInfo.InvariantCulture),
                geometry is null ? string.Empty : geometry.SliceThicknessMillimetres.ToString("0.##", culture),
                (geometry?.SliceSpacingMillimetres ?? line.Source.SliceSpacingMillimetres).ToString("0.##", culture),
                geometry is null ? string.Empty : geometry.CoverageMillimetres.ToString("0", culture),
                geometry is null ? line.Source.Tier : geometry.Tier.ToString())).ConfigureAwait(false);
        }
    }

    static string Weighting(SeriesWeighting weighting) => weighting switch
    {
        SeriesWeighting.T1 => "T1",
        SeriesWeighting.T2 => "T2",
        SeriesWeighting.Flair => "FLAIR",
        _ => "не определена",
    };

    static string Plane(ImagingPlane plane) => plane switch
    {
        ImagingPlane.Axial => "аксиальная",
        ImagingPlane.Coronal => "корональная",
        ImagingPlane.Sagittal => "сагиттальная",
        _ => "не определена",
    };
}

static async Task WriteRegisterAsync(string path, List<ReadingCase> cases)
{
    // Столбцы — те же, что в задании, и в том же порядке: регистр и задание
    // разъехаться не должны, иначе специалист заполнит не то поле.
    //
    // Возраста и пола здесь нет **намеренно**, хотя в манифесте они есть.
    // Медиана возраста у иНТГ 74 года против 59 у контролей, а 65 лет и старше
    // только 6 контролей из 26: строка «78, F» даёт читателю сильное
    // предположение о группе до того, как он откроет снимок. Для анализа
    // возраст берётся из манифеста по ключу, а читателю он не нужен — в задании
    // нет ни одной шкалы, где требуется поправка на возраст.
    //
    // Код серии — свой у каждого блока, а не один на строку. Индекс, рога и
    // III желудочек измеряются на аксиальной серии, угол и MTA — на корональной,
    // Фазекас — на FLAIR. Один столбец «серия измерений» три кода не вместил бы,
    // а без них нельзя сказать, с какой серии пришло значение, то есть нельзя
    // ответить на вопрос, ради которого чтение и затевалось: T1 против FLAIR.
    //
    // Аппарат, поле, название и толщину серии переписывать не нужно — они уже
    // в описи серий рядом со снимками, выведенные из тех же файлов.
    string[] columns =
    [
        "код исследования",
        "код специалиста",
        "тип чтения (1 первое; 2 повторное)",
        "дата чтения",
        "объёмный раздел применим (0/1)",
        "код серии: аксиальная",
        "код серии: корональная",
        "код серии: FLAIR",
        "код серии: объёмная T1",
        "оценка качества (1 пригодно; 2 ограниченно; 3 непригодно)",
        "ширина передних рогов, мм",
        "поперечник черепа, мм",
        "угол мозолистого тела, °",
        "височный рог справа, мм",
        "височный рог слева, мм",
        "ширина III желудочка, мм",
        "сильвиевы щели, балл",
        "сужение верхних борозд, балл",
        "фокально расширенные борозды, балл",
        "DESH, итоговая оценка",
        "Фазекас перивентрикулярно",
        "Фазекас глубокое белое вещество",
        "перивентрикулярные изменения, балл",
        "MTA справа",
        "MTA слева",
        "окклюзия ликворных путей (0/1)",
        "уровень препятствия",
        "следы операции на ликворных путях (0/1)",
        "пустое турецкое седло (0/1)",
        "иная патология (0/1)",
        "комментарий к иной патологии",
        "файл точек сохранён (0/1)",
        "сомнительный результат (0/1)",
        "нужен пересмотр (0/1)",
        "комментарий",
    ];

    await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));

    // Названия столбцов сами содержат точку с запятой («1 первое; 2 повторное»),
    // и без кавычек таблица разъезжается: заголовок получает больше полей, чем
    // строки, а Excel молча разносит одно название по трём столбцам.
    await writer.WriteLineAsync(string.Join(';', columns.Select(Quote))).ConfigureAwait(false);

    foreach (var item in cases)
    {
        var row = new string[columns.Length];

        Array.Fill(row, string.Empty);
        row[0] = item.Code;

        // Объёмный раздел задание выполняет только там, где автор отметил
        // исследование объёмным. Отметка ставится здесь, а не на глаз: уровень
        // Extended стоит и у серий, которые условию не отвечают, — нужна
        // неконтрастная T1 с полным охватом.
        row[4] = item.Series.Any(line =>
            line.Scanned?.Tier == AcquisitionTier.Extended
            && line.Weighting == SeriesWeighting.T1
            && line.Scanned.IsContrastEnhanced == false
            && line.Scanned.Geometry.CoversVentricles)
            ? "1"
            : "0";

        await writer.WriteLineAsync(string.Join(';', row.Select(Quote))).ConfigureAwait(false);
    }
}

static async Task WriteKeyAsync(string path, List<ReadingCase> cases, int seed, string datasetRoot)
{
    await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(true));

    // Отпечаток состава — не украшение: те же зерно и перестановка дают другие
    // коды, если набор изменился, потому что перемешивается весь список целиком.
    var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
        '|',
        cases.Select(item => item.Study.PseudonymousStudyId)
            .OrderBy(id => id, StringComparer.Ordinal)))))[..16];

    await writer.WriteLineAsync(string.Create(
        CultureInfo.InvariantCulture,
        $"# ключ выгрузки для чтения; зерно {seed}; отпечаток состава {fingerprint}; "
        + $"исследований {cases.Count}; не передаётся специалисту"))
        .ConfigureAwait(false);

    await writer.WriteLineAsync(string.Join(
        ';',
        "код исследования",
        "группа",
        "псевдоним пациента",
        "псевдоним исследования",
        "производный псевдоним исследования",
        "аппарат")).ConfigureAwait(false);

    foreach (var item in cases)
    {
        // Аппарат нужен анализу — и остаётся в ключе, а не в выгрузке: все 26
        // контрольных исследований сняты на SIEMENS Symphony 1,5 Т, а 15 из 25
        // исследований иНТГ — на чём-то другом. То есть «не Symphony» читается
        // как «иНТГ» без единой ошибки.
        var devices = item.Series
            .Select(line => line.Scanner)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);

        await writer.WriteLineAsync(string.Join(
            ';',
            item.Code,
            item.Study.Group,
            item.Study.PseudonymousSubjectId,
            item.Study.PseudonymousStudyId,
            item.Study.DerivedStudyId ?? string.Empty,
            Quote(string.Join(" | ", devices)))).ConfigureAwait(false);
    }
}

static async Task WriteReadMeAsync(string path, int count, int seed)
{
    var text = string.Create(
        CultureInfo.InvariantCulture,
        $"""
        Выгрузка для внешнего чтения

        Исследований: {count}. Каждое — дооперационная МРТ головного мозга,
        обезличенная копия под нейтральным кодом. Порядок кодов случайный, в коде
        нет признака группы: диагноз и ожидаемый ответ вам неизвестны намеренно.

        Раскладка:
          снимки\S-0001\SER01\...   файлы DICOM одной серии
          серии.csv                 опись серий: взвешенность, плоскость, срезы,
                                    толщина, шаг, охват
          регистр.csv               таблица результатов: одна строка на чтение

        В регистре у каждого блока измерений свой столбец кода серии: аксиальная
        (индекс, рога, III желудочек), корональная (угол, MTA, щели), FLAIR
        (Фазекас) и объёмная T1. Код берётся из описи серий. Без него измерение
        не сравнить с автоматическим: серий в исследовании бывает десяток.

        Порядок работы, определения всех показателей, схемы измерений и коды
        причин пропуска — в задании «ТЗ нейрорадиологу». Регистр и задание
        описывают одни и те же столбцы в одном порядке.

        Важное:
          - в ячейку только число; единицы указаны в названии столбца;
          - «0» значит «признака нет», пустая ячейка — «не оценено»;
          - у каждой пустой ячейки должен быть код причины;
          - столбец «объёмный раздел применим» заполнен заранее: 0 означает,
            что подходящей объёмной серии нет и раздел пропускается.

        """);

    await File.WriteAllTextAsync(path, text, new UTF8Encoding(true)).ConfigureAwait(false);
}

static string Quote(string value) =>
    value.Contains(';', StringComparison.Ordinal)
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
        : value;

/// <summary>Серия в выгрузке: под каким кодом уедет и что о ней известно.</summary>
internal sealed record ReadingSeries
{
    public required string Code { get; init; }

    public required DatasetSeries Source { get; init; }

    public required ImagingSeries? Scanned { get; init; }

    public required string Folder { get; init; }

    public required string Scanner { get; init; }

    public required long Bytes { get; init; }

    /// <summary>Взвешенность обходом файлов, с откатом к манифесту.</summary>
    public SeriesWeighting Weighting => this.Scanned?.Weighting
        ?? (Enum.TryParse<SeriesWeighting>(this.Source.Weighting, out var parsed)
            ? parsed
            : SeriesWeighting.Unknown);

    /// <summary>Плоскость серии; из манифеста она не выводится вовсе.</summary>
    public ImagingPlane Plane => this.Scanned?.Geometry.Plane ?? ImagingPlane.Unknown;
}

/// <summary>Исследование в выгрузке под кодом чтения.</summary>
internal sealed record ReadingCase
{
    public required string Code { get; init; }

    public required DatasetStudy Study { get; init; }

    public required List<ReadingSeries> Series { get; init; }
}
