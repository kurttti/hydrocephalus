using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Готовая к выводу плоскость.
///
/// Размер пикселя хранится в миллиметрах по обеим осям и, как правило, различается:
/// объём анизотропен, и вывод «пиксель в пиксель» растянул бы анатомию. Пропорции —
/// забота того, кто рисует, но данные для них даёт этот тип, а не догадка.
///
/// Класс, а не record: значение здесь — массив яркостей, и сравнение по значению
/// у record сравнивало бы ссылки на него. Два одинаковых вида оказались бы
/// неравными, а хеш считался бы по ссылке — ошибка, которую заметит только тот,
/// кто положит вид в множество или кэш.
/// </summary>
public sealed class PlaneImage
{
    /// <summary>Ширина в пикселях.</summary>
    public required int Width { get; init; }

    /// <summary>Высота в пикселях.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// Яркости 0–255, строка за строкой.
    ///
    /// Массив отдаётся как есть, без копии: вывод переносит его прямо в битмап,
    /// и лишняя копия объёмного вида стоит заметно. Изменять его на месте нельзя.
    /// </summary>
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
///
/// Размеры и шаг берутся из сетки отсчётов, а направления — из геометрии получения:
/// после ресэмплинга сетка другая, а стороны пациента те же.
///
/// Адресация плоскости общая с наложением маски (<see cref="PlaneAddressing"/>):
/// совпадение маски со срезом обязано быть верным по построению, а не держаться
/// на том, что две реализации не разошлись.
/// </summary>
public static class VolumeSlicer
{
    /// <summary>
    /// Сообщает, сколько плоскостей есть вдоль оси.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <returns>Число плоскостей.</returns>
    public static int CountAlong(IVoxelVolume volume, VolumeAxis axis)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return PlaneAddressing.CountAlong(volume.Grid, axis);
    }

    /// <summary>
    /// Извлекает плоскость и применяет окно.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="axis">Ось перелистывания.</param>
    /// <param name="index">Номер плоскости вдоль оси.</param>
    /// <param name="window">Окно и уровень.</param>
    /// <returns>Готовая к выводу плоскость.</returns>
    public static PlaneImage Extract(IVoxelVolume volume, VolumeAxis axis, int index, WindowLevel window)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, CountAlong(volume, axis));

        var extent = PlaneAddressing.ExtentOf(volume.Grid, axis);
        var (horizontal, vertical) = PlaneAddressing.DirectionsOf(volume.Geometry, axis);

        var pixels = new byte[extent.Width * extent.Height];

        for (var y = 0; y < extent.Height; y++)
        {
            for (var x = 0; x < extent.Width; x++)
            {
                var (column, row, slice) = PlaneAddressing.Locate(axis, index, x, y);

                pixels[(y * extent.Width) + x] = window.Map(volume[column, row, slice]);
            }
        }

        return new PlaneImage
        {
            Width = extent.Width,
            Height = extent.Height,
            Pixels = pixels,
            PixelWidthMillimetres = extent.PixelWidthMillimetres,
            PixelHeightMillimetres = extent.PixelHeightMillimetres,
            Labels = PatientOrientation.ForImagePlane(horizontal, vertical),

            // Плоскость называется по своей нормали, а нормаль вида —
            // векторное произведение его собственных осей.
            Plane = PatientOrientation.PlaneOf(horizontal.Cross(vertical)),
        };
    }
}
