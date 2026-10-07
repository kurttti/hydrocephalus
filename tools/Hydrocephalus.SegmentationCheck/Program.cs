using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Nifti;
using Hydrocephalus.SegmentationCheck;

// Проверка базовой сегментации желудочков на публичном наборе IXI.
//
// Пакетный замер ретроспективной выборки показал, что на реальных T1 порог
// ликвора не срабатывает ни разу (docs/data/README.md). IXI — около шестисот
// здоровых взрослых с T1 и T2 на трёх аппаратах, под лицензией CC BY-SA 3.0.
// Разметки желудочков в IXI нет, поэтому проверяется не точность, а более
// простой вопрос: находит ли метод вообще что-то правдоподобное, и на каких
// аппаратах и взвешенностях отказывает.
//
// Архивы читаются как есть, без распаковки: 9 ГБ снимков на диске ради одного
// прохода не нужны. Пофайловые результаты пишутся в --out вне репозитория;
// данные публичные, но набор тяжёлый и пересчитывается, а в репозитории
// остаются только агрегаты.
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(
        "Использование: Hydrocephalus.SegmentationCheck --out <каталог> [--limit N]"
        + " [--model <модель.onnx>] <IXI-T1.tar> [<IXI-T2.tar>...]");
    return args.Length == 0 ? 2 : 0;
}

var outIndex = Array.IndexOf(args, "--out");
var limitIndex = Array.IndexOf(args, "--limit");

if (outIndex < 0 || outIndex == args.Length - 1)
{
    Console.Error.WriteLine("Не задан --out.");
    return 2;
}

var limit = limitIndex >= 0 && limitIndex < args.Length - 1
    ? int.Parse(args[limitIndex + 1], CultureInfo.InvariantCulture)
    : int.MaxValue;

var modelIndex = Array.IndexOf(args, "--model");
var skip = new HashSet<int> { outIndex, outIndex + 1 };

if (limitIndex >= 0)
{
    skip.Add(limitIndex);
    skip.Add(limitIndex + 1);
}

if (modelIndex >= 0)
{
    skip.Add(modelIndex);
    skip.Add(modelIndex + 1);
}

var archives = args.Where((_, index) => !skip.Contains(index)).ToArray();

Directory.CreateDirectory(args[outIndex + 1]);

var resultsPath = Path.Combine(args[outIndex + 1], "ixi-segmentation.csv");

// Уже посчитанное читается и пропускается: с моделью серия занимает около
// минуты, и прерванный прогон иначе начинался бы сначала. Ключ — имя файла
// серии: оно уникально в наборе и уже стоит первым столбцом.
var alreadyDone = new HashSet<string>(StringComparer.Ordinal);

if (File.Exists(resultsPath))
{
    foreach (var line in await File.ReadAllLinesAsync(resultsPath))
    {
        var comma = line.IndexOf(',', StringComparison.Ordinal);

        if (comma > 0 && !line.StartsWith("file,", StringComparison.Ordinal))
        {
            alreadyDone.Add(line[..comma]);
        }
    }

    Console.WriteLine($"Уже посчитано: {alreadyDone.Count}; эти серии пропускаются.");
}

await using var results = new StreamWriter(resultsPath, append: alreadyDone.Count > 0, Encoding.UTF8);

if (alreadyDone.Count == 0)
{
    await results.WriteLineAsync(
        "file,site,weighting,outcome,volume_ml,model_ml,refusal_detail,seconds");
}

// Модель подключается по желанию: веса лежат вне репозитория и в CI их нет
// (ADR 0009). Без неё столбец остаётся пустым, и инструмент работает как прежде.
using var model = modelIndex >= 0 && modelIndex < args.Length - 1
    ? new OnnxVentricleSegmentation(args[modelIndex + 1])
    : null;

var outcomes = new Dictionary<string, int>(StringComparer.Ordinal);
var volumes = new Dictionary<string, List<double>>(StringComparer.Ordinal);
var watch = Stopwatch.StartNew();
var processed = 0;

