using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Распознавание характера серии по описанию, введённому оператором сканера.
///
/// Это эвристика над свободным текстом производителя, а не надёжный признак.
/// У неё заведомо есть ложноотрицательные срабатывания: описание может быть пустым,
/// на другом языке или содержать нестандартное сокращение. Поэтому правило собрано
/// в одном месте — чтобы его можно было проверить, расширить и обсудить, а не искать
/// разбросанные по коду проверки подстрок.
/// </summary>
internal static class SeriesClassification
{
    private static readonly string[] ContrastMarkers =
    [
        "+c",
        "+ c",
        "post c",
        "postc",
        "post-c",
        "ce t1",
        "t1 ce",
        "c+",
        "gd",
        "gado",
        "contrast",
        "контраст",
        "с контрастом",
    ];

    private static readonly string[] NonContrastMarkers =
    [
        "non contrast",
        "non-contrast",
        "pre contrast",
        "pre-contrast",
        "без контраста",
    ];

    /// <summary>
    /// Определяет, является ли серия постконтрастной по её описанию.
    /// Такие серии не подаются в MRI-only конвейер ни на одном уровне входа.
    /// </summary>
    /// <param name="seriesDescription">Описание серии, возможно пустое.</param>
    /// <returns><see langword="true"/>, если описание указывает на введение контраста.</returns>
    internal static bool LooksContrastEnhanced(string? seriesDescription)
    {
        if (string.IsNullOrWhiteSpace(seriesDescription))
        {
            return false;
        }

        var text = seriesDescription.ToLowerInvariant();

        // Явное указание на отсутствие контраста имеет приоритет: описания вида
        // "T1 non-contrast" содержат подстроки из обоих списков.
        if (NonContrastMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        return ContrastMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Определяет взвешенность серии по описанию.
    /// Та же оговорка: эвристика над свободным текстом.
    /// </summary>
    /// <param name="seriesDescription">Описание серии, возможно пустое.</param>
    /// <returns>Распознанная взвешенность либо <see cref="SeriesWeighting.Unknown"/>.</returns>
    internal static SeriesWeighting DetectWeighting(string? seriesDescription)
    {
        if (string.IsNullOrWhiteSpace(seriesDescription))
        {
            return SeriesWeighting.Unknown;
        }

        var text = seriesDescription.ToLowerInvariant();

        // FLAIR проверяется первым: описания вида "T2 FLAIR" содержат и "t2".
        if (text.Contains("flair", StringComparison.Ordinal))
        {
            return SeriesWeighting.Flair;
        }

        if (text.Contains("t1", StringComparison.Ordinal))
        {
            return SeriesWeighting.T1;
        }

        return text.Contains("t2", StringComparison.Ordinal)
            ? SeriesWeighting.T2
            : SeriesWeighting.Unknown;
    }
}
