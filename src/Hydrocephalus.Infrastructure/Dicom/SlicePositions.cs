using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Один экземпляр серии в том виде, в каком он важен для взаимного расположения срезов.
///
/// Кроме положения хранятся ключи осей, по которым серия может распадаться на
/// несколько наборов срезов: плоскость, эхо и номер получения. Сами значения тегов
/// нигде не выводятся — по ключам считается только число различных значений.
/// </summary>
/// <param name="Position">Положение первого воксела в системе координат пациента.</param>
/// <param name="HasPosition">Признак того, что ImagePositionPatient присутствует.</param>
/// <param name="OrientationKey">Ключ плоскости среза.</param>
/// <param name="EchoKey">Ключ эха.</param>
/// <param name="AcquisitionKey">Ключ получения (номер получения и временная позиция).</param>
/// <param name="IsMultiFrame">Признак многокадрового экземпляра.</param>
internal readonly record struct SliceSample(
    SpatialVector Position,
    bool HasPosition,
    string OrientationKey,
    string EchoKey,
    string AcquisitionKey,
    bool IsMultiFrame);

/// <summary>
/// Что положения срезов говорят о серии.
/// </summary>
/// <param name="SpacingMillimetres">Шаг между центрами соседних срезов.</param>
/// <param name="MaxDeviationMillimetres">
/// Наибольшее отклонение отдельного промежутка от среднего шага.
/// </param>
/// <param name="HasDuplicatePositions">Признак совпадающих положений срезов.</param>
/// <param name="Instances">Число принятых экземпляров серии.</param>
/// <param name="PositionedInstances">Из них тех, у которых есть положение.</param>
/// <param name="DistinctOffsets">Число различных положений вдоль нормали.</param>
/// <param name="DistinctOrientations">Число различных плоскостей среза.</param>
/// <param name="DistinctEchoes">Число различных эхо.</param>
/// <param name="DistinctAcquisitions">Число различных получений.</param>
/// <param name="MultiFrameInstances">Число многокадровых экземпляров.</param>
internal readonly record struct SlicePositioning(
    double SpacingMillimetres,
    double MaxDeviationMillimetres,
    bool HasDuplicatePositions,
    int Instances,
    int PositionedInstances,
    int DistinctOffsets,
    int DistinctOrientations,
    int DistinctEchoes,
    int DistinctAcquisitions,
    int MultiFrameInstances);

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
///
/// Проекция ведётся на одну нормаль — нормаль первого среза. Для серии, в которой
/// плоскости срезов различаются (обзорная серия из трёх проекций под одним
/// SeriesInstanceUID — обычное дело), это неверно: срезы разных плоскостей
/// сходятся в одну проекцию и читаются как совпадающие. Поэтому число различных
/// плоскостей считается отдельно, а решение по нему принимает
/// <see cref="SliceGeometryChecks"/>.
/// </summary>
internal static class SlicePositions
{
    /// <summary>Расстояние, ниже которого два среза считаются совпадающими, мм.</summary>
    private const double DuplicateToleranceMillimetres = 1e-4;

    /// <summary>
    /// Разбирает положения срезов.
    /// </summary>
    /// <param name="samples">Экземпляры серии в произвольном порядке.</param>
    /// <param name="rowDirection">Направляющий косинус строки.</param>
    /// <param name="columnDirection">Направляющий косинус столбца.</param>
    /// <param name="fallbackSpacingMillimetres">
    /// Шаг, используемый, когда вычислить его нельзя: серия из одного среза либо
    /// отсутствующие направляющие косинусы.
    /// </param>
    /// <returns>Шаг, равномерность и состав серии.</returns>
    internal static SlicePositioning Analyse(
        IReadOnlyList<SliceSample> samples,
        SpatialVector rowDirection,
        SpatialVector columnDirection,
        double fallbackSpacingMillimetres)
    {
        var positioned = samples.Where(sample => sample.HasPosition).ToArray();

        var composition = new SlicePositioning(
            SpacingMillimetres: fallbackSpacingMillimetres,
            MaxDeviationMillimetres: 0,
            HasDuplicatePositions: false,
            Instances: samples.Count,
            PositionedInstances: positioned.Length,
            DistinctOffsets: 0,
            DistinctOrientations: Distinct(samples, sample => sample.OrientationKey),
            DistinctEchoes: Distinct(samples, sample => sample.EchoKey),
            DistinctAcquisitions: Distinct(samples, sample => sample.AcquisitionKey),
            MultiFrameInstances: samples.Count(sample => sample.IsMultiFrame));

        var normal = rowDirection.Cross(columnDirection).Normalized();

        if (positioned.Length < 2 || normal.Length == 0)
        {
            return composition;
        }

        var offsets = positioned
            .Select(sample => sample.Position.Dot(normal))
            .Order()
            .ToArray();

        var gaps = new double[offsets.Length - 1];

        for (var index = 0; index < gaps.Length; index++)
        {
            gaps[index] = offsets[index + 1] - offsets[index];
        }

        var duplicates = gaps.Count(gap => gap < DuplicateToleranceMillimetres);

        composition = composition with
        {
            HasDuplicatePositions = duplicates > 0,
            DistinctOffsets = offsets.Length - duplicates,
        };

        // Медиана, а не среднее: один дублирующийся или потерянный срез не должен
        // сдвигать шаг, по которому потом считается объём.
        var spacing = Median(gaps);

        if (spacing <= 0)
        {
            return composition;
        }

        return composition with
        {
            SpacingMillimetres = spacing,
            MaxDeviationMillimetres = gaps.Max(gap => Math.Abs(gap - spacing)),
        };
    }

    private static int Distinct(IReadOnlyList<SliceSample> samples, Func<SliceSample, string> key) =>
        samples.Select(key).Distinct(StringComparer.Ordinal).Count();

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;

        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
