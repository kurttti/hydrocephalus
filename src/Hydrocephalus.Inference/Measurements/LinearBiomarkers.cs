using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;

namespace Hydrocephalus.Inference.Measurements;

/// <summary>
/// Линейные признаки, вычисляемые по двум-трём точкам на одном срезе.
///
/// Это детерминированная половина работы: где стоят точки, решает сегментация
/// или врач, а как из них получается число — здесь, и результат при одном
/// и том же входе всегда один и тот же.
///
/// Диагностических порогов здесь нет намеренно. Общеизвестное значение индекса
/// Эванса 0.3 в код не попадает: пороги принадлежат модели и протоколу
/// валидации (docs/validation/README.md), а не измерителю. Диапазоны ниже —
/// проверка правдоподобия измерения, а не признак болезни.
/// </summary>
public static class LinearBiomarkers
{
    /// <summary>Код метода для индекса Эванса.</summary>
    public const string EvansIndexCode = "evans-index";

    /// <summary>Код метода для угла мозолистого тела.</summary>
    public const string CallosalAngleCode = "callosal-angle";

    /// <summary>Версия определения методов.</summary>
    public const string DefinitionVersion = "1.0.0";

    /// <summary>
    /// Правдоподобный диапазон индекса Эванса. Отношение ширины передних рогов
    /// к внутреннему диаметру черепа физически меньше единицы; значения ниже
    /// 0.1 и выше 0.6 означают ошибку постановки точек, а не находку.
    /// </summary>
    private static readonly MeasurementRange EvansPlausibleRange = new(0.10, 0.60);

    /// <summary>
    /// Правдоподобный диапазон угла мозолистого тела в градусах.
    /// </summary>
    private static readonly MeasurementRange CallosalAnglePlausibleRange = new(30.0, 180.0);

    /// <summary>
    /// Вычисляет индекс Эванса: отношение наибольшей ширины передних рогов
    /// боковых желудочков к наибольшему внутреннему диаметру черепа.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="frontalHornFirst">Первая точка ширины передних рогов.</param>
    /// <param name="frontalHornSecond">Вторая точка ширины передних рогов.</param>
    /// <param name="innerSkullFirst">Первая точка внутреннего диаметра черепа.</param>
    /// <param name="innerSkullSecond">Вторая точка внутреннего диаметра черепа.</param>
    /// <param name="quality">Оценка достоверности измерения.</param>
    /// <returns>Признак с отношением.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если точки лежат на разных срезах, срез не аксиальный либо диаметр черепа нулевой.
    /// </exception>
    public static Biomarker EvansIndex(
        IVoxelVolume volume,
        VoxelPosition frontalHornFirst,
        VoxelPosition frontalHornSecond,
        VoxelPosition innerSkullFirst,
        VoxelPosition innerSkullSecond,
        MeasurementQuality quality = MeasurementQuality.Reliable)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Индекс Эванса определён на аксиальном срезе. Измеренный в другой
        // плоскости, он даёт число того же вида и другого смысла, поэтому
        // плоскость проверяется, а не подразумевается.
        RequirePlane(volume, ImagingPlane.Axial, EvansIndexCode);

        RequireSameSlice(
            EvansIndexCode,
            frontalHornFirst,
            frontalHornSecond,
            innerSkullFirst,
            innerSkullSecond);

        var horns = PatientSpace.DistanceMillimetres(volume, frontalHornFirst, frontalHornSecond);
        var skull = PatientSpace.DistanceMillimetres(volume, innerSkullFirst, innerSkullSecond);

        if (skull <= 0)
        {
            throw new DomainRuleViolationException(
                $"Biomarker '{EvansIndexCode}' needs a non-zero inner skull diameter.");
        }

        return Biomarker.Create(
            new MeasurementMethod
            {
                Code = EvansIndexCode,
                DefinitionVersion = DefinitionVersion,

                // Линейное измерение выполняется на одном срезе и объёмного
                // получения не требует (docs/clinical/README.md).
                RequiredTier = AcquisitionTier.Baseline,
            },
            volume.Geometry.Tier,
            horns / skull,
            MeasurementUnit.Ratio,
            quality,
            EvansPlausibleRange);
    }

    /// <summary>
    /// Вычисляет угол мозолистого тела между линиями, проведёнными из вершины
    /// к точкам на боковых желудочках.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="vertex">Вершина угла.</param>
    /// <param name="first">Точка на первом луче.</param>
    /// <param name="second">Точка на втором луче.</param>
    /// <param name="quality">Оценка достоверности измерения.</param>
    /// <returns>Признак с углом в градусах.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если точки лежат на разных срезах либо срез не корональный.
    /// </exception>
    public static Biomarker CallosalAngle(
        IVoxelVolume volume,
        VoxelPosition vertex,
        VoxelPosition first,
        VoxelPosition second,
        MeasurementQuality quality = MeasurementQuality.Reliable)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Угол измеряется на корональном срезе. В аксиальной плоскости те же
        // три точки дают другой угол, и различить эти два числа в отчёте
        // по значению невозможно.
        RequirePlane(volume, ImagingPlane.Coronal, CallosalAngleCode);

        RequireSameSlice(CallosalAngleCode, vertex, first, second);

        return Biomarker.Create(
            new MeasurementMethod
            {
                Code = CallosalAngleCode,
                DefinitionVersion = DefinitionVersion,
                RequiredTier = AcquisitionTier.Baseline,
            },
            volume.Geometry.Tier,
            PatientSpace.AngleDegrees(volume, vertex, first, second),
            MeasurementUnit.Degree,
            quality,
            CallosalAnglePlausibleRange);
    }

    private static void RequirePlane(IVoxelVolume volume, ImagingPlane expected, string code)
    {
        var actual = volume.Geometry.Plane;

        if (actual != expected)
        {
            throw new DomainRuleViolationException(
                $"Biomarker '{code}' is defined on the {expected} plane, but the volume is {actual}.");
        }
    }

    private static void RequireSameSlice(string code, params VoxelPosition[] positions)
    {
        var slice = positions[0].Slice;

        foreach (var position in positions)
        {
            if (Math.Abs(position.Slice - slice) > double.Epsilon)
            {
                // Точки с разных срезов дают длину, включающую расстояние между
                // ними по оси среза: измерение перестаёт быть тем, что называется.
                throw new DomainRuleViolationException(
                    $"Biomarker '{code}' must be measured on a single slice.");
            }
        }
    }
}
