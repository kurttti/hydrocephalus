namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Ось объёма, вдоль которой перелистываются плоскости.
/// </summary>
public enum VolumeAxis
{
    /// <summary>Поперёк срезов: та плоскость, в которой серия получена.</summary>
    AcrossSlices = 0,

    /// <summary>Поперёк строк.</summary>
    AcrossRows = 1,

    /// <summary>Поперёк столбцов.</summary>
    AcrossColumns = 2,
}

/// <summary>
/// Размер плоскости в отсчётах и физический шаг её пикселя.
/// </summary>
/// <param name="Width">Ширина в отсчётах.</param>
/// <param name="Height">Высота в отсчётах.</param>
/// <param name="PixelWidthMillimetres">Физическая ширина пикселя, мм.</param>
/// <param name="PixelHeightMillimetres">Физическая высота пикселя, мм.</param>
public readonly record struct PlaneExtent(
    int Width,
    int Height,
    double PixelWidthMillimetres,
    double PixelHeightMillimetres);

/// <summary>
/// Соответствие точки плоскости и отсчёта объёма.
///
/// Живёт в доменном слое и используется всеми, кто режет объём на плоскости:
/// и вывод изображения, и наложение маски. ADR 0007 требует, чтобы маска
/// попиксельно совпадала с той, по которой посчитаны признаки; если адресацию
/// повторить в двух местах, это совпадение держится на дисциплине и проверяется
/// тестом. Здесь оно верно по построению — расходиться нечему.
/// </summary>
public static class PlaneAddressing
{
    /// <summary>
    /// Сообщает, сколько плоскостей есть вдоль оси.
    /// </summary>
    /// <param name="grid">Сетка отсчётов.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <returns>Число плоскостей.</returns>
    public static int CountAlong(VolumeGrid grid, VolumeAxis axis) => axis switch
    {
        VolumeAxis.AcrossSlices => grid.Dimensions.Slices,
        VolumeAxis.AcrossRows => grid.Dimensions.Rows,
        VolumeAxis.AcrossColumns => grid.Dimensions.Columns,
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>
    /// Возвращает размеры плоскости и физический размер её пикселя.
    /// </summary>
    /// <param name="grid">Сетка отсчётов.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <returns>Размеры плоскости.</returns>
    public static PlaneExtent ExtentOf(VolumeGrid grid, VolumeAxis axis)
    {
        var dimensions = grid.Dimensions;

        return axis switch
        {
            VolumeAxis.AcrossSlices => new PlaneExtent(
                dimensions.Columns,
                dimensions.Rows,
                grid.ColumnSpacingMillimetres,
                grid.RowSpacingMillimetres),

            VolumeAxis.AcrossRows => new PlaneExtent(
                dimensions.Columns,
                dimensions.Slices,
                grid.ColumnSpacingMillimetres,
                grid.SliceSpacingMillimetres),

            VolumeAxis.AcrossColumns => new PlaneExtent(
                dimensions.Rows,
                dimensions.Slices,
                grid.RowSpacingMillimetres,
                grid.SliceSpacingMillimetres),

            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    /// <summary>
    /// Переводит точку плоскости в координаты отсчёта объёма.
    /// </summary>
    /// <param name="axis">Ось перелистывания.</param>
    /// <param name="index">Номер плоскости вдоль оси.</param>
    /// <param name="x">Координата по ширине плоскости.</param>
    /// <param name="y">Координата по высоте плоскости.</param>
    /// <returns>Координаты отсчёта.</returns>
    public static (int Column, int Row, int Slice) Locate(VolumeAxis axis, int index, int x, int y) =>
        axis switch
        {
            VolumeAxis.AcrossSlices => (x, y, index),
            VolumeAxis.AcrossRows => (x, index, y),
            VolumeAxis.AcrossColumns => (index, x, y),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

    /// <summary>
    /// Возвращает направления, вдоль которых идут оси плоскости.
    /// </summary>
    /// <param name="geometry">Геометрия получения серии.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <returns>Направление вправо и направление вниз по изображению.</returns>
    public static (SpatialVector Horizontal, SpatialVector Vertical) DirectionsOf(
        SeriesGeometry geometry,
        VolumeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        return axis switch
        {
            VolumeAxis.AcrossSlices => (geometry.RowDirection, geometry.ColumnDirection),
            VolumeAxis.AcrossRows => (geometry.RowDirection, geometry.SliceNormal),
            VolumeAxis.AcrossColumns => (geometry.ColumnDirection, geometry.SliceNormal),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }
}
