namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Сетка отсчётов объёма: сколько их и с каким шагом они разложены.
///
/// Отдельно от <see cref="SeriesGeometry"/> намеренно. Геометрия описывает, как
/// серия получена, и это свойство исследования — оно не меняется от того, что
/// объём переложили на другую сетку. Ресэмплинг меняет только сетку.
///
/// Различие не косметическое: уровень входа выводится из геометрии получения,
/// и объём, приведённый к шагу 1мм, не становится от этого пригодным для
/// объёмных признаков. Интерполяция не добавляет данных.
/// </summary>
/// <param name="Dimensions">Число отсчётов по столбцам, строкам и срезам.</param>
/// <param name="ColumnSpacingMillimetres">Шаг между столбцами, мм.</param>
/// <param name="RowSpacingMillimetres">Шаг между строками, мм.</param>
/// <param name="SliceSpacingMillimetres">Шаг между срезами, мм.</param>
public readonly record struct VolumeGrid(
    VolumeDimensions Dimensions,
    double ColumnSpacingMillimetres,
    double RowSpacingMillimetres,
    double SliceSpacingMillimetres)
{
    /// <summary>Признак изотропности: шаг одинаков по всем трём осям.</summary>
    public bool IsIsotropic =>
        Math.Abs(ColumnSpacingMillimetres - RowSpacingMillimetres) < Tolerance
        && Math.Abs(RowSpacingMillimetres - SliceSpacingMillimetres) < Tolerance;

    /// <summary>Физический размер вдоль столбцов, мм.</summary>
    public double WidthMillimetres => Extent(Dimensions.Columns, ColumnSpacingMillimetres);

    /// <summary>Физический размер вдоль строк, мм.</summary>
    public double HeightMillimetres => Extent(Dimensions.Rows, RowSpacingMillimetres);

    /// <summary>Физический размер вдоль срезов, мм.</summary>
    public double DepthMillimetres => Extent(Dimensions.Slices, SliceSpacingMillimetres);

    /// <summary>Допуск сравнения шагов, мм.</summary>
    private const double Tolerance = 1e-6;

    /// <summary>
    /// Расстояние между центрами первого и последнего отсчёта.
    /// Считается по промежуткам, а не по числу отсчётов: у серии из одного
    /// среза протяжённость нулевая, а не равная шагу.
    /// </summary>
    private static double Extent(int count, double spacing) =>
        count > 1 ? (count - 1) * spacing : 0;
}

/// <summary>
/// Загруженный объём: значения в единицах модальности вместе с тем, как серия
/// получена и как разложены отсчёты.
///
/// Контракт живёт в доменном слое, чтобы им могли пользоваться и загрузчик
/// в инфраструктуре, и предобработка в слое инференса, не ссылаясь друг на друга.
/// Сам буфер контракт не отдаёт: способ хранения — дело реализации, а через
/// границу <c>IInferenceEngine</c> объёмы не передаются вовсе (ADR 0002).
/// </summary>
public interface IVoxelVolume
{
    /// <summary>Геометрия получения серии. Ресэмплингом не меняется.</summary>
    SeriesGeometry Geometry { get; }

    /// <summary>Сетка, на которой лежат отсчёты.</summary>
    VolumeGrid Grid { get; }

    /// <summary>Наименьшее значение в объёме.</summary>
    float Minimum { get; }

    /// <summary>Наибольшее значение в объёме.</summary>
    float Maximum { get; }

    /// <summary>
    /// Значение отсчёта.
    /// </summary>
    /// <param name="column">Номер столбца.</param>
    /// <param name="row">Номер строки.</param>
    /// <param name="slice">Номер среза.</param>
    /// <returns>Значение в единицах модальности.</returns>
    float this[int column, int row, int slice] { get; }
}
