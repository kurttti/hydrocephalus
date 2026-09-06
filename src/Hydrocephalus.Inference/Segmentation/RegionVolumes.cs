using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Объёмы структур по маске сегментации.
///
/// Детерминированная половина работы: где проходят границы, решает сегментация,
/// а как из числа отсчётов получается объём — здесь.
///
/// Объём отсчёта считается по шагу сетки, а не по толщине среза. На серии
/// с зазором эти величины расходятся, и объём по толщине занижен ровно на долю
/// неполученной ткани. Такие серии до сюда не доходят — они базового уровня,
/// а объёмные признаки требуют расширенного, — но правило записано здесь, чтобы
/// оно не зависело от того, что кто-то раньше отфильтровал вход правильно.
/// </summary>
public static class RegionVolumes
{
    /// <summary>Кубических миллиметров в миллилитре.</summary>
    public const double CubicMillimetresPerMillilitre = 1000.0;

    /// <summary>Версия определения объёмного признака.</summary>
    public const string DefinitionVersion = "1.0.0";

    /// <summary>
    /// Правдоподобный диапазон объёма структуры головного мозга, мл.
    /// Верхняя граница выше внутричерепного объёма взрослого: диапазон ловит
    /// ошибку сегментации, а не отклонение от нормы.
    /// </summary>
    private static readonly MeasurementRange PlausibleRange = new(0.0, 2500.0);

    /// <summary>
    /// Считает объёмы всех структур маски.
    /// </summary>
    /// <param name="mask">Маска сегментации.</param>
    /// <param name="availableTier">Уровень входа исследования.</param>
    /// <param name="quality">Оценка достоверности измерения.</param>
    /// <returns>Признаки по структурам в порядке карты меток.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если сетка маски непригодна для измерения объёма.
    /// </exception>
    public static IReadOnlyList<Biomarker> Measure(
        VoxelMask mask,
        AcquisitionTier availableTier,
        MeasurementQuality quality = MeasurementQuality.Reliable)
    {
        ArgumentNullException.ThrowIfNull(mask);

        var voxelVolume = VoxelVolumeCubicMillimetres(mask.Grid);
        var counts = Count(mask);

        var biomarkers = new List<Biomarker>(mask.Map.Labels.Count);

        for (var label = 1; label <= mask.Map.Labels.Count; label++)
        {
            var structure = mask.Map.Labels[label - 1];

            biomarkers.Add(Biomarker.Create(
                MethodFor(structure),
                availableTier,
                counts[label] * voxelVolume / CubicMillimetresPerMillilitre,
                MeasurementUnit.Millilitre,
                quality,
                PlausibleRange,
                [structure]));
        }

        return biomarkers;
    }

    /// <summary>
    /// Считает долю объёма одной структуры от другой.
    ///
    /// Отношение считается по объёмам, а не по числу отсчётов: совпадают они
    /// только на одной и той же сетке, и правило, работающее лишь иногда, хуже
    /// правила, работающего всегда.
    /// </summary>
    /// <param name="numerator">Признак в числителе.</param>
    /// <param name="denominator">Признак в знаменателе.</param>
    /// <param name="code">Код получаемого отношения.</param>
    /// <param name="availableTier">Уровень входа исследования.</param>
    /// <param name="quality">Оценка достоверности измерения.</param>
    /// <returns>Признак-отношение.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если знаменатель не объём либо равен нулю.
    /// </exception>
    public static Biomarker Fraction(
        Biomarker numerator,
        Biomarker denominator,
        string code,
        AcquisitionTier availableTier,
        MeasurementQuality quality = MeasurementQuality.Reliable)
    {
        ArgumentNullException.ThrowIfNull(numerator);
        ArgumentNullException.ThrowIfNull(denominator);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        if (numerator.Unit != MeasurementUnit.Millilitre || denominator.Unit != MeasurementUnit.Millilitre)
        {
            // Отношение объёмов имеет смысл только между объёмами: делить
            // миллилитры на градусы можно арифметически и нельзя клинически.
            throw new DomainRuleViolationException(
                $"Biomarker '{code}' is a ratio of two volumes.");
        }

        if (denominator.Value <= 0)
        {
            throw new DomainRuleViolationException(
                $"Biomarker '{code}' needs a non-zero denominator.");
        }

        return Biomarker.Create(
            new MeasurementMethod
            {
                Code = code,
                DefinitionVersion = DefinitionVersion,
                RequiredTier = AcquisitionTier.Extended,
            },
            availableTier,
            numerator.Value / denominator.Value,
            MeasurementUnit.Ratio,
            quality,
            new MeasurementRange(0.0, 1.0),
            [.. numerator.SourceLabels, .. denominator.SourceLabels]);
    }

    /// <summary>
    /// Считает объём одного отсчёта сетки.
    /// </summary>
    /// <param name="grid">Сетка отсчётов.</param>
    /// <returns>Объём отсчёта в кубических миллиметрах.</returns>
    /// <exception cref="DomainRuleViolationException">Если шаг сетки неположителен.</exception>
    public static double VoxelVolumeCubicMillimetres(VolumeGrid grid)
    {
        if (grid.ColumnSpacingMillimetres <= 0
            || grid.RowSpacingMillimetres <= 0
            || grid.SliceSpacingMillimetres <= 0)
        {
            throw new DomainRuleViolationException(
                "A volume cannot be measured on a grid with a non-positive spacing.");
        }

        return grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres * grid.SliceSpacingMillimetres;
    }

    private static MeasurementMethod MethodFor(AnatomicalLabel structure) => new()
    {
        Code = "volume." + structure.Code,
        DefinitionVersion = DefinitionVersion,

        // Объёмный признак требует объёмного получения: на наборе отдельных
        // срезов с зазором объём структуры недостоверен (docs/clinical/README.md).
        RequiredTier = AcquisitionTier.Extended,
    };

    private static long[] Count(VoxelMask mask)
    {
        var counts = new long[byte.MaxValue + 1];
        var dimensions = mask.Grid.Dimensions;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    counts[mask[column, row, slice]]++;
                }
            }
        }

        // Метка вне карты означает, что маска и карта из разных версий:
        // проверяется до того, как числа попадут в признаки.
        for (var label = mask.Map.Count; label <= byte.MaxValue; label++)
        {
            if (counts[label] > 0)
            {
                _ = mask.Map.StructureOf((byte)label);
            }
        }

        return counts;
    }
}
