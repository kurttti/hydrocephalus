namespace Hydrocephalus.Inference.QualityControl;

/// <summary>
/// Границы, в которых вход считается пригодным.
///
/// Значения по умолчанию взяты из описания уровней входа в docs/clinical/README.md:
/// рутинные 2D-серии выборки имеют толщину среза 5–7мм, расширенный уровень требует
/// 3D-серии ≤1.5мм. Они вынесены в отдельный тип, потому что при пополнении выборки
/// пороги пересматриваются вместе с dataset manifest, а не правятся по месту.
///
/// Границы описывают только то, что видно в геометрии. Проверки, требующие вокселей
/// (двигательные артефакты, обрезание головы полем обзора, выход за пределы
/// обучающего распределения), сюда не входят.
/// </summary>
public sealed record InputQualityLimits
{
    /// <summary>Наибольшая допустимая толщина среза, мм.</summary>
    public double MaxSliceThicknessMillimetres { get; init; } = 7.0;

    /// <summary>Наименьшая правдоподобная толщина среза, мм.</summary>
    public double MinSliceThicknessMillimetres { get; init; } = 0.2;

    /// <summary>Наибольший допустимый шаг в плоскости среза, мм.</summary>
    public double MaxInPlaneSpacingMillimetres { get; init; } = 2.0;

    /// <summary>Наименьший правдоподобный шаг в плоскости среза, мм.</summary>
    public double MinInPlaneSpacingMillimetres { get; init; } = 0.05;

    /// <summary>
    /// Наибольшее допустимое отношение шагов по строкам и столбцам.
    /// Сильно неквадратный пиксель искажает линейные измерения, которые и есть
    /// единственный результат базового уровня входа.
    /// </summary>
    public double MaxInPlaneAnisotropy { get; init; } = 2.0;

    /// <summary>
    /// Наименьший размер поля обзора в плоскости среза, мм. Голова взрослого
    /// в поле меньшего размера не помещается физически.
    /// </summary>
    public double MinInPlaneExtentMillimetres { get; init; } = 150.0;

    /// <summary>
    /// Наименьшее покрытие по оси срезов для объёмного анализа, мм.
    /// Проверяется только на расширенном уровне: линейные измерения выполняются
    /// на одном срезе и полного покрытия не требуют.
    /// </summary>
    public double MinVolumeCoverageMillimetres { get; init; } = 100.0;
}
