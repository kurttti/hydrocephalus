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

    /// <summary>Способ получения серии.</summary>
    public required MrAcquisitionType AcquisitionType { get; init; }

    /// <summary>Толщина среза в миллиметрах.</summary>
    public required double SliceThicknessMillimetres { get; init; }

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

            return AcquisitionType == MrAcquisitionType.ThreeDimensional
                && SliceThicknessMillimetres <= ExtendedTierMaxSliceThicknessMillimetres
                    ? AcquisitionTier.Extended
                    : AcquisitionTier.Baseline;
        }
    }

    /// <summary>
    /// Признак того, что геометрия внутренне непротиворечива: положительные размеры,
    /// ненулевые и неколлинеарные направляющие косинусы.
    /// </summary>
    public bool IsWellFormed =>
        SliceThicknessMillimetres > 0
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