foreach (var archive in archives)
{
    // Архив может ещё докачиваться: чтение не должно мешать записи.
    await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var tar = new TarReader(file);

    var perArchive = 0;

    while (await tar.GetNextEntryAsync() is { } entry)
    {
        if (perArchive >= limit)
        {
            break;
        }

        if (entry.DataStream is null)
        {
            continue;
        }

        var weighting = IxiArchive.WeightingOf(entry.Name);

        if (weighting == SeriesWeighting.Unknown)
        {
            continue;
        }

        perArchive++;

        // Счётчик увеличивается и для пропущенных: --limit задаёт, сколько серий
        // архива охватить, а не сколько посчитать в этот раз. Иначе продолжение
        // прогона уехало бы дальше заданной границы.
        if (alreadyDone.Contains(Path.GetFileName(entry.Name)))
        {
            continue;
        }

        var site = IxiArchive.SiteOf(entry.Name);
        var group = site + "/" + weighting;
        var study = Stopwatch.StartNew();

        string outcome;
        double volume = 0;
        double modelVolume = -1;
        string detail = string.Empty;

        try
        {
            using var content = new MemoryStream();

            await using (var zip = new GZipStream(entry.DataStream, CompressionMode.Decompress, leaveOpen: true))
            {
                await zip.CopyToAsync(content);
            }

            var voxels = NiftiVolumeReader.Parse(content.GetBuffer().AsSpan(0, (int)content.Length));
            var segmentation = BaselineVentricleSegmentation.Segment(voxels, weighting);

            // Отказ называется своей причиной: «порог не отделил ликвор»
            // и «маска неправдоподобно мала» — разные поломки метода.
            var refusal = segmentation.Issues
                .FirstOrDefault(issue => issue.Severity == Hydrocephalus.Domain.Quality.QualityIssueSeverity.Blocking);

            if (refusal is not null)
            {
                detail = refusal.Parameters.GetValueOrDefault("selectedFraction")
                    ?? refusal.Parameters.GetValueOrDefault("millilitres")
                    ?? string.Empty;
            }

            volume = RegionVolumes
                .Measure(segmentation.Mask, AcquisitionTier.Extended, segmentation.Quality)
                .Sum(biomarker => biomarker.Value);

            if (model is not null)
            {
                // Пара на одной и той же серии: порог ворот расхождения выводится
                // из того, насколько два независимых пути расходятся там, где оба
                // дают ответ.
                var labels = model.Segment(VolumeConforming.Conform(voxels), null, CancellationToken.None);
                var grid = voxels.Grid;
                long count = 0;

                foreach (var value in VolumeConforming.ProjectBack(labels, voxels))
                {
                    count += value;
                }

                modelVolume = count * grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres
                    * grid.SliceSpacingMillimetres / 1000.0;
            }

            outcome = volume > 0
                ? "volume"
                : refusal is not null
                    ? "refused:" + refusal.Parameters.GetValueOrDefault("reason", "unknown")
                    : "empty-mask";

            if (volume > 0)
            {
                if (!volumes.TryGetValue(group, out var list))
                {
                    list = [];
                    volumes[group] = list;
                }

                list.Add(volume);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or Hydrocephalus.Domain.DomainRuleViolationException)
        {
            outcome = "error:" + exception.GetType().Name;
        }

        var key = group + " " + outcome;
        outcomes[key] = outcomes.GetValueOrDefault(key) + 1;

        await results.WriteLineAsync(string.Join(
            ',',
            Path.GetFileName(entry.Name),
            site,
            weighting,
            outcome,
            volume.ToString("0.0", CultureInfo.InvariantCulture),
            modelVolume < 0 ? string.Empty : modelVolume.ToString("0.0", CultureInfo.InvariantCulture),
            detail,
            study.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)));

        processed++;

        if (processed % 25 == 0)
        {
            Console.WriteLine($"Обработано {processed}…");
            await results.FlushAsync();
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== Исходы (центр/взвешенность исход) ===");

foreach (var (key, value) in outcomes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
{
    Console.WriteLine($"{key,-40} {value}");
}

Console.WriteLine();
Console.WriteLine("=== Объём там, где он получен, мл (P5 / медиана / P95) ===");

foreach (var (group, list) in volumes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
{
    list.Sort();

    Console.WriteLine(string.Create(
        CultureInfo.InvariantCulture,
        $"{group,-20} n={list.Count,-5} {IxiArchive.Percentile(list, 5):0.0} / {IxiArchive.Percentile(list, 50):0.0} / {IxiArchive.Percentile(list, 95):0.0}"));
}

Console.WriteLine();
Console.WriteLine($"Обработано серий: {processed}; время {watch.Elapsed.TotalMinutes:0.0} мин.");

return 0;
