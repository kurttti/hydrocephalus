using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Соответствие номера метки и анатомической структуры.
///
/// Версия обязательна и попадает в отчёт: то же число в маске при другой версии
/// карты означает другую структуру, и результат, полученный до смены карты,
/// нельзя сравнивать с полученным после.
/// </summary>
public sealed record LabelMap
{
    /// <summary>Номер, которым помечен фон.</summary>
    public const byte BackgroundLabel = 0;

    /// <summary>Версия карты меток.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// Структуры по номерам, начиная с первого. Фон в перечне не участвует:
    /// он не структура, и объём фона никому не нужен.
    /// </summary>
    public required IReadOnlyList<AnatomicalLabel> Labels { get; init; }

    /// <summary>Число меток, включая фон.</summary>
    public int Count => this.Labels.Count + 1;

    /// <summary>
    /// Возвращает структуру по номеру метки.
    /// </summary>
    /// <param name="label">Номер метки.</param>
    /// <returns>Структура либо <see langword="null"/> для фона.</returns>
    /// <exception cref="DomainRuleViolationException">Если номер вне карты.</exception>
    public AnatomicalLabel? StructureOf(byte label)
    {
        if (label == BackgroundLabel)
        {
            return null;
        }

        if (label > this.Labels.Count)
        {
            // Метка вне карты означает, что маска и карта из разных версий.
            // Считать по такой маске объёмы значило бы приписать числа
            // неизвестно каким структурам.
            throw new DomainRuleViolationException(
                $"Label {label} is outside label map version '{this.Version}'.");
        }

        return this.Labels[label - 1];
    }
}

/// <summary>
/// Маска сегментации: номер структуры в каждом отсчёте сетки.
///
/// Лежит на той же сетке, что и объём, по которому посчитана. Это условие
/// проверяется при создании, а не подразумевается: маска, снятая с другой сетки,
/// накладывается на изображение со смещением и при этом выглядит правдоподобно.
/// </summary>
public sealed class VoxelMask
{
    private readonly byte[] labels;

    /// <summary>
    /// Создаёт маску.
    /// </summary>
    /// <param name="grid">Сетка, на которой лежит маска.</param>
    /// <param name="map">Карта меток.</param>
    /// <param name="labels">Номера меток по отсчётам.</param>
    /// <exception cref="DomainRuleViolationException">
    /// Если размер массива не совпадает с сеткой.
    /// </exception>
    public VoxelMask(VolumeGrid grid, LabelMap map, byte[] labels)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(labels);

        var expected = (long)grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices;

        if (labels.LongLength != expected)
        {
            throw new DomainRuleViolationException(
                $"The mask holds {labels.LongLength} labels but the grid needs {expected}.");
        }

        this.Grid = grid;
        this.Map = map;
        this.labels = labels;
    }

    /// <summary>Сетка, на которой лежит маска.</summary>
    public VolumeGrid Grid { get; }

    /// <summary>Карта меток.</summary>
    public LabelMap Map { get; }

    /// <summary>
    /// Номер метки в отсчёте.
    /// </summary>
    /// <param name="column">Номер столбца.</param>
    /// <param name="row">Номер строки.</param>
    /// <param name="slice">Номер среза.</param>
    /// <returns>Номер метки.</returns>
    public byte this[int column, int row, int slice]
    {
        get
        {
            var dimensions = this.Grid.Dimensions;

            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            ArgumentOutOfRangeException.ThrowIfNegative(slice);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, dimensions.Columns);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, dimensions.Rows);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slice, dimensions.Slices);

            return this.labels[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column];
        }
    }

    /// <summary>
    /// Проверяет, что маска лежит на той же сетке, что и объём.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <returns><see langword="true"/>, если сетки совпадают.</returns>
    public bool Fits(IVoxelVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);

        return this.Grid == volume.Grid;
    }

    /// <summary>
    /// Извлекает плоскость маски.
    ///
    /// Адресация та же, что у среза изображения (<see cref="PlaneAddressing"/>),
    /// поэтому наложение совпадает попиксельно по построению — требование ADR 0007.
    /// </summary>
    /// <param name="axis">Ось перелистывания.</param>
    /// <param name="index">Номер плоскости вдоль оси.</param>
    /// <returns>Номера меток плоскости, строка за строкой.</returns>
    public byte[] ExtractPlane(VolumeAxis axis, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            index,
            PlaneAddressing.CountAlong(this.Grid, axis));

        var extent = PlaneAddressing.ExtentOf(this.Grid, axis);
        var plane = new byte[extent.Width * extent.Height];

        for (var y = 0; y < extent.Height; y++)
        {
            for (var x = 0; x < extent.Width; x++)
            {
                var (column, row, slice) = PlaneAddressing.Locate(axis, index, x, y);

                plane[(y * extent.Width) + x] = this[column, row, slice];
            }
        }

        return plane;
    }
}
