using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;

namespace Hydrocephalus.Inference.Measurements;

/// <summary>
/// Почему разметка не дала угла.
/// </summary>
public enum ManualCallosalAngleRefusal
{
    /// <summary>Отмечены не все три точки.</summary>
    PointsIncomplete = 1,

    /// <summary>Точки лежат на разных плоскостях.</summary>
    PointsOnDifferentPlanes = 2,

    /// <summary>Луч вырожден в точку: точка на крыше совпала с вершиной.</summary>
    RayHasZeroLength = 3,

    /// <summary>
    /// Плоскость разметки не корональная, а угол мозолистого тела определён
    /// только на ней.
    /// </summary>
    PlaneIsNotCoronal = 4,
}

/// <summary>
/// Что вышло из разметки: угол с наклоном плоскости либо названная причина отказа.
/// </summary>
public sealed record ManualCallosalAngleResult
{
    /// <summary>Угол; <see langword="null"/> при отказе.</summary>
    public Biomarker? Biomarker { get; init; }

    /// <summary>
    /// Наклон плоскости разметки вперёд-назад; <see langword="null"/> при отказе.
    /// </summary>
    public Biomarker? Pitch { get; init; }

    /// <summary>
    /// Поворот плоскости разметки влево-вправо; <see langword="null"/> при отказе.
    /// </summary>
    public Biomarker? Yaw { get; init; }

    /// <summary>Номер плоскости разметки; <see langword="null"/> при отказе.</summary>
    public int? PlaneIndex { get; init; }

    /// <summary>Причина отказа; <see langword="null"/>, если угол посчитан.</summary>
    public ManualCallosalAngleRefusal? Refusal { get; init; }

    /// <summary>Наклон плоскости в градусах, для показа на экране.</summary>
    public double? PitchDegrees => this.Pitch?.Value;

    /// <summary>Поворот плоскости в градусах, для показа на экране.</summary>
    public double? YawDegrees => this.Yaw?.Value;

    /// <summary>
    /// Все признаки одного измерения в порядке записи; пустой список при отказе.
    ///
    /// Собран здесь, а не у вызывающего: наклон и поворот плоскости без самого
    /// угла бессмысленны, а угол без них несравним с другими измерениями, и
    /// решать это заново на каждом вызове значило бы однажды решить иначе.
    /// </summary>
    public IReadOnlyList<Biomarker> Measurements => this.Biomarker is null
        || this.Pitch is null
        || this.Yaw is null
            ? []
            : [this.Biomarker, this.Pitch, this.Yaw];
}

/// <summary>
/// Угол мозолистого тела, отмеченный врачом на одном корональном срезе.
///
/// Нужен потому, что это второй признак из плана M3 и единственный, который
/// отличает иНТГ от заместительной гидроцефалии: индекс Эванса растёт и там и
/// там, а крыши желудочков расходятся по-разному. Измерим он на тех же рутинных
/// толстосрезовых сериях — определение требует одного среза, — и корональная
/// серия с покрытием не меньше 120 мм есть в наборе у 55 исследований из 57
/// против семи с объёмной (docs/data/README.md).
///
/// Считает не этот класс: он собирает три точки и отдаёт их
/// <see cref="LinearBiomarkers.CallosalAngle(IVoxelVolume, VolumeAxis, VoxelPosition, VoxelPosition, VoxelPosition, MeasurementQuality)"/>.
///
/// **Порядок точек: сначала вершина, потом крыши, и тут он важен иначе, чем у
/// индекса Эванса.** Там порядок задаёт ось измерения; здесь он называет, какая
/// из трёх точек — вершина. Угол между лучами к их порядку безразличен, то есть
/// левую и правую крыши можно отмечать в любом порядке, а вершину — нельзя.
///
/// **Поворот кадра на угол не влияет вовсе**, и это преимущество ручного пути
/// перед автоматическим: угол между двумя лучами при вершине не меняется от
/// поворота плоскости вокруг своей нормали, тогда как подгонка прямых к профилю
/// крыши от него зависит (docs/ml/README.md).
///
/// Что на угол влияет, записывается рядом с ним — см.
/// <see cref="Measure"/> и флаг качества там же.
/// </summary>
public sealed class ManualCallosalAngleMarking
{
    /// <summary>Сколько точек нужно: вершина и по одной на каждой крыше.</summary>
    public const int RequiredPoints = 3;

