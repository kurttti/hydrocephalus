using System.Globalization;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Проверки взаимного расположения срезов серии.
///
/// Зазор между срезами здесь замечанием не считается: рутинные 2D-серии выборки
/// идут с зазором штатно, и отмечать это на каждой серии — шум. Зазор влияет на
/// пригодность через фактический шаг выборки в доменной модели. Замечания даются
/// на то, что делает объём непостроимым: совпадающие положения и неравномерный шаг.
/// </summary>
internal static class SliceGeometryChecks
{
    /// <summary>
    /// Допустимое отклонение отдельного промежутка от медианного шага, доля.
    /// Небольшой разброс объясняется округлением координат в тегах.
    /// </summary>
    internal const double MaxSpacingDeviationFraction = 0.10;

    /// <summary>
    /// Проверяет расположение срезов.
    /// </summary>
    /// <param name="positioning">Результат разбора положений.</param>
    /// <returns>Найденные замечания, возможно пустой список.</returns>
    internal static IEnumerable<QualityIssue> Inspect(SlicePositioning positioning)
    {
        if (positioning.HasDuplicatePositions)
        {
            // Совпадающие положения означают, что под одним SeriesInstanceUID
            // лежит больше одного набора срезов — например, разные эхо.
            // Какой из них строить, метаданные не говорят.
            yield return Issue("duplicateSlicePositions");
        }

        if (positioning.SpacingMillimetres <= 0)
        {
            yield break;
        }

        var tolerance = positioning.SpacingMillimetres * MaxSpacingDeviationFraction;

        if (positioning.MaxDeviationMillimetres > tolerance)
        {
            // Пропущенный срез посреди серии не виден ни по числу файлов, ни по
            // толщине: он проявляется только как промежуток, выпадающий из шага.
            // Объём, посчитанный по такой серии, занижен молча.
            yield return Issue(
                "irregularSliceSpacing",
                ("spacingMm", Format(positioning.SpacingMillimetres)),
                ("maxDeviationMm", Format(positioning.MaxDeviationMillimetres)));
        }
    }

    private static QualityIssue Issue(string reason, params (string Key, string Value)[] parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason };

        foreach (var (key, value) in parameters)
        {
            values[key] = value;
        }

        return new QualityIssue
        {
            Code = QualityIssueCode.InconsistentGeometry,
            Severity = QualityIssueSeverity.Blocking,
            Parameters = values,
        };
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
