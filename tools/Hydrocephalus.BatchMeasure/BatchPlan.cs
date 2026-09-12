using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.BatchMeasure;

/// <summary>
/// Исследование, выбранное к замеру, и откуда оно взято.
/// </summary>
/// <param name="Scan">Разбор источника, в котором лежит выбранный экземпляр.</param>
/// <param name="Study">Исследование.</param>
/// <param name="Occurrences">В скольких источниках исследование встретилось.</param>
public sealed record PlannedStudy(DicomScanResult Scan, ImagingStudy Study, int Occurrences);

/// <summary>
/// Правила, которые решают, что и куда будет замерено.
///
/// Вынесены из Program, чтобы проверяться тестами: ошибка в любом из них
/// не видна по результату. Замер, прогнанный по исключённой папке, даст
/// правдоподобные цифры по посторонним исследованиям; вывод, записанный внутрь
/// репозитория, окажется в git вместе с измерениями пациентов; дубль, замеренный
/// дважды, удвоит вес пациента в любой сводке.
/// </summary>
public static class BatchPlan
{
    /// <summary>
    /// Папка, исключённая владельцем данных (docs/data/README.md, 2026-09-06):
    /// снимки не относятся к гидроцефалии.
    /// </summary>
    public const string ExcludedSourceName = "Головы";

    /// <summary>
    /// Лежит ли каталог внутри репозитория.
    ///
    /// Результат замера — измерения, привязанные к псевдонимам пациентов,
    /// то есть клинический набор данных. В репозиторий он попасть не должен
    /// ни при каких условиях, и проверка идёт по фактическому расположению,
    /// а не по договорённости.
    /// </summary>
    /// <param name="directory">Каталог вывода.</param>
    /// <returns><see langword="true"/>, если выше по дереву найден репозиторий.</returns>
    public static bool IsInsideRepository(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, ".git"))
                || File.Exists(Path.Combine(current.FullName, "Hydrocephalus.slnx")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Исключён ли источник владельцем данных.
    /// </summary>
    /// <param name="directory">Каталог источника.</param>
    /// <returns><see langword="true"/>, если это исключённая папка.</returns>
    public static bool IsExcludedSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));

        return string.Equals(name, ExcludedSourceName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Выбирает по одному экземпляру каждого исследования.
    ///
    /// Каждый четвёртый пациент выборки лежит более чем в одной папке, и одно
    /// исследование в разных выгрузках бывает неполным по-разному. Берётся
    /// экземпляр с наибольшим числом срезов: он ближе всего к тому, что было
    /// получено на аппарате. При равенстве — первый встреченный, чтобы выбор
    /// не зависел от порядка словаря.
    /// </summary>
    /// <param name="scans">Разборы источников в порядке их перечисления.</param>
    /// <returns>Исследования к замеру, по одному на псевдоним.</returns>
    public static IReadOnlyList<PlannedStudy> ChooseOccurrences(IEnumerable<DicomScanResult> scans)
    {
        ArgumentNullException.ThrowIfNull(scans);

        var chosen = new Dictionary<string, PlannedStudy>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var scan in scans)
        {
            foreach (var study in scan.Studies)
            {
                if (!chosen.TryGetValue(study.PseudonymousStudyId, out var existing))
                {
                    chosen[study.PseudonymousStudyId] = new PlannedStudy(scan, study, 1);
                    order.Add(study.PseudonymousStudyId);
                    continue;
                }

                chosen[study.PseudonymousStudyId] = SliceCount(study) > SliceCount(existing.Study)
                    ? new PlannedStudy(scan, study, existing.Occurrences + 1)
                    : existing with { Occurrences = existing.Occurrences + 1 };
            }
        }

        return [.. order.Select(id => chosen[id])];
    }

    private static int SliceCount(ImagingStudy study) =>
        study.Series.Sum(series => series.Geometry.Dimensions.Slices);
}
