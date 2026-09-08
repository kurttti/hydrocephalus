using System.Globalization;
using System.Text;
using FellowOakDicom;

// Раскладка исходной выборки по пациентам для врачебной рецензии.
//
// В отличие от Hydrocephalus.Inventory эта утилита намеренно работает
// с настоящими именами и путями: её результат нужен владельцу данных, чтобы
// открыть снимки конкретного пациента и поставить диагноз. По псевдониму
// пациента опознать нельзя, а именно это здесь и требуется.
//
// Из этого следуют границы: утилита запускается только владельцем данных
// на своей машине, её вывод никогда не попадает ни в репозиторий, ни в CI,
// ни в журнал сборки. В консоль печатаются только количества; имена уходят
// в файл плана рядом с целевым каталогом.
//
// Утилита только читает исходные каталоги и ничего в них не меняет.
if (args.Length < 2)
{
    Console.Error.WriteLine(
        "Использование: Hydrocephalus.ReviewPlan <файл-плана> <каталог> [<каталог>...]");
    return 2;
}

var planPath = args[0];
var sources = args[1..];

var missing = sources.Where(path => !Directory.Exists(path)).ToArray();

if (missing.Length > 0)
{
    Console.Error.WriteLine($"Не найдено каталогов: {missing.Length}.");
    return 2;
}

// Группировка повторяет правило дедупликации из docs/data/README.md, по которому
// получены 135 уникальных пациентов: ключ — PatientID, PatientName и дата
// рождения, а при полностью пустых полях — StudyInstanceUID. Ложное разделение
// безопаснее ложного объединения: один пациент в двух папках рецензии — потеря
// времени, двое разных в одной — неверный диагноз.
var subjects = new Dictionary<string, Subject>(StringComparer.Ordinal);

// Какие пациенты встречаются под каждым каталогом. Нужно, чтобы выбрать для
// копирования наибольшие каталоги, принадлежащие одному пациенту: структура
// источников неоднородна (PA*/ST*/SE*, папка на пациента, плоский список),
// и заранее назвать уровень «папка пациента» нельзя.
var subjectsByDirectory = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

var scanned = 0;
var recognised = 0;

