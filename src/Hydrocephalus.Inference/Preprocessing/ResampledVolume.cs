using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Preprocessing;

/// <summary>
/// Объём, переложенный на изотропную сетку.
///
/// Геометрия получения хранится неизменной — той же, что у исходной серии.
/// Ресэмплинг меняет только сетку отсчётов и не может улучшить уровень входа:
/// 2D-серия с шагом 5мм, приведённая к 1мм, остаётся базовым уровнем, потому
/// что интерполяция не добавляет данных, а лишь распределяет имеющиеся.
/// </summary>
public sealed class ResampledVolume : IVoxelVolume
{
    private readonly float[] voxels;

    internal ResampledVolume(SeriesGeometry geometry, VolumeGrid grid, float[] voxels)
    {
        this.Geometry = geometry;
        this.Grid = grid;
        this.voxels = voxels;

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

    /// <summary>Геометрия получения исходной серии.</summary>
    public SeriesGeometry Geometry { get; }

    /// <summary>Изотропная сетка отсчётов.</summary>
    public VolumeGrid Grid { get; }

    /// <summary>Наименьшее значение в объёме.</summary>
    public float Minimum { get; }

    /// <summary>Наибольшее значение в объёме.</summary>
    public float Maximum { get; }

    /// <summary>
    /// Значение отсчёта.
    /// </summary>
    /// <param name="column">Номер столбца.</param>
    /// <param name="row">Номер строки.</param>
    /// <param name="slice">Номер среза.</param>
    /// <returns>Значение в единицах модальности.</returns>
    public float this[int column, int row, int slice]
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

            return this.voxels[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column];
        }
    }
}
