using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.SegmentationCheck;

/// <summary>
/// Правила разбора архивов IXI и сводки по ним.
///
/// Вынесены из Program, чтобы проверяться тестами: взвешенность серии
/// выводится из имени файла, и ошибка здесь направит порог в обратную сторону —
/// сегментация T2 как T1 выбирает ткань вместо ликвора и выглядит как отказ
/// метода, которого на самом деле нет.
/// </summary>
public static class IxiArchive
{
    /// <summary>
    /// Взвешенность серии по имени файла IXI вида <c>IXI002-Guys-0828-T1.nii.gz</c>.
    /// </summary>
    /// <param name="entryName">Имя файла внутри архива.</param>
    /// <returns>Взвешенность; <see cref="SeriesWeighting.Unknown"/>, если имя не опознано.</returns>
    public static SeriesWeighting WeightingOf(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);

        var name = Path.GetFileName(entryName);

        // Суффикс ищется целиком: у PD-серий в имени нет «T1»/«T2», но само
        // слово IXI и номер центра могли бы совпасть с подстрокой по случайности.
        return name.EndsWith("-T1.nii.gz", StringComparison.OrdinalIgnoreCase)
            ? SeriesWeighting.T1
            : name.EndsWith("-T2.nii.gz", StringComparison.OrdinalIgnoreCase)
                ? SeriesWeighting.T2
                : SeriesWeighting.Unknown;
    }

    /// <summary>
    /// Центр сбора по имени файла IXI (Guys, HH, IOP).
    ///
    /// Три центра — три аппарата; сводка без разбивки по ним скрыла бы,
    /// что метод работает на одном аппарате и не работает на другом.
    /// </summary>
    /// <param name="entryName">Имя файла внутри архива.</param>
    /// <returns>Название центра или «?», если имя не опознано.</returns>
    public static string SiteOf(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);

        var parts = Path.GetFileName(entryName).Split('-');

        return parts.Length >= 4 ? parts[1] : "?";
    }

    /// <summary>
    /// Процентиль по отсортированному списку, методом ближайшего ранга.
    /// </summary>
    /// <param name="sorted">Значения по возрастанию.</param>
    /// <param name="percent">Процентиль от 0 до 100.</param>
    /// <returns>Значение процентиля; 0 для пустого списка.</returns>
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percent / 100.0 * sorted.Count);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
