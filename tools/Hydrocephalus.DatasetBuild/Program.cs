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
        """
        Использование:
          Hydrocephalus.DatasetBuild <группа>=<каталог> [<группа>=<каталог>...] [--overwrite]
              собрать набор заново;
          Hydrocephalus.DatasetBuild --demographics-only <группа>=<каталог>...
              дополнить манифест возрастом и полом, не трогая файлы набора;
          Hydrocephalus.DatasetBuild --exclude=<таблица.csv> [--apply]
              вынести из набора исследования, помеченные в таблице решений;
              без --apply только печатает, что будет сделано;
          Hydrocephalus.DatasetBuild --add <группа>=<каталог>... [--apply]
              пополнить существующий набор новыми исследованиями, не пересобирая
              его; без --apply только печатает, что будет добавлено.
        """);

    return 2;
}

var groups = new List<(string Group, string Root)>();

foreach (var argument in args)
{
    if (string.Equals(argument, "--overwrite", StringComparison.Ordinal)
        || string.Equals(argument, "--demographics-only", StringComparison.Ordinal)
        || string.Equals(argument, "--add", StringComparison.Ordinal)
        || string.Equals(argument, "--apply", StringComparison.Ordinal)
        || argument.StartsWith("--exclude=", StringComparison.Ordinal))
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

// Пополнение существующего набора.
//
// Зачем отдельно от сборки. Полная пересборка вернула бы исследования,
// вынесенные решениями владельца данных, и переписала бы хеши всех файлов, а
// добавить нужно только новое. Поэтому режим читает существующий манифест,
// пропускает то, что в наборе уже есть, и дописывает остальное.
//
// Исследование считается тем же самым по исходному псевдониму: он выводится из
// UID источника, то есть не зависит ни от имени папки, ни от того, сколько раз
// выгрузку копировали.
if (args.Contains("--add", StringComparer.Ordinal))
{
    var before = await DatasetManifest.ReadAsync(datasetRoot, CancellationToken.None);

    if (before is null)
    {
        Console.Error.WriteLine("Манифеста рядом с набором нет: пополнять нечего, нужна сборка.");

        return 2;
    }

    if (groups.Count == 0)
    {
        Console.Error.WriteLine("Не задано ни одного <группа>=<каталог>.");

        return 2;
    }

    var apply = args.Contains("--apply", StringComparer.Ordinal);
    var options = new DicomImportOptions { PseudonymSalt = salt, MaxFileCount = 1_000_000 };
    var writer = new StudyImporter(
        options,
        new WorkingCopyOptions { RootDirectory = Path.Combine(datasetRoot, ".unused") },
        TimeProvider.System);

    var have = before.Studies
        .Select(study => study.PseudonymousStudyId)
        .ToHashSet(StringComparer.Ordinal);

    var added = new List<DatasetStudy>();
    var skipped = new List<string>();
    var problems = new List<string>();

    Console.WriteLine(apply ? "=== Пополнение ===" : "=== Что будет добавлено (--apply не задан) ===");

    foreach (var (group, root) in groups)
    {
        var folders = Directory.EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"{group}: каталогов в источнике {folders.Count}");

        for (var index = 0; index < folders.Count; index++)
        {
            var number = index + 1;
            DicomScanResult scan;

            try
            {
                scan = await new DicomStudyScanner(options)
                    .ScanAsync(folders[index], CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{group}/{number}: разбор не удался ({error.GetType().Name})");
                continue;
            }

            if (scan.Studies.Count == 0)
            {
                problems.Add($"{group}/{number}: читаемого исследования нет — {scan.DescribeRejections()}");
                continue;
            }

            foreach (var study in scan.Studies)
            {
                if (have.Contains(study.PseudonymousStudyId))
                {
                    skipped.Add($"{group}/{number}: {Head(study.PseudonymousStudyId)} уже в наборе");
                    continue;
                }

                var series = study.Series.Count;
                var age = study.PatientAgeYears;
                var sex = study.PatientSex;

                Console.WriteLine(
                    $"  {group}/{number}: пациент {Head(study.PseudonymousSubjectId)}, "
                    + $"исследование {Head(study.PseudonymousStudyId)}, серий {series}, "
                    + $"возраст {(age is null ? "—" : age.Value.ToString(CultureInfo.InvariantCulture))}, "
                    + $"пол {sex ?? "—"}");

                if (!apply)
                {
                    continue;
                }

                var target = Path.Combine(
                    datasetRoot, group, study.PseudonymousSubjectId, study.PseudonymousStudyId);

                try
                {
                    var copy = await writer.WriteDeidentifiedCopyAsync(
                        scan, study, target, CancellationToken.None);

                    var written = await new DicomStudyScanner(options)
                        .ScanAsync(target, CancellationToken.None);

                    var derived = written.Studies.SingleOrDefault();

                    added.Add(new DatasetStudy
                    {
                        Group = group,
                        PseudonymousSubjectId = study.PseudonymousSubjectId,
                        PseudonymousStudyId = study.PseudonymousStudyId,
                        DerivedSubjectId = derived?.PseudonymousSubjectId,
                        DerivedStudyId = derived?.PseudonymousStudyId,
                        AgeYears = study.PatientAgeYears,
                        Sex = study.PatientSex,
                        Series = [.. copy.Study.Series.Select(item => new DatasetSeries
                        {
                            PseudonymousSeriesId = item.PseudonymousSeriesId,
                            Weighting = item.Weighting.ToString(),
                            Tier = item.Geometry.Tier.ToString(),
                            Slices = item.Geometry.Dimensions.Slices,
                            SliceSpacingMillimetres = item.Geometry.SliceSpacingMillimetres,
                        })],
                        ExcludedSeries = copy.ExcludedSeries.Count,
                        Files = [.. HashFiles(target)],
                    });

                    have.Add(study.PseudonymousStudyId);
                }
                catch (DomainRuleViolationException error)
                {
                    // Прежде всего отказ проверки обезличивания: исследование с
                    // остаточным идентификатором в набор не попадает.
                    Discard(target);
                    problems.Add($"{group}/{number}: {Shorten(error.Message)}");
                }
                catch (Exception error) when (error is IOException or InvalidOperationException)
                {
                    Discard(target);
                    problems.Add($"{group}/{number}: запись не удалась ({error.GetType().Name})");
                }
            }
        }
    }

    Console.WriteLine();

    if (skipped.Count > 0)
    {
        Console.WriteLine($"=== Уже были в наборе ({skipped.Count}) ===");

        foreach (var line in skipped)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
    }

    if (problems.Count > 0)
    {
        Console.WriteLine($"=== Не вошли ({problems.Count}) ===");

        foreach (var line in problems)
        {
            Console.WriteLine("  " + line);
        }

        Console.WriteLine();
    }

    if (!apply)
    {
        Console.WriteLine("Ничего не изменено. Повторить с --apply.");

        return 0;
    }

    var all = before.Studies.Concat(added).ToList();

    // Копия манифеста до пополнения: состав выборки меняется, и прежний состав
    // должен остаться читаемым.
    var backup = Path.Combine(
        datasetRoot,
        $"манифест-до-пополнения-{DateTime.Now:yyyy-MM-dd}.json");

    File.Copy(Path.Combine(datasetRoot, DatasetManifest.FileName), backup, overwrite: true);

    await (before with { Studies = all }).WriteAsync(datasetRoot, CancellationToken.None);

    Console.WriteLine($"добавлено исследований: {added.Count}");
    Console.WriteLine($"в манифесте стало: {all.Count} (было {before.Studies.Count})");

    foreach (var group in all.Select(study => study.Group).Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"  {group,-28} пациентов {Subjects(all, group),3}, исследований {Studies(all, group),3}");
    }

    Console.WriteLine("Копия манифеста до пополнения лежит рядом с набором.");

    return 0;
}

