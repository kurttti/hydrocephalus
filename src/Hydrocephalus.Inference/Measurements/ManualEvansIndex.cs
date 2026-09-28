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

    /// <summary>
    /// Отклонение оси измерения от горизонтали кадра в градусах — измеренный
    /// поворот головы в аппарате. <see langword="null"/> при отказе.
    /// </summary>
    public double? RotationDegrees { get; init; }
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
///
/// **Порядок точек: сначала череп, потом рога, и это не косметика.**
/// Обе величины определения поперечные, то есть отмеряются вдоль одной оси.
/// Оси сетки для этого не годятся: голова в аппарате лежит не строго, и при
/// повороте на угол горизонтальная хорда черепа длиннее поперечника, а
/// латеральные углы рогов перестают попадать на одну строку. Череп завышается,
/// рога занижаются, и ошибки складываются — при 15 градусах индекс уходит вниз
/// почти на 5%, то есть 0,30 читается как 0,286.
///
/// Поэтому ось задаёт врач: пара точек черепа определяет направление, а отрезок
/// рогов **проецируется** на него. Положение головы в аппарате после этого не
/// значит ничего — выравнивание идёт по анатомии, которую видно.
///
/// Череп первым потому, что он длиннее: та же погрешность в один пиксел даёт
/// на базе 145мм наклон оси около 0,2 градуса, а на базе 40мм — около 0,6.
/// </summary>
public sealed class ManualEvansMarking
{
    /// <summary>Сколько точек нужно: два конца диаметра черепа и два конца рогов.</summary>
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

        var skullStart = this.points[0];
        var skullEnd = this.points[1];
        var hornStart = this.points[2];

        var axis = Displacement(volume, skullStart, skullEnd);

        if (axis.Length <= 0)
        {
            return Refused(ManualEvansRefusal.SkullDiameterIsZero);
        }

        // Отрезок рогов кладётся на ось черепа: в определении обе величины
        // поперечные, то есть отмеряются вдоль одного направления. Кратность
        // считается в системе пациента, а прикладывается к смещению в сетке —
        // поэтому обратное преобразование не нужно, а точка остаётся на той же
        // плоскости, на которой была отмечена.
        var along = Displacement(volume, hornStart, this.points[3]).Dot(axis)
            / axis.Dot(axis);

        var hornEnd = new VoxelPosition(
            hornStart.Column + ((skullEnd.Column - skullStart.Column) * along),
            hornStart.Row + ((skullEnd.Row - skullStart.Row) * along),
            hornStart.Slice + ((skullEnd.Slice - skullStart.Slice) * along));

        var segments = new EvansSegments(
            this.AxialAcross,
            this.PlaneIndex!.Value,
            hornStart,
            hornEnd,
            skullStart,
            skullEnd);

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
                RotationDegrees = RotationFromImageAxis(volume, this.AxialAcross, axis),
            };
        }
        catch (DomainRuleViolationException)
        {
            // Нулевой диаметр отсеян выше, поэтому домен отказывает здесь по
            // единственной оставшейся причине: ось не ведёт вверх-вниз, то есть
            // плоскость не аксиальная.
            return Refused(ManualEvansRefusal.PlaneIsNotAxial);
        }
    }

    /// <summary>
    /// Насколько ось измерения отклонена от горизонтали кадра.
    ///
    /// Это измеренный поворот головы в аппарате: врач выравнивается по анатомии,
    /// а горизонталь задана сеткой. Число нужно при сверке с автоматическим
    /// индексом — тот меряет вдоль строк и потому к повороту чувствителен.
    /// Возвращается острый угол: сторона, с которой отмерено, значения не имеет.
    /// </summary>
    private static double RotationFromImageAxis(
        IVoxelVolume volume,
        VolumeAxis axialAcross,
        SpatialVector axis)
    {
        var (horizontal, _) = PlaneAddressing.DirectionsOf(volume.Geometry, axialAcross);

        var cosine = Math.Abs(axis.Dot(horizontal.Normalized())) / axis.Length;

        return double.RadiansToDegrees(Math.Acos(Math.Clamp(cosine, -1.0, 1.0)));
    }

    private static SpatialVector Displacement(
        IVoxelVolume volume,
        VoxelPosition from,
        VoxelPosition to)
    {
        var start = PatientSpace.ToPatient(volume, from);
        var end = PatientSpace.ToPatient(volume, to);

        return new SpatialVector(end.X - start.X, end.Y - start.Y, end.Z - start.Z);
    }

    private static ManualEvansResult Refused(ManualEvansRefusal refusal) =>
        new() { Refusal = refusal };

    private int PlaneOf(VoxelPosition point) => PlaneAddressing.IndexOf(
        this.AxialAcross,
        (int)point.Column,
        (int)point.Row,
        (int)point.Slice);
}