    private readonly List<VoxelPosition> points = [];

    /// <summary>
    /// Создаёт разметку на заданной оси.
    /// </summary>
    /// <param name="coronalAcross">Ось объёма, поперёк которой лежит корональная плоскость.</param>
    public ManualCallosalAngleMarking(VolumeAxis coronalAcross) =>
        this.CoronalAcross = coronalAcross;

    /// <summary>Ось, поперёк которой лежит плоскость разметки.</summary>
    public VolumeAxis CoronalAcross { get; }

    /// <summary>Отмеченные точки в порядке постановки.</summary>
    public IReadOnlyList<VoxelPosition> Points => this.points;

    /// <summary>Номер плоскости разметки; <see langword="null"/>, если точек нет.</summary>
    public int? PlaneIndex => this.points.Count == 0 ? null : PlaneOf(this.points[0]);

    /// <summary>Отмечены ли все три точки.</summary>
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
    /// Считает угол по отмеченным точкам.
    ///
    /// **Флаг качества — <see cref="MeasurementQuality.Questionable"/>, и это
    /// отличие от ручного индекса Эванса намеренное.** Там ручная разметка и
    /// есть референс: определение индекса требует одного аксиального среза, и
    /// отмеченный врачом срез этому определению отвечает. Здесь определение
    /// требует большего — «корональная плоскость, срез через заднюю спайку,
    /// перпендикулярно линии AC–PC» [Nakajima 2021], — а ни задняя спайка, ни
    /// линия AC–PC в приложении не ищутся. Отмеченная плоскость определению
    /// поэтому не равна, и насколько она от него отстоит, измерено:
    ///
    /// - **положение среза.** Угол меняется на 1,2–11,5° на миллиметр смещения
    ///   от плоскости задней спайки, у худшего из трёх снимков — скачок 11,5°
    ///   между соседними миллиметровыми срезами (docs/ml/README.md). Шаг
    ///   корональной серии набора — медиана 5,88 мм, то есть центр ближайшего
    ///   среза отстоит от уровня спайки до ±3 мм;
    /// - **наклон плоскости.** Отклонение от истинной корональной значимо меняет
    ///   угол у больных иНТГ и незначимо у здоровых уже при ±5–10°. В наборе
    ///   медиана отклонения 9,2° в группе иНТГ против 5,8° у контролей, а на
    ///   однородных корональных T2 — 11,1° против 5,8°.
    ///
    /// Второе записывается рядом с углом двумя признаками, первое — нет: чтобы
    /// назвать смещение среза, нужен уровень задней спайки, которого нет.
    /// Поэтому измерение сомнительно по названной причине, а не надёжно.
    ///
    /// Воспроизводимость между наблюдателями это не оценивает: она свойство
    /// набора повторных измерений, а не одного (M3, docs/roadmap.md).
    /// </summary>
    /// <param name="volume">Объём, на котором шла разметка.</param>
    /// <returns>Угол с наклоном плоскости либо причина отказа.</returns>
    public ManualCallosalAngleResult Measure(IVoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (!this.IsComplete)
        {
            return Refused(ManualCallosalAngleRefusal.PointsIncomplete);
        }

        var planes = this.points.Select(PlaneOf).Distinct().Count();

        if (planes > 1)
        {
            // До этого дойти не должно: Add начинает разметку заново, встретив
            // другую плоскость. Проверка остаётся, потому что порядок точек
            // задаёт вызывающая сторона, а косое измерение выглядит как обычное.
            return Refused(ManualCallosalAngleRefusal.PointsOnDifferentPlanes);
        }

        var vertex = this.points[0];
        var first = this.points[1];
        var second = this.points[2];

        // Вырожденный луч отсеивается здесь, до домена, по той же причине, по
        // которой индекс Эванса сам проверяет нулевой диаметр: домен отказывает
        // одним исключением на обе причины, и после проверки остаётся ровно
        // одна — плоскость не корональная.
        if (PatientSpace.DistanceMillimetres(volume, vertex, first) <= 0
            || PatientSpace.DistanceMillimetres(volume, vertex, second) <= 0)
        {
            return Refused(ManualCallosalAngleRefusal.RayHasZeroLength);
        }

        try
        {
            var measured = LinearBiomarkers.CallosalAngle(
                volume,
                this.CoronalAcross,
                vertex,
                first,
                second,
                MeasurementQuality.Questionable);

            var (pitch, yaw) = Tilt(volume.Geometry, this.CoronalAcross);

            return new ManualCallosalAngleResult
            {
                // Вычисление одно, метод получения другой — как у индекса Эванса.
                Biomarker = measured with
                {
                    Method = measured.Method with
                    {
                        Code = LinearBiomarkers.ManualCallosalAngleCode,
                    },
                },
                Pitch = PlaneTilt(
                    LinearBiomarkers.CoronalPlanePitchCode,
                    pitch,
                    volume.Geometry.Tier,
                    measured.Method.RequiredTier),
                Yaw = PlaneTilt(
                    LinearBiomarkers.CoronalPlaneYawCode,
                    yaw,
                    volume.Geometry.Tier,
                    measured.Method.RequiredTier),
                PlaneIndex = this.PlaneIndex,
            };
        }
        catch (DomainRuleViolationException)
        {
            // Вырожденный луч отсеян выше, точки на одной плоскости проверены,
            // поэтому домен отказывает здесь по единственной оставшейся
            // причине: ось не ведёт вперёд-назад.
            return Refused(ManualCallosalAngleRefusal.PlaneIsNotCoronal);
        }
    }

