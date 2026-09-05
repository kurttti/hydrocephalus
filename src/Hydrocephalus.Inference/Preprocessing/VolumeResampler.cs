using System.Globalization;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Preprocessing;

/// <summary>
/// Приведение объёма к изотропной сетке — этап предобработки перед сегментацией
/// и вычислением признаков.
///
/// Признаки считаются на приведённой сетке, и на ней же врач подтверждает маски
/// (ADR 0007): показывать одно, а измерять по другому нельзя.
///
/// Интерполяция трилинейная. Метод ближайшего соседа дал бы ступеньки на границах
/// структур, а именно по границам считаются линейные размеры и объёмы; более
/// высокие порядки (например, кубическая) дают выбросы за пределы исходного
/// диапазона значений, из-за чего в маске появляется интенсивность, которой
/// в ткани не было.
/// </summary>
public static class VolumeResampler
{
    /// <summary>Наименьший допустимый шаг целевой сетки, мм.</summary>
    public const double MinTargetSpacingMillimetres = 0.1;

    /// <summary>
    /// Наибольшее число отсчётов приведённого объёма. Защита от сетки, которую
    /// не удержит память: шаг задаётся снаружи, и опечатка в нём иначе привела бы
    /// к попытке выделить десятки гигабайт.
    /// </summary>
    public const long MaxVoxelCount = 512L * 512 * 1024;

    /// <summary>
    /// Приводит объём к изотропной сетке с заданным шагом.
    /// </summary>
    /// <param name="source">Исходный объём.</param>
    /// <param name="targetSpacingMillimetres">Шаг целевой сетки, мм.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Объём на изотропной сетке.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Если шаг вне допустимых пределов.</exception>
    /// <exception cref="InvalidOperationException">Если исходная сетка непригодна.</exception>
    public static ResampledVolume ToIsotropic(
        IVoxelVolume source,
        double targetSpacingMillimetres,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            targetSpacingMillimetres,
            MinTargetSpacingMillimetres);

        var grid = source.Grid;

        EnsureUsable(grid);

        var target = TargetGrid(grid, targetSpacingMillimetres);

        var voxels = new float[
            (long)target.Dimensions.Columns * target.Dimensions.Rows * target.Dimensions.Slices];

        Fill(source, grid, target, targetSpacingMillimetres, voxels, cancellationToken);

        return new ResampledVolume(source.Geometry, target, voxels);
    }

    /// <summary>
    /// Выбирает шаг, при котором приведение не выдумывает разрешения:
    /// наименьший шаг исходной сетки.
    ///
    /// Взять меньший было бы соблазнительно ради гладкой картинки, но лишние
    /// отсчёты не несут данных, а стоят памяти и времени на каждом дальнейшем
    /// этапе.
    /// </summary>
    /// <param name="source">Исходный объём.</param>
    /// <returns>Шаг целевой сетки, мм.</returns>
    public static double NaturalSpacingOf(IVoxelVolume source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var grid = source.Grid;

        return Math.Max(
            MinTargetSpacingMillimetres,
            Math.Min(
                grid.ColumnSpacingMillimetres,
                Math.Min(grid.RowSpacingMillimetres, grid.SliceSpacingMillimetres)));
    }

    private static void EnsureUsable(VolumeGrid grid)
    {
        if (grid.ColumnSpacingMillimetres <= 0
            || grid.RowSpacingMillimetres <= 0
            || grid.SliceSpacingMillimetres <= 0)
        {
            throw new InvalidOperationException(
                "The source grid declares a non-positive spacing and cannot be resampled.");
        }

        if (grid.Dimensions.Columns <= 0 || grid.Dimensions.Rows <= 0 || grid.Dimensions.Slices <= 0)
        {
            throw new InvalidOperationException("The source grid is empty.");
        }
    }

    private static VolumeGrid TargetGrid(VolumeGrid source, double spacing)
    {
        // Сохраняется физический размер, а не число отсчётов: цель приведения —
        // та же анатомия на другой сетке.
        var columns = CountFor(source.WidthMillimetres, spacing);
        var rows = CountFor(source.HeightMillimetres, spacing);
        var slices = CountFor(source.DepthMillimetres, spacing);

        var total = (long)columns * rows * slices;

        if (total > MaxVoxelCount)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Resampling to {0} mm would need {1} voxels, above the accepted limit.",
                spacing,
                total));
        }

        return new VolumeGrid(new VolumeDimensions(columns, rows, slices), spacing, spacing, spacing);
    }

    private static int CountFor(double extentMillimetres, double spacing) =>
        (int)Math.Floor((extentMillimetres / spacing) + 1e-9) + 1;

    private static void Fill(
        IVoxelVolume source,
        VolumeGrid sourceGrid,
        VolumeGrid target,
        double spacing,
        float[] voxels,
        CancellationToken cancellationToken)
    {
        var columnScale = spacing / sourceGrid.ColumnSpacingMillimetres;
        var rowScale = spacing / sourceGrid.RowSpacingMillimetres;
        var sliceScale = spacing / sourceGrid.SliceSpacingMillimetres;

        var dimensions = target.Dimensions;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sourceSlice = slice * sliceScale;

            for (var row = 0; row < dimensions.Rows; row++)
            {
                var sourceRow = row * rowScale;
                var offset = ((slice * dimensions.Rows) + row) * dimensions.Columns;

                for (var column = 0; column < dimensions.Columns; column++)
                {
                    voxels[offset + column] = Sample(
                        source,
                        sourceGrid.Dimensions,
                        column * columnScale,
                        sourceRow,
                        sourceSlice);
                }
            }
        }
    }

    /// <summary>
    /// Трилинейная выборка. Координаты за краем прижимаются к последнему отсчёту:
    /// экстраполяция за пределы полученных данных дала бы значения, которых
    /// томограф не измерял.
    /// </summary>
    private static float Sample(
        IVoxelVolume source,
        VolumeDimensions dimensions,
        double column,
        double row,
        double slice)
    {
        var (c0, c1, cf) = Neighbours(column, dimensions.Columns);
        var (r0, r1, rf) = Neighbours(row, dimensions.Rows);
        var (s0, s1, sf) = Neighbours(slice, dimensions.Slices);

        var front = Bilinear(source, c0, c1, cf, r0, r1, rf, s0);

        if (s0 == s1)
        {
            return front;
        }

        var back = Bilinear(source, c0, c1, cf, r0, r1, rf, s1);

        return (float)(front + ((back - front) * sf));
    }

    private static float Bilinear(
        IVoxelVolume source,
        int c0,
        int c1,
        double cf,
        int r0,
        int r1,
        double rf,
        int slice)
    {
        var top = Linear(source[c0, r0, slice], source[c1, r0, slice], cf);

        if (r0 == r1)
        {
            return top;
        }

        var bottom = Linear(source[c0, r1, slice], source[c1, r1, slice], cf);

        return (float)(top + ((bottom - top) * rf));
    }

    private static float Linear(float first, float second, double fraction) =>
        (float)(first + ((second - first) * fraction));

    private static (int Low, int High, double Fraction) Neighbours(double position, int count)
    {
        if (position <= 0)
        {
            return (0, 0, 0);
        }

        if (position >= count - 1)
        {
            return (count - 1, count - 1, 0);
        }

        var low = (int)Math.Floor(position);

        return (low, low + 1, position - low);
    }
}
