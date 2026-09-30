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
    /// Поворот головы в плоскости кадра, выведенный из оси измерения;
    /// <see langword="null"/> при отказе.
    /// </summary>
    public Biomarker? Rotation { get; init; }

    /// <summary>Тот же поворот в градусах, для показа на экране.</summary>
    public double? RotationDegrees => this.Rotation?.Value;
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
/// Обе величины определения поперечные, то есть отмеряются вдоль одной оси
/// [Nakajima 2021]. Ось задаёт врач: пара точек черепа определяет направление,
/// а отрезок рогов **проецируется** на него.
///
/// Это убирает наклон нажатия, и он не умозрителен: пара рогов, отмеченная со
/// смещением в 30 строк, без проекции дала бы 0,3125 вместо 0,25 — четверть
/// сверху (см. тест). Заодно опорой перестаёт быть сетка, то есть положение
/// головы в аппарате не значит ничего.
///
/// Насколько к повороту чувствителен автоматический путь — отдельный вопрос,
/// и ответ на него измерен, а не выведен: до 15 градусов сдвиг не выделяется
/// из дискретизации (docs/data/README.md).
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
                Rotation = HeadRotation(volume, this.AxialAcross, axis, measured.Method.RequiredTier),
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
    /// Насколько ось измерения отклонена от направления влево-вправо пациента.
    ///
    /// Это и есть поворот головы: система координат пациента задана укладкой на
    /// столе, её ось X идёт налево, и если голова повёрнута, анатомическая
    /// поперечная ось с ней расходится. Число нужно при сверке с автоматическим
    /// индексом — тот меряет вдоль линии сетки и потому к повороту чувствителен.
    ///
    /// Опора берётся анатомическая, а не сеточная, и это не придирка: у объёма,
    /// полученного сагиттально, «горизонталь» плоскости по адресации сетки
    /// оказывается передне-задней осью, и отсчёт от неё даёт 90° на ровно
    /// лежащей голове. Такое и вышло на первом же прогоне по реальной серии.
    ///
    /// Ось X проецируется в плоскость измерения: при косой плоскости она из неё
    /// выходит, и угол между вектором плоскости и вектором вне её мерил бы не
    /// поворот, а наклон плоскости. Возвращается острый угол: сторона, с которой
    /// отмерено, значения не имеет.
    /// </summary>
    private static Biomarker HeadRotation(
        IVoxelVolume volume,
        VolumeAxis axialAcross,
        SpatialVector axis,
        AcquisitionTier requiredTier)
    {
        var (horizontal, vertical) = PlaneAddressing.DirectionsOf(volume.Geometry, axialAcross);

        var first = horizontal.Normalized();
        var second = vertical.Normalized();

        // Налево по соглашению DICOM — это +X системы координат пациента.
        var left = new SpatialVector(1, 0, 0);

        var inPlane = new SpatialVector(
            (first.X * left.Dot(first)) + (second.X * left.Dot(second)),
            (first.Y * left.Dot(first)) + (second.Y * left.Dot(second)),
            (first.Z * left.Dot(first)) + (second.Z * left.Dot(second)));

        var cosine = inPlane.Length <= 0
            ? 1.0
            : Math.Abs(axis.Dot(inPlane)) / (axis.Length * inPlane.Length);

        var degrees = double.RadiansToDegrees(Math.Acos(Math.Clamp(cosine, -1.0, 1.0)));

        return Biomarker.Create(
            new MeasurementMethod
            {
                Code = LinearBiomarkers.HeadRotationCode,
                DefinitionVersion = LinearBiomarkers.DefinitionVersion,
                RequiredTier = requiredTier,
                Reference = "docs/data/README.md, раздел «Ось измерения задаёт врач, а не сетка»",
            },
            volume.Geometry.Tier,
            degrees,
            MeasurementUnit.Degree,

            // Надёжность та же, что у самого измерения: угол выводится из оси,
            // которую задал врач, и отдельного источника ошибки не имеет.
            MeasurementQuality.Reliable,
            LinearBiomarkers.HeadRotationPlausibleRange);
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