    /// <summary>
    /// Насколько плоскость разметки отклонена от истинной корональной.
    ///
    /// Возвращаются два угла, а не одно полное отклонение: чувствительность
    /// угла мозолистого тела зависит от оси поворота, и сложенные вместе числа
    /// обратно уже не разделить. Полное отклонение из них выводится.
    ///
    /// Опора — система координат пациента: по соглашению DICOM её +Y идёт
    /// назад, и нормаль истинной корональной плоскости лежит вдоль этой оси.
    /// Нормаль предварительно разворачивается назад, потому что сторона, в
    /// которую она смотрит, задана порядком срезов в серии, а не анатомией;
    /// после разворота знак обоих углов определён однозначно.
    ///
    /// Это **не** отклонение от перпендикуляра к линии AC–PC, которого требует
    /// определение признака: ось пациента задана укладкой на столе, а не его
    /// спайками. Измеряется та часть отклонения, которую внёс оператор,
    /// наклонив блок срезов, — она же и самая изменчивая.
    /// </summary>
    private static (double Pitch, double Yaw) Tilt(SeriesGeometry geometry, VolumeAxis axis)
    {
        var normal = PlaneAddressing.NormalOf(geometry, axis).Normalized();

        if (normal.Y < 0)
        {
            normal = new SpatialVector(-normal.X, -normal.Y, -normal.Z);
        }

        var pitch = double.RadiansToDegrees(
            Math.Atan2(normal.Z, Math.Sqrt((normal.X * normal.X) + (normal.Y * normal.Y))));

        var yaw = double.RadiansToDegrees(Math.Atan2(normal.X, normal.Y));

        return (pitch, yaw);
    }

    private static Biomarker PlaneTilt(
        string code,
        double degrees,
        AcquisitionTier availableTier,
        AcquisitionTier requiredTier) =>
        Biomarker.Create(
            new MeasurementMethod
            {
                Code = code,
                DefinitionVersion = LinearBiomarkers.DefinitionVersion,
                RequiredTier = requiredTier,
                Reference = "docs/ml/README.md, раздел «Угол мозолистого тела: чувствительность к положению плоскости»",
            },
            availableTier,
            degrees,
            MeasurementUnit.Degree,

            // Надёжность отдельная от надёжности угла: сам наклон известен
            // точно — он прочитан из геометрии серии, а не измерен врачом.
            MeasurementQuality.Reliable,
            LinearBiomarkers.CoronalPlaneTiltPlausibleRange);

    private static ManualCallosalAngleResult Refused(ManualCallosalAngleRefusal refusal) =>
        new() { Refusal = refusal };

    private int PlaneOf(VoxelPosition point) => PlaneAddressing.IndexOf(
        this.CoronalAcross,
        (int)point.Column,
        (int)point.Row,
        (int)point.Slice);
}