foreach (var source in sources)
{
    Console.WriteLine($"Разбор: {Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))}…");

    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        scanned++;

        DicomDataset dataset;

        try
        {
            dataset = DicomFile.Open(file, FileReadOption.SkipLargeTags).Dataset;
        }
        catch (Exception exception) when (exception is DicomFileException or IOException or InvalidOperationException)
        {
            continue;
        }

        var patientId = Text(dataset, DicomTag.PatientID);
        var patientName = Text(dataset, DicomTag.PatientName);
        var birthDate = Text(dataset, DicomTag.PatientBirthDate);
        var studyUid = Text(dataset, DicomTag.StudyInstanceUID);
        var seriesUid = Text(dataset, DicomTag.SeriesInstanceUID);

        if (string.IsNullOrEmpty(seriesUid))
        {
            continue;
        }

        recognised++;

        var key = SubjectKey(patientId, patientName, birthDate, studyUid);

        if (!subjects.TryGetValue(key, out var subject))
        {
            subject = new Subject(key);
            subjects[key] = subject;
        }

        subject.Observe(patientId, patientName, birthDate, studyUid, seriesUid, source, file);

        // Файл запоминается вместе со своим каталогом: если каталог окажется
        // общим для нескольких пациентов, копировать его целиком нельзя,
        // и такие файлы придётся переносить поштучно.
        subject.Files.Add(file);

        // Ключ пациента поднимается вверх по дереву до корня источника: каталог
        // помечается всеми пациентами, чьи файлы лежат под ним.
        var directory = Path.GetDirectoryName(file);

        while (directory is not null && directory.Length >= source.Length)
        {
            if (!subjectsByDirectory.TryGetValue(directory, out var seen))
            {
                seen = new HashSet<string>(StringComparer.Ordinal);
                subjectsByDirectory[directory] = seen;
            }

            seen.Add(key);

            if (string.Equals(directory, source.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }
}

// Для каждого пациента — наибольшие каталоги, под которыми лежат файлы только
// его одного. Спуск останавливается там, где каталог перестаёт быть общим:
// копировать выше значило бы принести в папку рецензии чужие снимки.
foreach (var source in sources)
{
    Collect(source.TrimEnd(Path.DirectorySeparatorChar));
}

void Collect(string directory)
{
    if (!subjectsByDirectory.TryGetValue(directory, out var seen))
    {
        return;
    }

    if (seen.Count == 1)
    {
        subjects[seen.First()].Directories.Add(directory);
        return;
    }

    foreach (var child in Directory.EnumerateDirectories(directory))
    {
        Collect(child);
    }
}

// Файлы, не попавшие ни в один выбранный каталог. Они лежат прямо в каталогах,
// общих для нескольких пациентов: скопировать такой каталог целиком значило бы
// принести врачу чужие снимки, а пропустить — потерять часть исследования.
foreach (var subject in subjects.Values)
{
    foreach (var file in subject.Files)
    {
        var directory = Path.GetDirectoryName(file);

        var covered = subject.Directories.Any(chosen =>
            directory is not null
            && (directory.Equals(chosen, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(chosen + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));

        if (!covered)
        {
            subject.LooseFiles.Add(file);
        }
    }
}

// Имена папок рецензии: сначала имя пациента из тегов, иначе имя исходной папки.
// Оба варианта опознаваемы врачом, а псевдоним — нет.
var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

foreach (var subject in subjects.Values.OrderBy(item => item.DisplayName, StringComparer.CurrentCulture))
{
    var name = Sanitize(subject.DisplayName);

    if (used.TryGetValue(name, out var count))
    {
        used[name] = count + 1;
        name = $"{name} ({count + 1})";
    }
    else
    {
        used[name] = 1;
    }

    subject.FolderName = name;
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(planPath))!);

await using (var writer = new StreamWriter(planPath, append: false, new UTF8Encoding(true)))
{
    await writer.WriteLineAsync(string.Join(
        '\t',
        "ПапкаРецензии",
        "ИмяИзТегов",
        "PatientID",
        "ДатаРождения",
        "Исследований",
        "Серий",
        "Источников",
        "ОтдельныхФайлов",
        "ИсходныеКаталоги"));

    foreach (var subject in subjects.Values.OrderBy(item => item.FolderName, StringComparer.CurrentCulture))
    {
        await writer.WriteLineAsync(string.Join(
            '\t',
            subject.FolderName,
            subject.DisplayName,
            subject.PatientId,
            subject.BirthDate,
            subject.Studies.Count.ToString(CultureInfo.InvariantCulture),
            subject.Series.Count.ToString(CultureInfo.InvariantCulture),
            subject.Sources.Count.ToString(CultureInfo.InvariantCulture),
            subject.LooseFiles.Count.ToString(CultureInfo.InvariantCulture),
            string.Join('|', subject.Directories.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))));
    }
}

// Список одиночных файлов пишется отдельно: он длинный и нужен только
// копированию, а не чтению человеком.
var loosePath = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(planPath))!,
    "_отдельные файлы.tsv");

await using (var writer = new StreamWriter(loosePath, append: false, new UTF8Encoding(true)))
{
    await writer.WriteLineAsync(string.Join('	', "ПапкаРецензии", "Файл"));

    foreach (var subject in subjects.Values.OrderBy(item => item.FolderName, StringComparer.CurrentCulture))
    {
        foreach (var file in subject.LooseFiles.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            await writer.WriteLineAsync(subject.FolderName + "	" + file);
        }
    }
}

// В консоль — только количества.
Console.WriteLine();
Console.WriteLine($"файлов просмотрено: {scanned}");
Console.WriteLine($"из них DICOM с серией: {recognised}");
Console.WriteLine($"уникальных пациентов: {subjects.Count}");
Console.WriteLine($"пациентов более чем в одном источнике: {subjects.Values.Count(item => item.Sources.Count > 1)}");
Console.WriteLine($"каталогов к копированию: {subjects.Values.Sum(item => item.Directories.Count)}");
Console.WriteLine($"отдельных файлов к копированию: {subjects.Values.Sum(item => item.LooseFiles.Count)}");
Console.WriteLine($"пациентов без имени в тегах: {subjects.Values.Count(item => string.IsNullOrWhiteSpace(item.PatientName))}");
Console.WriteLine();
Console.WriteLine("План записан. Имена пациентов есть только в файле плана, не в этом выводе.");

