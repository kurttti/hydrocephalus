using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;

namespace Hydrocephalus.Inference.Measurements;

/// <summary>
/// Почему разметка не дала индекса.
/// </summary>
public enum ManualEvansRefusal
{
    /// <summary>Отмечены не все четыре точки.</summary>
    PointsIncomplete = 1,

    /// <summary>Точки лежат на разных плоскостях.</summary>
    PointsOnDifferentPlanes = 2,

    /// <summary>Концы диаметра черепа совпали: отмерять нечего.</summary>
    SkullDiameterIsZero = 3,

    /// <summary>Плоскость разметки не аксиальная, а индекс Эванса определён только на ней.</summary>
    PlaneIsNotAxial = 4,
}

/// <summary>
/// Что вышло из разметки: признак с отрезками либо названная причина отказа.
/// </summary>
public sealed record ManualEvansResult
{
    /// <summary>Признак; <see langword="null"/> при отказе.</summary>
    public Biomarker? Biomarker { get; init; }

    /// <summary>Отрезки измерения; <see langword="null"/> при отказе.</summary>
    public EvansSegments? Segments { get; init; }

    /// <summary>Причина отказа; <see langword="null"/>, если индекс посчитан.</summary>
    public ManualEvansRefusal? Refusal { get; init; }
}

/// <summary>
/// Что произошло с очередной отмеченной точкой.
/// </summary>
public enum MarkingOutcome
{
    /// <summary>Точка принята.</summary>
    Added = 1,

    /// <summary>
    /// Точка лежит на другой плоскости, и разметка начата заново с неё.
    ///
    /// Врач листает срезы в поисках нужного уровня, и точки, поставленные до
    /// перелистывания, к новому срезу отношения не имеют. Отбросить их сразу и
    /// сказать об этом лучше, чем принять и отказать после четвёртого нажатия:
    /// причина отказа тогда называется на три нажатия позже, чем стала известна.
    /// </summary>
    RestartedOnAnotherPlane = 2,

    /// <summary>Все четыре точки уже отмечены; точка не принята.</summary>
    AlreadyComplete = 3,
}

/// <summary>
/// Индекс Эванса, отмеченный врачом на одном аксиальном срезе.
///
/// Автоматический индекс выводится из маски и потому требует объёмной серии.
/// Таких в выборке мало: в контрольной группе ни одной, прошедшей проверку маски
/// (docs/data/README.md). Сам же индекс определён на одном аксиальном срезе и к
/// толщине среза безразличен, поэтому на рутинных толстосрезовых сериях он
/// измерим — но только руками.
///
/// Считает не этот класс: он собирает четыре точки и отдаёт их
/// <see cref="LinearBiomarkers.EvansIndex(IVoxelVolume, VolumeAxis, VoxelPosition, VoxelPosition, VoxelPosition, VoxelPosition, MeasurementQuality)"/>.
/// Отношение, миллиметры системы координат пациента и диапазон правдоподобия
/// там уже есть, и второй путь вычисления рано или поздно разошёлся бы с первым.
/// </summary>
public sealed class ManualEvansMarking
{
    /// <summary>Сколько точек нужно: два конца рогов и два конца диаметра черепа.</summary>
    public const int RequiredPoints = 4;

    private readonly List<VoxelPosition> points = [];

    /// <summary>
    /// Создаёт разметку на заданной оси.
    /// </summary>
    /// <param name="axialAcross">Ось объёма, поперёк которой лежит аксиальная плоскость.</param>
    public ManualEvansMarking(VolumeAxis axialAcross) => this.AxialAcross = axialAcross;

    /// <summary>Ось, поперёк которой лежит плоскость разметки.</summary>
    public VolumeAxis AxialAcross { get; }

    /// <summary>Отмеченные точки в порядке постановки.</summary>
    public IReadOnlyList<VoxelPosition> Points => this.points;

    /// <summary>Номер плоскости разметки; <see langword="null"/>, если точек нет.</summary>
    public int? PlaneIndex => this.points.Count == 0 ? null : PlaneOf(this.points[0]);

    /// <summary>Отмечены ли все четыре точки.</summary>
    public bool IsComplete => this.points.Count == RequiredPoints;

