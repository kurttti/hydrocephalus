using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Что положения срезов говорят о серии.
/// </summary>
/// <param name="SpacingMillimetres">Шаг между центрами соседних срезов.</param>
/// <param name="MaxDeviationMillimetres">
/// Наибольшее отклонение отдельного промежутка от среднего шага.
/// </param>
/// <param name="HasDuplicatePositions">Признак совпадающих положений срезов.</param>
internal readonly record struct SlicePositioning(
    double SpacingMillimetres,
    double MaxDeviationMillimetres,
    bool HasDuplicatePositions);

/// <summary>
/// Разбор положений срезов серии.
///
/// Шаг между срезами вычисляется по ImagePositionPatient, а не берётся из
/// SliceThickness: это разные величины. Рутинные 2D-серии часто идут с зазором,
/// и объём, посчитанный по толщине, окажется заниженным ровно на долю пропущенной
/// ткани. Обратный случай — перекрывающиеся срезы — завышает объём.
///
/// Положения проецируются на нормаль среза, полученную из направляющих косинусов.
/// Разность координат Z вместо проекции дала бы неверный шаг на наклонных
/// и корональных сериях.
/// </summary>
internal static class SlicePositions
{
    /// <summary>Расстояние, ниже которого два среза считаются совпадающими, мм.</summary>
    private const double DuplicateToleranceMillimetres = 1e-4;

    /// <summary>
    /// Разбирает положения срезов.
    /// </summary>
    /// <param name="positions">Положения срезов в произвольном порядке.</param>
    /// <param name="rowDirection">Направляющий косинус строки.</param>
    /// <param name="columnDirection">Направляющий косинус столбца.</param>
    /// <param name="fallbackSpacingMillimetres">
    /// Шаг, используемый, когда вычислить его нельзя: серия из одного среза либо
    /// отсутствующие направляющие косинусы.
    /// </param>
    /// <returns>Шаг, равномерность и признак совпадающих положений.</returns>
    internal static SlicePositioning Analyse(
        IReadOnlyList<SpatialVector> positions,
        SpatialVector rowDirection,
        SpatialVector columnDirection,
        double fallbackSpacingMillimetres)
    {
        var normal = rowDirection.Cross(columnDirection).Normalized();

        if (positions.Count < 2 || normal.Length == 0)
        {
            return new SlicePositioning(fallbackSpacingMillimetres, 0, HasDuplicatePositions: false);
        }

        var offsets = positions
            .Select(position => position.Dot(normal))
            .Order()
            .ToArray();

        var gaps = new double[offsets.Length - 1];

        for (var index = 0; index < gaps.Length; index++)
        {
            gaps[index] = offsets[index + 1] - offsets[index];
        }

        var duplicates = gaps.Any(gap => gap < DuplicateToleranceMillimetres);

        // Медиана, а не среднее: один дублирующийся или потерянный срез не должен
        // сдвигать шаг, по которому потом считается объём.
        var spacing = Median(gaps);

        if (spacing <= 0)
        {
            return new SlicePositioning(fallbackSpacingMillimetres, 0, duplicates);
        }

        var deviation = gaps.Max(gap => Math.Abs(gap - spacing));

        return new SlicePositioning(spacing, deviation, duplicates);
    }

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
