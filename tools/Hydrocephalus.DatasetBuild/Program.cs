using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Dicom;

// Сборка замороженного обезличенного набора выборки.
//
// Зачем. Исходники лежат на внешнем диске в единственном экземпляре: он упадёт —
// и материал кончится вместе с ним. Работать с него медленно, а отдать наружу
// для внешней валидации (M5) нельзя — имена папок содержат фамилии. Рабочие
// копии приложения для этого не годятся: их ключ защищён DPAPI и привязан к
// учётной записи на этой машине, то есть копия не переносится и не
// восстанавливается нигде больше.
//
// Открытыми файлами — решение владельца данных от 2026-09-29. Персональных
// данных в наборе нет: остаточные идентификаторы ловит та же проверка, что и при
// импорте, и при её срабатывании исследование в набор не попадает.
//
// Раскладка ведёт соответствие исходным псевдонимам, и это не удобство. Обезличивание
// переписывает UID, поэтому разбор копии вывел бы псевдоним от псевдонима: другое
// исследование, другой пациент. Измерения по набору тогда не совпали бы ни с
// группами, ни с прежними отчётами. Поэтому группа и исходные псевдонимы записаны
// в раскладке и в манифесте, а не выводятся заново.
//
// Утилита только читает источник. Ни имён файлов, ни имён папок она не печатает:
// в них фамилии.
if (args.Length == 0)
{
    Console.Error.WriteLine(
        "Использование: Hydrocephalus.DatasetBuild <группа>=<каталог> [<группа>=<каталог>...]");

    return 2;
}

var groups = new List<(string Group, string Root)>();

foreach (var argument in args)
{
    if (string.Equals(argument, "--overwrite", StringComparison.Ordinal))
    {
        continue;
    }

    var separator = argument.IndexOf('=', StringComparison.Ordinal);

    if (separator <= 0 || separator == argument.Length - 1)
    {
        Console.Error.WriteLine("Каждый довод задаётся как <группа>=<каталог>.");

        return 2;
    }

    var root = argument[(separator + 1)..];

    if (!Directory.Exists(root))
    {
        // Путь не печатается: он называет папку с фамилиями.
        Console.Error.WriteLine($"Каталог группы «{argument[..separator]}» не найден.");

        return 2;
    }

    groups.Add((argument[..separator], root));
}

// Соль установочная, та же, что у приложения: с другой псевдонимы набора
// не сойдутся с теми, что стоят в отчётах и в счёте групп.
var saltPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Hydrocephalus",
    "secrets",
    "pseudonym-salt.bin");

var salt = await PseudonymSaltStore.GetOrCreateAsync(saltPath, CancellationToken.None);

var datasetRoot = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Hydrocephalus",
    "dataset");

// Набор целиком выводится из источника, но существующий не дополняется молча:
// прогон с другими именами групп оставил бы прежние рядом, и одни и те же
// пациенты оказались бы в наборе дважды под разными именами. Счёт по группам
// после такого неверен, а причина невидима.
if (Directory.Exists(datasetRoot) && Directory.EnumerateFileSystemEntries(datasetRoot).Any())
{
    if (!args.Contains("--overwrite", StringComparer.Ordinal))
    {
        Console.Error.WriteLine(
            "Набор уже существует. Повторная сборка стирает прежний: добавьте --overwrite.");

        return 2;
    }

    Directory.Delete(datasetRoot, recursive: true);
}

var importOptions = new DicomImportOptions
{
    PseudonymSalt = salt,

    // Выборка обходится целиком, а лимит по умолчанию рассчитан на одно исследование.
    MaxFileCount = 1_000_000,
};

var importer = new StudyImporter(
    importOptions,
    new WorkingCopyOptions
    {
        // Рабочие копии здесь не создаются: набор пишется открытыми файлами.
        // Корень всё же задаётся, потому что его требует конструктор.
        RootDirectory = Path.Combine(datasetRoot, ".unused"),
    },
    TimeProvider.System);

var entries = new List<DatasetStudy>();
var refused = new List<string>();
var subjects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