    /// <summary>
    /// Отмечает точку.
    /// </summary>
    /// <param name="point">Положение вокселя.</param>
    /// <returns>Что произошло с точкой.</returns>
    public MarkingOutcome Add(VoxelPosition point)
    {
        if (this.IsComplete)
        {
            return MarkingOutcome.AlreadyComplete;
        }

        if (this.points.Count > 0 && PlaneOf(point) != this.PlaneIndex)
        {
            this.points.Clear();
            this.points.Add(point);

            return MarkingOutcome.RestartedOnAnotherPlane;
        }

        this.points.Add(point);

        return MarkingOutcome.Added;
    }

    /// <summary>Снимает последнюю отмеченную точку.</summary>
    /// <returns><see langword="true"/>, если точка была снята.</returns>
    public bool UndoLast()
    {
        if (this.points.Count == 0)
        {
            return false;
        }

        this.points.RemoveAt(this.points.Count - 1);

        return true;
    }

    /// <summary>Снимает все точки.</summary>
    public void Reset() => this.points.Clear();

    /// <summary>
    /// Считает индекс по отмеченным точкам.
    ///
    /// Флаг качества — <see cref="MeasurementQuality.Reliable"/>, и это
    /// сознательное отличие от автоматического пути. Тот помечает измерение
    /// сомнительным по названной причине: «метод не валидирован против ручной
    /// разметки». Ручная разметка и есть та разметка, поэтому причина на неё не
    /// переносится — иначе в системе не осталось бы измерения, которым можно
    /// проверить хоть что-нибудь. Неправдоподобное значение при этом не
    /// скрывается: диапазон проверяется, как и у автоматического, и читалка
    /// результата объявляет вышедшее за него непригодным.
    ///
    /// Воспроизводимость между наблюдателями это не оценивает: она свойство
    /// набора повторных измерений, а не одного (M3, docs/roadmap.md).
    /// </summary>
    /// <param name="volume">Объём, на котором шла разметка.</param>
    /// <returns>Признак с отрезками либо причина отказа.</returns>
    public ManualEvansResult Measure(IVoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (!this.IsComplete)
        {
            return Refused(ManualEvansRefusal.PointsIncomplete);
        }

        var planes = this.points.Select(PlaneOf).Distinct().Count();

        if (planes > 1)
        {
            // До этого дойти не должно: Add начинает разметку заново, встретив
            // другую плоскость. Проверка остаётся, потому что порядок точек
            // задаёт вызывающая сторона, а косое измерение выглядит как обычное.
            return Refused(ManualEvansRefusal.PointsOnDifferentPlanes);
        }

        var segments = new EvansSegments(
            this.AxialAcross,
            this.PlaneIndex!.Value,
            this.points[0],
            this.points[1],
            this.points[2],
            this.points[3]);

        try
        {
            var measured = LinearBiomarkers.EvansIndex(
                volume,
                segments.AxialAcross,
                segments.FrontalHornFirst,
                segments.FrontalHornSecond,
                segments.InnerSkullFirst,
                segments.InnerSkullSecond,
                MeasurementQuality.Reliable);

            return new ManualEvansResult
            {
                // Вычисление одно, метод получения другой. Код переписывается
                // здесь, а не вторым путём вычисления: иначе отношение считалось
                // бы в двух местах и однажды разошлось бы само с собой.
                Biomarker = measured with
                {
                    Method = measured.Method with { Code = LinearBiomarkers.ManualEvansIndexCode },
                },
                Segments = segments,
            };
        }
        catch (DomainRuleViolationException)
        {
            // Домен отказывает по двум оставшимся причинам: ось не ведёт
            // вверх-вниз либо концы диаметра черепа совпали. Первая — свойство
            // серии, вторая — промах постановки; различаются они по самим точкам.
            return Refused(
                this.points[2] == this.points[3]
                    ? ManualEvansRefusal.SkullDiameterIsZero
                    : ManualEvansRefusal.PlaneIsNotAxial);
        }
    }

    private static ManualEvansResult Refused(ManualEvansRefusal refusal) =>
        new() { Refusal = refusal };

    private int PlaneOf(VoxelPosition point) => PlaneAddressing.IndexOf(
        this.AxialAcross,
        (int)point.Column,
        (int)point.Row,
        (int)point.Slice);
}