// Вынос исследований из набора по таблице решений владельца данных.
//
// Почему утилитой, а не руками. Исключение затрагивает три места сразу: каталоги
// набора, манифест и знаменатели всех пересчётов. Пока оно держится списком в
// документе, каждый пересчёт обязан этот список применять сам — и однажды не
// применит. Утилита делает вынос один раз и оставляет след: копию манифеста до
// правки и причину по каждому пациенту.
//
// Данные не удаляются: каталоги переносятся в dataset-исключённые\<дата>, то есть
// решение обратимо. Место решения — таблица, которую заполнял владелец данных,
// а не довод командной строки.
var excludeArgument = args.FirstOrDefault(argument =>
    argument.StartsWith("--exclude=", StringComparison.Ordinal));

if (excludeArgument is not null)
{
    var tablePath = excludeArgument["--exclude=".Length..];

    if (!File.Exists(tablePath))
    {
        Console.Error.WriteLine("Таблица решений не найдена.");

        return 2;
    }

    var current = await DatasetManifest.ReadAsync(datasetRoot, CancellationToken.None);

    if (current is null)
    {
        Console.Error.WriteLine("Манифеста рядом с набором нет: выносить нечего.");

        return 2;
    }

    var decisions = ReadDecisions(tablePath);

    if (decisions.Count == 0)
    {
        Console.Error.WriteLine("В таблице нет строк с «исключено» = 1: выносить нечего.");

        return 2;
    }

    // Сверка с набором идёт до любого переноса: решение по пациенту, которого в
    // наборе нет, означает, что таблица и набор разошлись, и тогда неизвестно,
    // что именно выносится. Такое останавливает проход целиком.
    var known = current.Studies
        .GroupBy(study => (study.Group, study.PseudonymousSubjectId))
        .ToDictionary(group => group.Key, group => group.ToList());

    var unknown = decisions
        .Where(decision => !known.ContainsKey((decision.Group, decision.Subject)))
        .ToList();

    if (unknown.Count > 0)
    {
        Console.Error.WriteLine($"В наборе нет {unknown.Count} из {decisions.Count} помеченных:");

        foreach (var decision in unknown)
        {
            Console.Error.WriteLine($"  {decision.Group}/{Head(decision.Subject)}");
        }

        return 2;
    }

    var apply = args.Contains("--apply", StringComparer.Ordinal);

    Console.WriteLine(apply ? "=== Вынос ===" : "=== Что будет сделано (--apply не задан) ===");

    var movedStudies = 0;

    foreach (var decision in decisions.OrderBy(item => item.Subject, StringComparer.Ordinal))
    {
        var studies = known[(decision.Group, decision.Subject)];
        movedStudies += studies.Count;

        Console.WriteLine(
            $"  {decision.Group}/{Head(decision.Subject)}  исследований {studies.Count}  — {decision.Reason}");
    }

    var removed = decisions.Select(item => (item.Group, item.Subject)).ToHashSet();

    var kept = current.Studies
        .Where(study => !removed.Contains((study.Group, study.PseudonymousSubjectId)))
        .ToList();

    Console.WriteLine();
    Console.WriteLine($"пациентов выносится: {decisions.Count}, их исследований: {movedStudies}");

    foreach (var group in current.Studies
        .Select(study => study.Group)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal))
    {
        Console.WriteLine(
            $"  {group,-28} пациентов {Subjects(current.Studies, group),3} -> {Subjects(kept, group),3}"
            + $", исследований {Studies(current.Studies, group),3} -> {Studies(kept, group),3}");
    }

    if (!apply)
    {
        Console.WriteLine();
        Console.WriteLine("Ничего не изменено. Повторить с --apply.");

        return 0;
    }

    var outcome = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Hydrocephalus",
        "dataset-исключённые",
        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    Directory.CreateDirectory(outcome);

    File.Copy(
        Path.Combine(datasetRoot, DatasetManifest.FileName),
        Path.Combine(outcome, "манифест-до-правки.json"),
        overwrite: true);

    await File.WriteAllLinesAsync(
        Path.Combine(outcome, "причины.txt"),
        [
            $"Вынесено из набора по таблице решений владельца данных, {DateTime.Now:yyyy-MM-dd}.",
            "Данные не удалены: каталоги перенесены сюда и могут быть возвращены.",
            string.Empty,
            .. decisions
                .OrderBy(item => item.Subject, StringComparer.Ordinal)
                .Select(item => $"{item.Group}/{item.Subject}  {item.Reason}"),
        ],
        CancellationToken.None);

    foreach (var decision in decisions)
    {
        var source = Path.Combine(datasetRoot, decision.Group, decision.Subject);

        if (!Directory.Exists(source))
        {
            Console.WriteLine($"  каталога {Head(decision.Subject)} уже нет — пропущен");
            continue;
        }

        var destination = Path.Combine(outcome, decision.Group, decision.Subject);

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(source, destination);
    }

    await (current with { Studies = kept }).WriteAsync(datasetRoot, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine("Перенесено. Рядом с вынесенным лежат копия манифеста до правки и причины.");
    Console.WriteLine("Ссылки папки просмотра на вынесенное теперь ведут в пустоту:");
    Console.WriteLine("пометки в их именах сохранились, сами каталоги переехали.");

    return 0;
}

