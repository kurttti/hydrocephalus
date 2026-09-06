using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Пороги по интенсивности, вычисляемые из самого объёма.
///
/// Абсолютных значений здесь нет и быть не может: интенсивность МРТ не
/// калибрована, и один и тот же порог на двух аппаратах означает разные ткани.
/// Всё считается от распределения значений конкретного объёма.
/// </summary>
public static class IntensityThresholds
{
    /// <summary>Число корзин гистограммы.</summary>
    public const int BinCount = 256;

    /// <summary>
    /// Считает порог методом Оцу: значение, максимизирующее межклассовую
    /// дисперсию.
    ///
    /// Метод предполагает ровно два класса. Для отделения головы от фона это
    /// так. Внутри головы классов больше — ликвор, серое и белое вещество, —
    /// и порог встанет между наиболее разделимой парой, которой не обязательно
    /// окажется «ликвор против ткани». Поэтому вызывающий обязан проверить
    /// правдоподобие полученной доли, а не доверять порогу на слово.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="include">
    /// Отбор отсчётов; null — все. Порог внутри головы считается только по ней:
    /// фон занимает большую часть кадра и утянул бы границу к себе.
    /// </param>
    /// <returns>Порог в единицах модальности.</returns>
    /// <exception cref="DomainRuleViolationException">Если разделять нечего.</exception>
    public static double Otsu(IVoxelVolume volume, Func<float, bool>? include = null) =>
        Compute(volume, include, selection: null);

    /// <summary>
    /// Считает порог Оцу только по отобранным отсчётам.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="selection">Маска отсчётов, участвующих в расчёте.</param>
    /// <returns>Порог в единицах модальности.</returns>
    /// <exception cref="DomainRuleViolationException">Если разделять нечего.</exception>
    public static double OtsuWithin(IVoxelVolume volume, bool[] selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        return Compute(volume, include: null, selection);
    }

    private static double Compute(IVoxelVolume volume, Func<float, bool>? include, bool[]? selection)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var minimum = (double)volume.Minimum;
        var maximum = (double)volume.Maximum;

        if (maximum - minimum <= 0)
        {
            // На однородном объёме двух классов нет, и любой порог был бы
            // выдумкой: разделить нечего.
            throw new DomainRuleViolationException(
                "An intensity threshold cannot be derived from a uniform volume.");
        }

        var histogram = Histogram(volume, minimum, maximum, include, selection, out var total);

        if (total == 0)
        {
            throw new DomainRuleViolationException(
                "An intensity threshold needs at least one selected voxel.");
        }

        var sum = 0.0;

        for (var bin = 0; bin < BinCount; bin++)
        {
            sum += bin * (double)histogram[bin];
        }

        var backgroundSum = 0.0;
        long backgroundCount = 0;
        var bestVariance = -1.0;
        var bestBin = 0;

        for (var bin = 0; bin < BinCount; bin++)
        {
            backgroundCount += histogram[bin];

            if (backgroundCount == 0)
            {
                continue;
            }

            var foregroundCount = total - backgroundCount;

            if (foregroundCount == 0)
            {
                break;
            }

            backgroundSum += bin * (double)histogram[bin];

            var backgroundMean = backgroundSum / backgroundCount;
            var foregroundMean = (sum - backgroundSum) / foregroundCount;
            var difference = backgroundMean - foregroundMean;

            var variance = (double)backgroundCount * foregroundCount * difference * difference;

            if (variance > bestVariance)
            {
                bestVariance = variance;
                bestBin = bin;
            }
        }

        // Верхняя граница корзины, а не её середина. Порог Оцу разделяет корзины,
        // а не значения: значение из выбранной корзины лежит и выше её середины,
        // и сравнение с серединой отбросило бы ровно тот класс, который искали.
        // Отсюда и правило сравнения: нижний класс — «не больше порога»,
        // верхний — «строго больше».
        return minimum + ((bestBin + 1.0) * (maximum - minimum) / BinCount);
    }

    private static long[] Histogram(
        IVoxelVolume volume,
        double minimum,
        double maximum,
        Func<float, bool>? include,
        bool[]? selection,
        out long total)
    {
        var histogram = new long[BinCount];
        var range = maximum - minimum;
        var dimensions = volume.Grid.Dimensions;

        total = 0;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (selection is not null
                        && !selection[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column])
                    {
                        continue;
                    }

                    var value = volume[column, row, slice];

                    if (include is not null && !include(value))
                    {
                        continue;
                    }

                    histogram[BinOf(value, minimum, range)]++;
                    total++;
                }
            }
        }

        return histogram;
    }

    private static int BinOf(double value, double minimum, double range) =>
        Math.Clamp((int)((value - minimum) * (BinCount - 1) / range), 0, BinCount - 1);
}
