using System.Globalization;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Строка состояния о сегментации открытой серии.
///
/// Отказ сегментации называется своей причиной, а не общим «маска не
/// построена». Причины различаются по смыслу: порог, взявший ткань, фрагмент
/// в пару миллилитров и область у края кадра — три разные поломки, и врач,
/// открывший разбор решения, должен знать, какую из них он смотрит.
/// </summary>
public static class SegmentationReadout
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private const string ReviewHint =
        " Что метод нашёл — «Разбор метода»: оранжевым выбранное, зелёным отброшенный ликвор.";

    /// <summary>
    /// Описывает результат сегментации.
    /// </summary>
    /// <param name="result">Результат либо <see langword="null"/>, если сегментация не запускалась.</param>
    /// <param name="tier">Уровень получения показанной серии.</param>
    /// <param name="evans">Индекс Эванса, выведенный из маски; <see langword="null"/>, если не выводился.</param>
    /// <returns>Строка состояния.</returns>
    public static string Describe(
        BaselineSegmentationResult? result,
        AcquisitionTier tier = AcquisitionTier.Extended,
        AutomaticEvansResult? evans = null)
    {
        if (tier != AcquisitionTier.Extended)
        {
            // По толстым срезам объём не считается вовсе, и причина отказа
            // сегментации здесь только сбила бы с толку: «упирается в край кадра»
            // читается как дефект серии, а дело в том, что это не объём.
            return "Открыто. Серия базового уровня (толстые срезы): объём желудочков по ней не считается."
                + (result is null ? string.Empty : " Маска и «Разбор метода» — только для ориентировки.");
        }

        if (result is not { } segmentation)
        {
            return "Открыто. Маска не построена: взвешенность серии не распознана.";
        }

        var refusal = segmentation.Issues.FirstOrDefault(
            issue => issue.Severity == QualityIssueSeverity.Blocking);

        if (refusal is not null)
        {
            return "Открыто. Объём не посчитан: " + ReasonOf(refusal) + ReviewHint;
        }

        if (IsEmpty(segmentation.Mask))
        {
            return "Открыто. Желудочки не найдены: весь найденный ликвор отброшен как периферический."
                + ReviewHint;
        }

        return "Открыто. Маска получена baseline-методом и не является проверенной сегментацией."
            + DescribeEvans(evans);
    }

    private static string DescribeEvans(AutomaticEvansResult? evans) => evans switch
    {
        null => string.Empty,

        // Число без места измерения проверить нельзя: рядом с ним сказано,
        // где увидеть концы отрезков.
        { Biomarker: { } index } =>
            " Индекс Эванса " + index.Value.ToString("0.00", Russian)
            + " (сомнительно): отрезки — в «Разборе метода» на аксиальном срезе,"
            + " синим ширина передних рогов, малиновым внутренний диаметр черепа.",

        { Refusal: AutomaticEvansRefusal.FrontalHornsNotFound } =>
            " Индекс Эванса не посчитан: в маске нет передних рогов обоих желудочков.",
        { Refusal: AutomaticEvansRefusal.InnerSkullNotFound } =>
            " Индекс Эванса не посчитан: на срезе рогов не найдена граница черепа.",
        { Refusal: AutomaticEvansRefusal.AxesNotAligned } =>
            " Индекс Эванса не посчитан: серия слишком наклонена к осям головы.",
        { Refusal: AutomaticEvansRefusal.WeightingNotSupported } =>
            " Индекс Эванса по этой взвешенности автоматически не считается.",
        _ => string.Empty,
    };

    private static string ReasonOf(QualityIssue refusal) =>
        refusal.Parameters.GetValueOrDefault("reason") switch
        {
            "thresholdDidNotIsolateCsf" =>
                $"порог ликвора выделил {Percent(refusal.Parameters.GetValueOrDefault("selectedFraction"))} головы — это ткань, а не ликвор.",
            "ventricularSystemImplausiblySmall" =>
                $"найденная область — {Millilitres(refusal.Parameters.GetValueOrDefault("millilitres"))} мл, это фрагмент, а не желудочковая система.",
            "ventricularSystemTruncatedByFrame" =>
                "найденная область упирается в край кадра — желудочки обрезаны или выбрано не то.",
            _ => "сегментация отказала.",
        };

    private static string Percent(string? fraction) =>
        double.TryParse(fraction, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("P0", Russian)
            : "слишком большую часть";

    private static string Millilitres(string? millilitres) =>
        double.TryParse(millilitres, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value.ToString("0.0", Russian)
            : "меньше порога";

    private static bool IsEmpty(VoxelMask mask)
    {
        var dimensions = mask.Grid.Dimensions;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (mask[column, row, slice] != 0)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}
