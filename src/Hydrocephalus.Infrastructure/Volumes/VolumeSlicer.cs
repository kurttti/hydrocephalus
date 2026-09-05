using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Volumes;

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
/// Готовая к выводу плоскость.
///
/// Размер пикселя хранится в миллиметрах по обеим осям и, как правило, различается:
/// объём анизотропен, и вывод «пиксель в пиксель» растянул бы анатомию. Пропорции —
/// забота того, кто рисует, но данные для них даёт этот тип, а не догадка.
/// </summary>
public sealed record PlaneImage
{
    /// <summary>Ширина в пикселях.</summary>
    public required int Width { get; init; }

    /// <summary>Высота в пикселях.</summary>
    public required int Height { get; init; }

    /// <summary>Яркости 0–255, строка за строкой.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Физическая ширина пикселя, мм.</summary>
    public required double PixelWidthMillimetres { get; init; }

    /// <summary>Физическая высота пикселя, мм.</summary>
    public required double PixelHeightMillimetres { get; init; }

    /// <summary>Подписи сторон изображения.</summary>
    public required EdgeLabels Labels { get; init; }

    /// <summary>Анатомическая плоскость этого вида.</summary>
    public required ImagingPlane Plane { get; init; }
}

/// <summary>
/// Извлечение плоскостей из объёма.
///
/// Плоскости берутся по собственным осям объёма, без интерполяции: каждый пиксель
/// вывода — ровно один воксель. Косой реформат появится вместе с приведением
/// к изотропной сетке (ADR 0007); до тех пор врач видит именно те значения,
/// которые прочитаны из файлов, а не результат ещё одного преобразования.
///
/// Анатомическая принадлежность каждого вида выводится из направляющих косинусов,
/// а не назначается по номеру оси: серия могла быть получена в любой плоскости.
/// </summary>
public static class VolumeSlicer
{
    /// <summary>
    /// Сообщает, сколько плоскостей есть вдоль оси.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <returns>Число плоскостей.</returns>
    public static int CountAlong(VoxelVolume volume, VolumeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var dimensions = volume.Geometry.Dimensions;

        return axis switch
        {
            VolumeAxis.AcrossSlices => dimensions.Slices,
            VolumeAxis.AcrossRows => dimensions.Rows,
            VolumeAxis.AcrossColumns => dimensions.Columns,
            _ => 0,
        };
    }

    /// <summary>
    /// Извлекает плоскость и применяет окно.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <param name="index">Номер плоскости вдоль оси.</param>
    /// <param name="window">Окно и уровень.</param>
    /// <returns>Готовая к выводу плоскость.</returns>
    public static PlaneImage Extract(VoxelVolume volume, VolumeAxis axis, int index, WindowLevel window)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CountAlong(volume, axis));

        var geometry = volume.Geometry;
        var dimensions = geometry.Dimensions;

        return axis switch
        {
            VolumeAxis.AcrossSlices => Build(
                volume,
                window,
                dimensions.Columns,
                dimensions.Rows,
                (x, y) => (x, y, index),
                geometry.PixelSpacing.ColumnMillimetres,
                geometry.PixelSpacing.RowMillimetres,
                geometry.RowDirection,
                geometry.ColumnDirection),

            VolumeAxis.AcrossRows => Build(
                volume,
                window,
                dimensions.Columns,
                dimensions.Slices,
                (x, y) => (x, index, y),
                geometry.PixelSpacing.ColumnMillimetres,
                geometry.SliceSpacingMillimetres,
                geometry.RowDirection,
                geometry.SliceNormal),

            VolumeAxis.AcrossColumns => Build(
                volume,
                window,
                dimensions.Rows,
                dimensions.Slices,
                (x, y) => (index, x, y),
                geometry.PixelSpacing.RowMillimetres,
                geometry.SliceSpacingMillimetres,
                geometry.ColumnDirection,
                geometry.SliceNormal),

            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };
    }

    private static PlaneImage Build(
        VoxelVolume volume,
        WindowLevel window,
        int width,
        int height,
        Func<int, int, (int Column, int Row, int Slice)> locate,
        double pixelWidthMillimetres,
        double pixelHeightMillimetres,
        SpatialVector horizontal,
        SpatialVector vertical)
    {
        var pixels = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (column, row, slice) = locate(x, y);
                pixels[(y * width) + x] = window.Map(volume[column, row, slice]);
            }
        }

        return new PlaneImage
        {
            Width = width,
            Height = height,
            Pixels = pixels,
            PixelWidthMillimetres = pixelWidthMillimetres,
            PixelHeightMillimetres = pixelHeightMillimetres,
            Labels = PatientOrientation.ForImagePlane(horizontal, vertical),

            // Плоскость называется по своей нормали, а нормаль вида —
            // векторное произведение его собственных осей.
            Plane = PatientOrientation.PlaneOf(horizontal.Cross(vertical)),
        };
    }
}