foreach (var (group, root) in groups)
{
    var folders = Directory.EnumerateDirectories(root)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    Console.WriteLine($"{group}: каталогов пациентов {folders.Count}");

    // Не присваивание: группа иНТГ собрана из двух папок, и второй проход
    // обнулил бы счёт пациентов первого.
    if (!subjects.TryGetValue(group, out var counted))
    {
        counted = new HashSet<string>(StringComparer.Ordinal);
        subjects[group] = counted;
    }

    for (var index = 0; index < folders.Count; index++)
    {
        var number = index + 1;

        DicomScanResult scan;

        try
        {
            scan = await new DicomStudyScanner(importOptions)
                .ScanAsync(folders[index], CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            refused.Add($"{group}/{number}: разбор не удался ({error.GetType().Name})");
            continue;
        }

        if (scan.Studies.Count == 0)
        {
            refused.Add($"{group}/{number}: читаемого исследования нет — {scan.DescribeRejections()}");
            continue;
        }

        foreach (var study in scan.Studies)
        {
            var target = Path.Combine(
                datasetRoot,
                group,
                study.PseudonymousSubjectId,
                study.PseudonymousStudyId);

            try
            {
                var copy = await importer.WriteDeidentifiedCopyAsync(
                    scan, study, target, CancellationToken.None);

                counted.Add(study.PseudonymousSubjectId);

                // Записанная копия разбирается ещё раз — ради псевдонимов, которые
                // из неё выйдут. Обезличивание переписывает UID, поэтому разбор
                // копии даёт псевдоним от псевдонима, а при пустом PatientID —
                // отдельного «пациента» на каждое исследование. Без этой пары
                // измерения, сделанные по набору, не легли бы ни в одну группу:
                // проверено, у первого же исследования исходный b7141aa5…
                // превращается в 2193b2fb….
                var written = await new DicomStudyScanner(importOptions)
                    .ScanAsync(target, CancellationToken.None);

                var derived = written.Studies.SingleOrDefault();

                entries.Add(new DatasetStudy
                {
                    Group = group,
                    PseudonymousSubjectId = study.PseudonymousSubjectId,
                    PseudonymousStudyId = study.PseudonymousStudyId,
                    DerivedSubjectId = derived?.PseudonymousSubjectId,
                    DerivedStudyId = derived?.PseudonymousStudyId,
                    Series = [.. copy.Study.Series.Select(series => new DatasetSeries
                    {
                        PseudonymousSeriesId = series.PseudonymousSeriesId,
                        Weighting = series.Weighting.ToString(),
                        Tier = series.Geometry.Tier.ToString(),
                        Slices = series.Geometry.Dimensions.Slices,
                        SliceSpacingMillimetres = series.Geometry.SliceSpacingMillimetres,
                    })],
                    ExcludedSeries = copy.ExcludedSeries.Count,
                    Files = [.. HashFiles(target)],
                });
            }
            catch (DomainRuleViolationException error)
            {
                // Прежде всего отказ проверки обезличивания: исследование с
                // остаточным идентификатором в набор не попадает. Частично
                // записанное убирается — иначе оно выглядело бы пригодным.
                Discard(target);
                refused.Add($"{group}/{number}: {Shorten(error.Message)}");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException)
            {
                Discard(target);
                refused.Add($"{group}/{number}: запись не удалась ({error.GetType().Name})");
            }
        }

        Console.Out.Flush();
    }
}

var manifest = new DatasetManifest
{
    BuiltAt = DateTimeOffset.UtcNow,
    BuiltBy = BuildProvenance.CommitShaOf(typeof(StudyImporter).Assembly),
    Studies = entries,
};

await manifest.WriteAsync(datasetRoot, CancellationToken.None);

Console.WriteLine();
Console.WriteLine("=== Записано ===");

foreach (var group in groups.Select(item => item.Group).Distinct(StringComparer.Ordinal))
{
    var studies = entries.Count(entry => string.Equals(entry.Group, group, StringComparison.Ordinal));

    Console.WriteLine(
        $"{group,-28} пациентов {subjects[group].Count,3}  исследований {studies,3}");
}

Console.WriteLine($"файлов всего: {entries.Sum(entry => entry.Files.Count)}");

if (refused.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine($"=== Не вошли ({refused.Count}) ===");

    foreach (var line in refused)
    {
        Console.WriteLine("  " + line);
    }
}

Console.WriteLine();
Console.WriteLine("Манифест записан рядом с набором.");

return 0;

static IEnumerable<DatasetFile> HashFiles(string directory) =>
    Directory.EnumerateFiles(directory, "*.dcm", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => new DatasetFile
        {
            // Путь относительный и целиком из псевдонимов: раскладка внутри
            // каталога построена по ним же.
            Path = Path.GetRelativePath(directory, path).Replace('\\', '/'),
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        });

static void Discard(string directory)
{
    if (Directory.Exists(directory))
    {
        Directory.Delete(directory, recursive: true);
    }

    // Каталог пациента убирается следом, если он опустел: иначе от отклонённого
    // исследования остаётся пустая папка, и пациент выглядит вошедшим в набор.
    var subject = Path.GetDirectoryName(directory);

    if (subject is not null
        && Directory.Exists(subject)
        && !Directory.EnumerateFileSystemEntries(subject).Any())
    {
        Directory.Delete(subject);
    }
}

static string Shorten(string message) =>
    message.Length <= 120 ? message : message[..120] + "…";
