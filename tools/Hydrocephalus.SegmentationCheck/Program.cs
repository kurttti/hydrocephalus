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
    Console.WriteLine("Использование: Hydrocephalus.SegmentationCheck --out <каталог> [--limit N] <IXI-T1.tar> [<IXI-T2.tar>...]");
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

var skip = new HashSet<int> { outIndex, outIndex + 1 };

if (limitIndex >= 0)
{
    skip.Add(limitIndex);
    skip.Add(limitIndex + 1);
}

var archives = args.Where((_, index) => !skip.Contains(index)).ToArray();

Directory.CreateDirectory(args[outIndex + 1]);

await using var results = new StreamWriter(
    Path.Combine(args[outIndex + 1], "ixi-segmentation.csv"),
    append: false,
    Encoding.UTF8);

await results.WriteLineAsync("file,site,weighting,outcome,volume_ml,csf_fraction_of_head_selected,seconds");

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

        var site = IxiArchive.SiteOf(entry.Name);
        var group = site + "/" + weighting;
        var study = Stopwatch.StartNew();

        string outcome;
        double volume = 0;
        string fraction = string.Empty;

        try
        {
            using var content = new MemoryStream();

            await using (var zip = new GZipStream(entry.DataStream, CompressionMode.Decompress, leaveOpen: true))
            {
                await zip.CopyToAsync(content);
            }

            var voxels = NiftiVolumeReader.Parse(content.GetBuffer().AsSpan(0, (int)content.Length));
            var segmentation = BaselineVentricleSegmentation.Segment(voxels, weighting);

            fraction = segmentation.Issues
                .Where(issue => issue.Parameters.ContainsKey("selectedFraction"))
                .Select(issue => issue.Parameters["selectedFraction"])
                .FirstOrDefault() ?? string.Empty;

            volume = RegionVolumes
                .Measure(segmentation.Mask, AcquisitionTier.Extended, segmentation.Quality)
                .Sum(biomarker => biomarker.Value);

            outcome = volume > 0
                ? "volume"
                : segmentation.Quality == MeasurementQuality.Unreliable
                    ? "threshold-failed"
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
            fraction,
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