// Возраст и пол — отдельный проход по тем же источникам, без записи файлов.
//
// Зачем отдельно. Поля нужны как ковариаты сравнения групп (возраст — главный
// конфаундер размера желудочков), а пересборка набора ради них обошлась бы
// дороже, чем стоит: она вернула бы исключённые решениями владельца данных
// исследования, переписала бы все хеши файлов и оборвала бы ссылки папки
// просмотра. Поэтому проход читает источник, ничего не пишет в файлы набора и
// дополняет только манифест, сводя записи по исходному псевдониму исследования.
//
// В сами файлы возраст и пол не попадают: профиль обезличивания удаляет
// (0010,1010) и (0010,0040) по-прежнему.
if (args.Contains("--demographics-only", StringComparer.Ordinal))
{
    var existing = await DatasetManifest.ReadAsync(datasetRoot, CancellationToken.None);

    if (existing is null)
    {
        Console.Error.WriteLine("Манифеста рядом с набором нет: дополнять нечего.");

        return 2;
    }

    var scanOptions = new DicomImportOptions
    {
        PseudonymSalt = salt,
        MaxFileCount = 1_000_000,
    };

    var found = new Dictionary<string, (int? Age, string? Sex)>(StringComparer.Ordinal);
    var unreadable = 0;

    foreach (var (group, root) in groups)
    {
        var patients = Directory.EnumerateDirectories(root)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Console.WriteLine($"{group}: каталогов пациентов {patients.Count}");

        foreach (var folder in patients)
        {
            DicomScanResult result;

            try
            {
                result = await new DicomStudyScanner(scanOptions)
                    .ScanAsync(folder, CancellationToken.None);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                unreadable++;
                continue;
            }

            foreach (var study in result.Studies)
            {
                // Источники пересекаются: одно и то же исследование лежит в
                // нескольких папках. Первое названное значение и остаётся.
                var known = found.GetValueOrDefault(study.PseudonymousStudyId);

                found[study.PseudonymousStudyId] = (
                    known.Age ?? study.PatientAgeYears,
                    known.Sex ?? study.PatientSex);
            }
        }
    }

    var filled = existing.Studies
        .Select(study =>
        {
            var characteristics = found.GetValueOrDefault(study.PseudonymousStudyId);

            return study with
            {
                AgeYears = study.AgeYears ?? characteristics.Age,
                Sex = study.Sex ?? characteristics.Sex,
            };
        })
        .ToList();

    await (existing with { Studies = filled }).WriteAsync(datasetRoot, CancellationToken.None);

    Console.WriteLine();
    Console.WriteLine("=== Дополнено ===");
    Console.WriteLine($"исследований в манифесте: {filled.Count}");
    Console.WriteLine($"из них найдено в источнике: {filled.Count(study => found.ContainsKey(study.PseudonymousStudyId))}");
    Console.WriteLine($"возраст известен: {filled.Count(study => study.AgeYears is not null)}");
    Console.WriteLine($"пол известен: {filled.Count(study => study.Sex is not null)}");

    foreach (var group in filled.Select(study => study.Group).Distinct(StringComparer.Ordinal))
    {
        var studies = filled.Where(study =>
            string.Equals(study.Group, group, StringComparison.Ordinal)).ToList();

        var ages = studies
            .Where(study => study.AgeYears is not null)
            .Select(study => study.AgeYears!.Value)
            .Order()
            .ToList();

        Console.WriteLine();
        Console.WriteLine($"{group,-28} исследований {studies.Count,3}  с возрастом {ages.Count,3}");

        if (ages.Count > 0)
        {
            var median = ages.Count % 2 == 1
                ? ages[ages.Count / 2]
                : (ages[(ages.Count / 2) - 1] + ages[ages.Count / 2]) / 2.0;

            Console.WriteLine(
                $"{string.Empty,-28} возраст: медиана {median.ToString("0.#", CultureInfo.InvariantCulture)}, "
                + $"от {ages[0]} до {ages[^1]}");
        }

        foreach (var sex in studies
            .Select(study => study.Sex ?? "не указан")
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{string.Empty,-28} пол {sex.Key}: {sex.Count()}");
        }
    }

    if (unreadable > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"каталогов не прочитано: {unreadable}");
    }

    Console.WriteLine();
    Console.WriteLine("Манифест дополнен; файлы набора не менялись.");

    return 0;
}

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
                    AgeYears = study.PatientAgeYears,
                    Sex = study.PatientSex,
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

