namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Способ получения серии по DICOM-тегу MRAcquisitionType.
/// </summary>
public enum MrAcquisitionType
{
    /// <summary>Тип не указан в метаданных или не распознан.</summary>
    Unknown = 0,

    /// <summary>Двумерная серия срезов.</summary>
    TwoDimensional = 1,

    /// <summary>Трёхмерная серия (объёмное получение).</summary>
    ThreeDimensional = 2,
}

/// <summary>
/// Уровень входных данных из docs/clinical/README.md. Выводится из геометрии,
/// а не задаётся отдельно, чтобы уровень нельзя было объявить в обход фактических параметров серии.
/// </summary>
public enum AcquisitionTier
{
    /// <summary>Геометрия недостаточна даже для линейных измерений.</summary>
    Unusable = 0,

    /// <summary>Базовый уровень: рутинные 2D-серии, доступны только линейные измерения.</summary>
    Baseline = 1,

    /// <summary>Расширенный уровень: 3D-серия, пригодная для сегментации и объёмных признаков.</summary>
    Extended = 2,
}

/// <summary>
/// Геометрия серии: то, что должно быть непротиворечивым до любой обработки пикселей.
/// </summary>
public sealed record SeriesGeometry
{
    /// <summary>Максимальная толщина среза (мм), при которой серия считается пригодной для 3D-конвейера.</summary>
    public const double ExtendedTierMaxSliceThicknessMillimetres = 1.5;

    /// <summary>
    /// Допуск при сравнении шага выборки с порогом расширенного уровня, мм.
    ///
    /// Нужен потому, что шаг приходит не из тега, а выводится из положений
    /// срезов, и арифметика с плавающей точкой даёт хвост. В выборке нашлись
    /// две объёмные T1 по 104 среза толщиной ровно 1,5 мм, у которых шаг вышел
    /// 1,5000000000000675 — строгое сравнение относило их к базовому уровню, и
    /// обе теряли объёмный конвейер из-за семи десятых пикометра.
    ///
    /// Величина выбрана заведомо ниже всего, что может что-то значить: нанометр
    /// на порядки больше наблюдаемого хвоста (7·10⁻¹¹ мм) и на порядки меньше
    /// любой разницы, различимой томографом. Это поправка на арифметику, а не
    /// послабление порога: серия с шагом 1,51 мм остаётся базовой.
    /// </summary>
    public const double TierSamplingToleranceMillimetres = 1e-6;

    /// <summary>
    /// Наименьшая протяжённость съёмки, при которой желудочки могут попасть
    /// в кадр целиком, мм.
    ///
    /// Живёт в домене, потому что число нужно двоим и расходиться им нельзя:
    /// по нему **выбирается** серия для анализа и по нему же сегментация
    /// **отказывает** от прицельного блока. Два разных числа означали бы, что
    /// приложение выбирает серию, которую само потом отвергнет.
    ///
    /// Величина взята от анатомии, а не от оборудования: от основания черепа
    /// до свода около 130 мм, желудочковая система занимает середину, и сотня
    /// миллиметров — наименьшее, что вмещает её целиком при любой разумной
    /// укладке. Прицельные блоки в выборке покрывают 34–72 мм.
    /// </summary>
    public const double MinVentricleCoverageMillimetres = 100.0;

    /// <summary>Способ получения серии.</summary>
    public required MrAcquisitionType AcquisitionType { get; init; }

    /// <summary>
    /// Допустимое превышение шага над толщиной, доля. Небольшое расхождение
    /// объясняется округлением значений в тегах, а не реальным зазором.
    /// </summary>
    public const double SliceGapToleranceFraction = 0.05;

    /// <summary>Толщина среза в миллиметрах: сколько ткани возбуждено под один срез.</summary>
    public required double SliceThicknessMillimetres { get; init; }

    /// <summary>
    /// Шаг между центрами соседних срезов в миллиметрах.
    ///
    /// Хранится отдельно от толщины, потому что это разные величины: рутинные
    /// 2D-серии часто идут с зазором, и объём, посчитанный по толщине, окажется
    /// заниженным ровно на долю пропущенной ткани. Для серии из одного среза
    /// шаг равен толщине.
    /// </summary>
    public required double SliceSpacingMillimetres { get; init; }

    /// <summary>Размер пикселя в плоскости среза (мм) по строкам и столбцам.</summary>
    public required InPlaneSpacing PixelSpacing { get; init; }

    /// <summary>Число столбцов, строк и срезов.</summary>
    public required VolumeDimensions Dimensions { get; init; }

    /// <summary>Направляющий косинус строки изображения.</summary>
    public required SpatialVector RowDirection { get; init; }

    /// <summary>Направляющий косинус столбца изображения.</summary>
    public required SpatialVector ColumnDirection { get; init; }

    /// <summary>Положение первого воксела в системе координат пациента (мм).</summary>
    public required SpatialVector Origin { get; init; }

