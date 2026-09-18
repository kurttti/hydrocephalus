using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Отбор по ядрам на сглаженном объёме — последняя ступень для T1 и FLAIR.
///
/// На шумных и низкоконтрастных сериях ликвор желудочков лежит близко
/// к порогу фона, и шум разбрасывает по нему отсчёты светлее порога.
/// Ядра толщиной в несколько миллиметров из такого ликвора не собираются,
/// хотя желудочки на снимке видны отчётливо. Сглаживание до полутора
/// миллиметров убирает этот разброс, не стирая стенку желудочка.
///
/// Ступень включается только после отказа обеих прежних, поэтому то, что
/// они уже измеряют, от неё не меняется. Сглаживание размывает и край
/// кадра, так что маска этой ступени должна лежать от него с запасом:
/// на прицельном блоке она иначе обрезается кадром, не касаясь его.
/// </summary>
public static partial class BaselineVentricleSegmentation
{
    private static BaselineSegmentationResult? SelectBySmoothedCores(
        IVoxelVolume volume,
        BaselineSegmentationOptions options,
        CancellationToken cancellationToken)
    {
        if (Smoothed(volume, options.CoreSmoothingMillimetres, cancellationToken) is not { } smoothed
            || SelectByCores(smoothed, IntensityThresholds.Otsu(smoothed), options, cancellationToken) is not { } cores
            || MarginToFrame(volume.Grid, cores.Labels) < options.SmoothedCoreFrameMarginMillimetres)
        {
            return null;
        }

        return Finish(volume.Grid, cores.Labels, cores.Candidate, cores.HeadVoxels, cores.Rejected, options);
    }

    /// <summary>
    /// Гауссово сглаживание до заданной ширины на полувысоте, мм. Оси, шаг
    /// которых уже не меньше этой ширины, не трогаются; если сглаживать нечего,
    /// возвращается <see langword="null"/>.
    /// </summary>
    private static SmoothedVolume? Smoothed(IVoxelVolume volume, double fullWidthMillimetres, CancellationToken cancellationToken)
    {
        var grid = volume.Grid;
        var dimensions = grid.Dimensions;
        var columns = dimensions.Columns;
        var rows = dimensions.Rows;
        var slices = dimensions.Slices;
        double[] spacings = [grid.ColumnSpacingMillimetres, grid.RowSpacingMillimetres, grid.SliceSpacingMillimetres];
        int[] counts = [columns, rows, slices];
        int[] strides = [1, columns, columns * rows];

        var values = new float[columns * rows * slices];

        for (var slice = 0; slice < slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    values[Offset(dimensions, column, row, slice)] = volume[column, row, slice];
                }
            }
        }

        var smoothedAny = false;
        var parallel = new ParallelOptions { CancellationToken = cancellationToken };

        for (var axis = 0; axis < 3; axis++)
        {
            // Ширины складываются квадратично: отсчёт уже имеет ширину своего
            // шага, и досглаживать нужно только до недостающей.
            var sigma = Math.Sqrt(Math.Max(0, (fullWidthMillimetres * fullWidthMillimetres) - (spacings[axis] * spacings[axis])))
                / FullWidthPerSigma
                / spacings[axis];

            if (sigma < MinSmoothingSigma)
            {
                continue;
            }

            smoothedAny = true;

            var kernel = GaussianKernel(sigma);
            var source = (float[])values.Clone();
            var count = counts[axis];
            var stride = strides[axis];

            // Начала линий вдоль оси: все отсчёты, чей индекс по этой оси равен нулю.
            var starts = Enumerable.Range(0, values.Length)
                .Where(offset => (offset / stride) % count == 0)
                .ToArray();

            Parallel.ForEach(starts, parallel, start => BlurLine(source, values, start, stride, count, kernel));
        }

        return smoothedAny ? new SmoothedVolume(volume.Geometry, grid, values) : null;
    }

    private static double[] GaussianKernel(double sigma)
    {
        var radius = (int)Math.Ceiling(3 * sigma);
        var kernel = new double[(2 * radius) + 1];

        for (var step = -radius; step <= radius; step++)
        {
            kernel[step + radius] = Math.Exp(-(step * step) / (2 * sigma * sigma));
        }

        return kernel;
    }

    private static void BlurLine(float[] source, float[] target, int start, int stride, int count, double[] kernel)
    {
        var radius = kernel.Length / 2;

        for (var index = 0; index < count; index++)
        {
            double sum = 0;
            double weight = 0;

            // У края кадра ядро обрезается и перенормируется: продолжать
            // линию фоном или отражением значило бы выдумать отсчёты.
            for (var step = Math.Max(-radius, -index); step <= Math.Min(radius, count - 1 - index); step++)
            {
                sum += kernel[step + radius] * source[start + ((index + step) * stride)];
                weight += kernel[step + radius];
            }

            target[start + (index * stride)] = (float)(sum / weight);
        }
    }

    /// <summary>Наименьшее расстояние от маски до края кадра, мм.</summary>
    private static double MarginToFrame(VolumeGrid grid, byte[] labels)
    {
        var dimensions = grid.Dimensions;
        var margin = double.PositiveInfinity;

        for (var offset = 0; offset < labels.Length; offset++)
        {
            if (labels[offset] == 0)
            {
                continue;
            }

            var (column, row, slice) = Locate(dimensions, offset);

            margin = Math.Min(margin, Math.Min(column, dimensions.Columns - 1 - column) * grid.ColumnSpacingMillimetres);
            margin = Math.Min(margin, Math.Min(row, dimensions.Rows - 1 - row) * grid.RowSpacingMillimetres);
            margin = Math.Min(margin, Math.Min(slice, dimensions.Slices - 1 - slice) * grid.SliceSpacingMillimetres);
        }

        return margin;
    }

    /// <summary>Отношение ширины гауссианы на полувысоте к её σ.</summary>
    private const double FullWidthPerSigma = 2.3548;

    /// <summary>Меньшее σ, в отсчётах, почти не меняет значения, и ось не сглаживается.</summary>
    private const double MinSmoothingSigma = 0.3;

    private sealed class SmoothedVolume : IVoxelVolume
    {
        private readonly float[] voxels;

        public SmoothedVolume(SeriesGeometry geometry, VolumeGrid grid, float[] voxels)
        {
            this.Geometry = geometry;
            this.Grid = grid;
            this.voxels = voxels;
            this.Minimum = voxels.Min();
            this.Maximum = voxels.Max();
        }

        public SeriesGeometry Geometry { get; }

        public VolumeGrid Grid { get; }

        public float Minimum { get; }

        public float Maximum { get; }

        public float this[int column, int row, int slice] =>
            this.voxels[Offset(this.Grid.Dimensions, column, row, slice)];
    }
}