/// <summary>Первые восемь знаков псевдонима — столько печатается в отчётах.</summary>
static string Head(string pseudonym) =>
    pseudonym.Length <= 8 ? pseudonym : pseudonym[..8];

/// <summary>Сколько различных пациентов группы в этих записях.</summary>
static int Subjects(IEnumerable<DatasetStudy> studies, string group) =>
    studies.Where(study => string.Equals(study.Group, group, StringComparison.Ordinal))
        .Select(study => study.PseudonymousSubjectId)
        .Distinct(StringComparer.Ordinal)
        .Count();

/// <summary>Сколько исследований группы в этих записях.</summary>
static int Studies(IEnumerable<DatasetStudy> studies, string group) =>
    studies.Count(study => string.Equals(study.Group, group, StringComparison.Ordinal));

/// <summary>
/// Читает из таблицы решений строки, помеченные к выносу.
///
/// Таблица — та самая, которую владелец данных заполнял после просмотра
/// исследований: разделитель «;», кодировка UTF-8, поля с «;» внутри — в
/// кавычках. Нужны четыре столбца: группа, псевдоним пациента, пометка и причина.
/// </summary>
static List<(string Group, string Subject, string Reason)> ReadDecisions(string path)
{
    var lines = File.ReadAllLines(path, Encoding.UTF8);

    if (lines.Length < 2)
    {
        return [];
    }

    var header = SplitRow(lines[0].TrimStart('\uFEFF'));

    int Column(string name) =>
        header.FindIndex(value => string.Equals(value.Trim(), name, StringComparison.Ordinal));

    var groupAt = Column("группа");
    var subjectAt = Column("псевдоним пациента");
    var flagAt = Column("исключено (0/1)");
    var reasonAt = Column("причина исключения");

    if (groupAt < 0 || subjectAt < 0 || flagAt < 0)
    {
        throw new InvalidOperationException(
            "В таблице нет столбцов «группа», «псевдоним пациента», «исключено (0/1)».");
    }

    var decisions = new List<(string Group, string Subject, string Reason)>();

    foreach (var line in lines.Skip(1))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var cells = SplitRow(line);

        string Cell(int index) =>
            index >= 0 && index < cells.Count ? cells[index].Trim() : string.Empty;

        if (!string.Equals(Cell(flagAt), "1", StringComparison.Ordinal))
        {
            continue;
        }

        var reason = Cell(reasonAt);

        decisions.Add((
            Cell(groupAt),
            Cell(subjectAt),
            reason.Length == 0 ? "причина в таблице не указана" : reason));
    }

    return decisions;
}

/// <summary>Делит строку CSV по «;», уважая кавычки и удвоенную кавычку внутри них.</summary>
static List<string> SplitRow(string line)
{
    var cells = new List<string>();
    var cell = new StringBuilder();
    var quoted = false;

    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];

        if (character == '"')
        {
            if (quoted && index + 1 < line.Length && line[index + 1] == '"')
            {
                cell.Append('"');
                index++;
            }
            else
            {
                quoted = !quoted;
            }

            continue;
        }

        if (character == ';' && !quoted)
        {
            cells.Add(cell.ToString());
            cell.Clear();

            continue;
        }

        cell.Append(character);
    }

    cells.Add(cell.ToString());

    return cells;
}

static string Shorten(string message) =>
    message.Length <= 120 ? message : message[..120] + "…";