return 0;

static string Text(DicomDataset dataset, DicomTag tag) =>
    dataset.GetSingleValueOrDefault(tag, string.Empty)?.Trim() ?? string.Empty;

static string SubjectKey(string patientId, string patientName, string birthDate, string studyUid)
{
    if (string.IsNullOrEmpty(patientId)
        && string.IsNullOrEmpty(patientName)
        && string.IsNullOrEmpty(birthDate))
    {
        return "study:" + studyUid;
    }

    return string.Create(
        CultureInfo.InvariantCulture,
        $"id:{patientId.ToUpperInvariant()}|{patientName.ToUpperInvariant()}|{birthDate.ToUpperInvariant()}");
}

static string Sanitize(string value)
{
    var builder = new StringBuilder();

    foreach (var character in value.Replace('^', ' ').Trim())
    {
        builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
    }

    var name = builder.ToString().Trim().Trim('.');

    return string.IsNullOrWhiteSpace(name) ? "БЕЗ ИМЕНИ" : name;
}

/// <summary>Один пациент выборки и всё, что о нём известно из тегов.</summary>
internal sealed class Subject(string key)
{
    internal string Key { get; } = key;

    internal string PatientId { get; private set; } = string.Empty;

    internal string PatientName { get; private set; } = string.Empty;

    internal string BirthDate { get; private set; } = string.Empty;

    internal string? SourceFolder { get; private set; }

    internal HashSet<string> Studies { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Series { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal List<string> Files { get; } = [];

    internal List<string> LooseFiles { get; } = [];

    internal HashSet<string> FolderCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal string FolderName { get; set; } = string.Empty;

    /// <summary>Имя для папки рецензии: из тегов, иначе имя исходной папки.</summary>
    /// <summary>
    /// Имя для папки рецензии. Берётся самое длинное имя исходной папки: в этой
    /// выборке к фамилии часто дописаны диагноз или дата, и врачу это полезно.
    /// Имя из тегов — запасной вариант, а не основной.
    /// </summary>
    internal string DisplayName =>
        this.FolderCandidates.Count > 0
            ? this.FolderCandidates.MaxBy(item => item.Length)!
            : !string.IsNullOrWhiteSpace(this.PatientName)
                ? this.PatientName
                : this.SourceFolder ?? "БЕЗ ИМЕНИ";

    internal void Observe(
        string patientId,
        string patientName,
        string birthDate,
        string studyUid,
        string seriesUid,
        string source,
        string file)
    {
        // Имя папки под корнем источника — это фамилия, как её знает врач.
        // Имя из тегов для этого непригодно: в выборке оно записано латиницей,
        // а у части пациентов букв в нём нет вовсе.
        var relative = file[source.TrimEnd(Path.DirectorySeparatorChar).Length..].TrimStart(Path.DirectorySeparatorChar);
        var segments = relative.Split(Path.DirectorySeparatorChar);

        if (segments.Length > 1)
        {
            this.FolderCandidates.Add(segments[0]);
        }

        // Первое непустое значение выигрывает: часть файлов серии может нести
        // пустые поля, и затирать ими уже известное имя нельзя.
        if (this.PatientId.Length == 0)
        {
            this.PatientId = patientId;
        }

        if (this.PatientName.Length == 0)
        {
            this.PatientName = patientName;
        }

        if (this.BirthDate.Length == 0)
        {
            this.BirthDate = birthDate;
        }

        this.SourceFolder ??= Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));

        if (studyUid.Length > 0)
        {
            this.Studies.Add(studyUid);
        }

        this.Series.Add(seriesUid);
        this.Sources.Add(source);
    }
}
