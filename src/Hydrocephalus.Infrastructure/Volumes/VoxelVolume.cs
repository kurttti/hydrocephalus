using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Загруженный объём рабочей копии.
///
/// Значения хранятся в единицах модальности, то есть после применения
/// RescaleSlope и RescaleIntercept: два срезa одной серии могут прийти с разными
/// коэффициентами, и сравнение сырых значений между ними бессмысленно.
///
/// Объём живёт в инфраструктурном слое и не пересекает границу
/// <c>IInferenceEngine</c>: контракт передаёт ссылку на рабочую копию, а не буфер
/// (ADR 0002).
/// </summary>
public sealed class VoxelVolume
{
    private readonly float[] voxels;

    internal VoxelVolume(SeriesGeometry geometry, float[] voxels, WindowLevel? suggestedWindow = null)
    {
        this.Geometry = geometry;
        this.voxels = voxels;
        this.SuggestedWindow = suggestedWindow;

        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;

        foreach (var value in voxels)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }

        this.Minimum = voxels.Length == 0 ? 0 : minimum;
        this.Maximum = voxels.Length == 0 ? 0 : maximum;
    }

    /// <summary>Геометрия серии, из которой собран объём.</summary>
    public SeriesGeometry Geometry { get; }

    /// <summary>
    /// Окно и уровень, заданные в тегах серии, если они там были.
    /// Значение из тегов выбрал тот, кто снимал, поэтому оно предпочтительнее
    /// расчётного — но лишь пока оно что-то показывает на этом объёме (ADR 0007).
    /// </summary>
    public WindowLevel? SuggestedWindow { get; }

    /// <summary>Наименьшее значение в объёме, в единицах модальности.</summary>
    public float Minimum { get; }

    /// <summary>Наибольшее значение в объёме, в единицах модальности.</summary>
    public float Maximum { get; }

    /// <summary>Все воксели в порядке «столбец быстрее строки, строка быстрее среза».</summary>
    public ReadOnlySpan<float> Voxels => this.voxels;

    /// <summary>
    /// Значение вокселя.
    /// </summary>
    /// <param name="column">Номер столбца.</param>
    /// <param name="row">Номер строки.</param>
    /// <param name="slice">Номер среза в порядке возрастания координаты вдоль нормали.</param>
    /// <returns>Значение в единицах модальности.</returns>
    public float this[int column, int row, int slice]
    {
        get
        {
            var dimensions = this.Geometry.Dimensions;

            ArgumentOutOfRangeException.ThrowIfNegative(column);
            ArgumentOutOfRangeException.ThrowIfNegative(row);
            ArgumentOutOfRangeException.ThrowIfNegative(slice);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, dimensions.Columns);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(row, dimensions.Rows);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slice, dimensions.Slices);

            return this.voxels[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column];
        }
    }
}