    /// <summary>
    /// Уровень входа, выводимый из фактической геометрии.
    /// Расширенный уровень требует объёмного получения и тонких срезов;
    /// всё остальное пригодное — базовый уровень с линейными измерениями.
    /// </summary>
    public AcquisitionTier Tier
    {
        get
        {
            if (!IsWellFormed)
            {
                return AcquisitionTier.Unusable;
            }

            // Уровень определяется фактической плотностью выборки, а не одной лишь
            // толщиной среза: 3D-серия с тонкими срезами, но большим шагом
            // не даёт данных для объёмных признаков.
            return AcquisitionType == MrAcquisitionType.ThreeDimensional
                && EffectiveSliceSamplingMillimetres
                    <= ExtendedTierMaxSliceThicknessMillimetres + TierSamplingToleranceMillimetres
                    ? AcquisitionTier.Extended
                    : AcquisitionTier.Baseline;
        }
    }

    /// <summary>
    /// Фактический шаг выборки по оси срезов: наибольшая из толщины и шага.
    /// </summary>
    public double EffectiveSliceSamplingMillimetres =>
        Math.Max(SliceThicknessMillimetres, SliceSpacingMillimetres);

    /// <summary>
    /// Признак зазора между срезами: шаг заметно больше толщины, то есть часть
    /// ткани между срезами не получена.
    /// </summary>
    public bool HasSliceGap =>
        SliceSpacingMillimetres > SliceThicknessMillimetres * (1 + SliceGapToleranceFraction);

    /// <summary>
    /// Нормаль плоскости среза, полученная из направляющих косинусов.
    /// </summary>
    public SpatialVector SliceNormal => RowDirection.Cross(ColumnDirection).Normalized();

    /// <summary>Плоскость получения серии.</summary>
    public ImagingPlane Plane => PatientOrientation.PlaneOf(SliceNormal);

    /// <summary>
    /// Протяжённость съёмки вдоль оси срезов, мм: сколько анатомии в кадре.
    ///
    /// Считается по шагу, а не по толщине: между срезами с зазором ткани нет,
    /// но в кадр она попадает, и для вопроса «есть ли здесь желудочки»
    /// существенно именно это.
    /// </summary>
    public double CoverageMillimetres => Dimensions.Slices * SliceSpacingMillimetres;

    /// <summary>
    /// Может ли серия содержать желудочковую систему целиком.
    ///
    /// Отдельное свойство, а не сравнение в месте вызова: число срезов
    /// протяжённости не измеряет — 24 среза по 1,5 мм дают 36 мм, а 19 срезов
    /// по 5,72 мм дают 109 мм. Прежний выбор серии сравнивал число срезов и
    /// поэтому предпочитал тонкий прицельный блок серии, покрывающей голову.
    /// </summary>
    public bool CoversVentricles => CoverageMillimetres >= MinVentricleCoverageMillimetres;

    /// <summary>
    /// Куда указывает строка изображения, то есть какая сторона пациента
    /// оказывается справа на выведенном срезе.
    /// </summary>
    public AnatomicalDirection RowDirectionTowards => PatientOrientation.Of(RowDirection);

    /// <summary>
    /// Куда указывает столбец изображения, то есть какая сторона пациента
    /// оказывается снизу на выведенном срезе.
    /// </summary>
    public AnatomicalDirection ColumnDirectionTowards => PatientOrientation.Of(ColumnDirection);

    /// <summary>
    /// Куда идёт переход к следующему срезу серии.
    /// </summary>
    public AnatomicalDirection SliceDirectionTowards => PatientOrientation.Of(SliceNormal);

    /// <summary>
    /// Признак того, что геометрия внутренне непротиворечива: положительные размеры,
    /// ненулевые и неколлинеарные направляющие косинусы.
    /// </summary>
    public bool IsWellFormed =>
        SliceThicknessMillimetres > 0
        && SliceSpacingMillimetres > 0
        && PixelSpacing.RowMillimetres > 0
        && PixelSpacing.ColumnMillimetres > 0
        && Dimensions.Columns > 0
        && Dimensions.Rows > 0
        && Dimensions.Slices > 0
        && RowDirection.Length > 0
        && ColumnDirection.Length > 0;
}

/// <summary>
/// Размер пикселя в плоскости среза.
/// </summary>
/// <param name="RowMillimetres">Шаг между строками, мм.</param>
/// <param name="ColumnMillimetres">Шаг между столбцами, мм.</param>
public readonly record struct InPlaneSpacing(double RowMillimetres, double ColumnMillimetres);

/// <summary>
/// Размеры объёма в вокселах.
/// </summary>
/// <param name="Columns">Число столбцов.</param>
/// <param name="Rows">Число строк.</param>
/// <param name="Slices">Число срезов.</param>
public readonly record struct VolumeDimensions(int Columns, int Rows, int Slices);
