using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Выбор окна и уровня для показа объёма.
///
/// Порядок задан ADR 0007: сначала значения из DICOM, потому что их выбрал тот,
/// кто снимал, и врач ожидает увидеть привычную картинку. Расчёт по гистограмме —
/// откат для случая, когда тегов нет или их значения недостоверны.
///
/// «Недостоверны» проверяется по самому объёму: окно, целиком лежащее вне
/// диапазона значений, даёт равномерно чёрный или белый экран. Такая картинка
/// выглядит как отсутствие данных, а не как ошибка настройки, поэтому доверять
/// тегам без проверки нельзя.
/// </summary>
public static class WindowLevelSelector
{
    /// <summary>Доля значений, отсекаемая снизу при расчёте по гистограмме.</summary>
    public const double LowerPercentile = 0.02;

    /// <summary>Доля значений, отсекаемая сверху при расчёте по гистограмме.</summary>
    public const double UpperPercentile = 0.98;

    /// <summary>Число корзин гистограммы.</summary>
    private const int BinCount = 1024;

    /// <summary>
    /// Выбирает окно для объёма.
    /// </summary>
    /// <param name="volume">Загруженный объём.</param>
    /// <returns>Окно из тегов, если оно пригодно, иначе рассчитанное по гистограмме.</returns>
    public static WindowLevel For(VoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return For(volume, volume.SuggestedWindow);
    }

    /// <summary>
    /// Выбирает окно для объёма с заданной подсказкой.
    ///
    /// Перегрузка для объёмов, у которых тегов нет: приведённый к другой сетке
    /// объём — это те же данные, и подсказка исходной серии для него остаётся
    /// верной, но взять её ему неоткуда.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="suggested">Окно из тегов серии либо <see langword="null"/>.</param>
    /// <returns>Подсказка, если она пригодна, иначе рассчитанное по гистограмме.</returns>
    public static WindowLevel For(IVoxelVolume volume, WindowLevel? suggested)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return suggested is { } window && IsPlausible(window, volume)
            ? window
            : FromHistogram(volume);
    }

    /// <summary>
    /// Рассчитывает окно по гистограмме значений объёма.
    ///
    /// Края отсекаются процентилями: одиночный выброс — металлический артефакт
    /// или дефектный воксель — растянул бы окно так, что вся ткань стала бы
    /// одинаково серой.
    /// </summary>
    /// <param name="volume">Загруженный объём.</param>
    /// <returns>Рассчитанное окно.</returns>
    public static WindowLevel FromHistogram(IVoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var minimum = (double)volume.Minimum;
        var maximum = (double)volume.Maximum;

        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || maximum - minimum < WindowLevel.MinWidth)
        {
            // Однородный объём: осмысленного окна у него нет, и растягивать
            // шум до полной шкалы было бы вредно.
            return new WindowLevel(minimum, WindowLevel.MinWidth);
        }

        var histogram = new int[BinCount];
        var scale = (BinCount - 1) / (maximum - minimum);

        var dimensions = volume.Grid.Dimensions;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    histogram[(int)((volume[column, row, slice] - minimum) * scale)]++;
                }
            }
        }

        var total = 0L;

        foreach (var count in histogram)
        {
            total += count;
        }

        var lower = Quantile(histogram, total, LowerPercentile, minimum, scale);
        var upper = Quantile(histogram, total, UpperPercentile, minimum, scale);

        var width = Math.Max(upper - lower, WindowLevel.MinWidth);

        return new WindowLevel((lower + upper) / 2, width);
    }

    /// <summary>
    /// Проверяет, что окно вообще что-то показывает на этом объёме.
    /// </summary>
    /// <param name="window">Проверяемое окно.</param>
    /// <param name="volume">Объём.</param>
    /// <returns><see langword="true"/>, если окно пересекается с диапазоном значений.</returns>
    public static bool IsPlausible(WindowLevel window, IVoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (!window.IsUsable)
        {
            return false;
        }

        var half = window.Width / 2;

        return window.Center + half > volume.Minimum && window.Center - half < volume.Maximum;
    }

    private static double Quantile(int[] histogram, long total, double fraction, double minimum, double scale)
    {
        var target = (long)(total * fraction);
        var seen = 0L;

        for (var bin = 0; bin < histogram.Length; bin++)
        {
            seen += histogram[bin];

            if (seen >= target)
            {
                return minimum + (bin / scale);
            }
        }

        return minimum + ((histogram.Length - 1) / scale);
    }
}
