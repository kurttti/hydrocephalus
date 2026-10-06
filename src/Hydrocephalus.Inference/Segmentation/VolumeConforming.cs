using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Приведение снимка к тому виду, на котором обучена модель: куб 256 с шагом
/// 1 мм, укладка LIA, яркость 0–255.
///
/// Без приведения модель не работает: сеть принимает семь соседних срезов при
/// базовом разрешении 1 мм, и на серии 6 мм такая стопка охватывает тридцать с
/// лишним миллиметров анатомии вместо семи. На непривёденном объёме она
/// размечала шесть вокселей вместо желудочков (docs/data/README.md).
///
/// Повторяет порядок действий FastSurfer: сначала разворот и выборка, затем
/// масштабирование яркости по **исходным** отсчётам, а не по развёрнутым.
/// Правильность проверяется не согласием с чужим кодом построчно, а сверкой с
/// его выводом на настоящих сериях.
/// </summary>
public static class VolumeConforming
{
    /// <summary>Сторона куба, к которому приводится объём.</summary>
    public const int Size = 256;

    /// <summary>Шаг приведённого объёма, мм.</summary>
    public const double SpacingMillimetres = 1.0;

    /// <summary>Наибольшее значение яркости после приведения.</summary>
    public const double MaxIntensity = 255.0;

    /// <summary>
    /// Доля ненулевых отсчётов, ниже которой берётся верх шкалы.
    ///
    /// Масштабирование устойчивое, а не по размаху: одиночный яркий выброс —
    /// артефакт, блик жира, металл — иначе сжал бы всю анатомию в нижние
    /// единицы шкалы.
    /// </summary>
    public const double HighCropFraction = 0.999;

    /// <summary>
    /// Приводит объём к виду, который принимает модель.
    /// </summary>
    /// <param name="volume">Объём серии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Отсчёты куба, ось 0 быстрее оси 1, ось 1 быстрее оси 2.</returns>
    public static byte[] Conform(IVoxelVolume volume, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var source = SourceToPatient(volume);
        var inverse = Invert(source);
        var target = TargetToPatient(source, volume.Grid.Dimensions);
        var sampled = Resample(volume, inverse, target, cancellationToken);
        var (offset, scale) = RobustScale(volume, cancellationToken);
        var voxels = new byte[sampled.Length];

        for (var index = 0; index < sampled.Length; index++)
        {
            // Нули остаются нулями: за пределами снимка нет ни ткани, ни фона,
            // и сдвиг шкалы превратил бы пустоту в слабый сигнал.
            if (sampled[index] == 0f)
            {
                continue;
            }

            var value = Math.Round((sampled[index] - offset) * scale);

            voxels[index] = (byte)Math.Clamp(value, 0, MaxIntensity);
        }

        return voxels;
    }

    /// <summary>
    /// Переносит разметку приведённого куба обратно в сетку серии.
    ///
    /// Нужно потому, что меряет приложение в геометрии снимка, а не приведённой:
    /// отрезки обязаны лечь на те же отсчёты, которые видит врач.
    ///
    /// Выборка ближайшим соседом, а не линейная: метки не усредняются — половина
    /// желудочка не желудочек.
    /// </summary>
    /// <param name="conformed">Разметка куба: 1 — желудочки, 0 — остальное.</param>
    /// <param name="volume">Объём серии, в сетку которого переносится разметка.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Разметка в сетке серии.</returns>
    public static byte[] ProjectBack(
        byte[] conformed,
        IVoxelVolume volume,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conformed);
        ArgumentNullException.ThrowIfNull(volume);

        var dimensions = volume.Grid.Dimensions;
        var source = SourceToPatient(volume);
        var target = TargetToPatient(source, dimensions);
        var inverseTarget = Invert(target);
        var labels = new byte[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var patient = Apply(source, column, row, slice);
                    var cube = Apply(inverseTarget, patient.X, patient.Y, patient.Z);

                    var i = (int)Math.Round(cube.X);
                    var j = (int)Math.Round(cube.Y);
                    var k = (int)Math.Round(cube.Z);

                    if (i < 0 || j < 0 || k < 0 || i >= Size || j >= Size || k >= Size)
                    {
                        continue;
                    }

                    labels[(((slice * dimensions.Rows) + row) * dimensions.Columns) + column] =
                        conformed[(((k * Size) + j) * Size) + i];
                }
            }
        }

        return labels;
    }

    /// <summary>
    /// Переход от отсчётов серии к координатам пациента, в системе RAS.
    ///
    /// DICOM задаёт направления в LPS, а обучающий контур работает в RAS;
    /// переход между ними — смена знака у первых двух осей.
    /// </summary>
    private static double[,] SourceToPatient(IVoxelVolume volume)
    {
        var geometry = volume.Geometry;
        var grid = volume.Grid;
        var normal = Cross(geometry.RowDirection, geometry.ColumnDirection);
        var matrix = new double[4, 4];

        SetColumn(matrix, 0, geometry.RowDirection, grid.ColumnSpacingMillimetres);
        SetColumn(matrix, 1, geometry.ColumnDirection, grid.RowSpacingMillimetres);
        SetColumn(matrix, 2, normal, grid.SliceSpacingMillimetres);

        matrix[0, 3] = geometry.Origin.X;
        matrix[1, 3] = geometry.Origin.Y;
        matrix[2, 3] = geometry.Origin.Z;
        matrix[3, 3] = 1;

        for (var column = 0; column < 4; column++)
        {
            matrix[0, column] = -matrix[0, column];
            matrix[1, column] = -matrix[1, column];
        }

        return matrix;
    }

    /// <summary>
    /// Переход от отсчётов куба к координатам пациента.
    ///
    /// Оси куба направлены влево, вниз и вперёд — это и есть укладка LIA. Шаг
    /// миллиметровый, а начало выбрано так, чтобы середина куба легла на
    /// середину исходного объёма: иначе голова уедет за край.
    /// </summary>
    private static double[,] TargetToPatient(double[,] source, VolumeDimensions dimensions)
    {
        var matrix = new double[4, 4];

        // Влево — минус по оси «вправо», вниз — минус по «вверх», вперёд — плюс.
        matrix[0, 0] = -SpacingMillimetres;
        matrix[2, 1] = -SpacingMillimetres;
        matrix[1, 2] = SpacingMillimetres;
        matrix[3, 3] = 1;

        var centre = Apply(
            source,
            dimensions.Columns / 2.0,
            dimensions.Rows / 2.0,
            dimensions.Slices / 2.0);

        var half = Size / 2.0;

        Span<double> centres = [centre.X, centre.Y, centre.Z];

        for (var row = 0; row < 3; row++)
        {
            matrix[row, 3] = centres[row]
                - ((matrix[row, 0] * half) + (matrix[row, 1] * half) + (matrix[row, 2] * half));
        }

        return matrix;
    }

    /// <summary>Трилинейная выборка исходного объёма по сетке куба.</summary>
    private static float[] Resample(
        IVoxelVolume volume,
        double[,] patientToSource,
        double[,] targetToPatient,
        CancellationToken cancellationToken)
    {
        var dimensions = volume.Grid.Dimensions;
        var sampled = new float[Size * Size * Size];

        for (var k = 0; k < Size; k++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var j = 0; j < Size; j++)
            {
                for (var i = 0; i < Size; i++)
                {
                    var patient = Apply(targetToPatient, i, j, k);
                    var source = Apply(patientToSource, patient.X, patient.Y, patient.Z);

                    sampled[(((k * Size) + j) * Size) + i] =
                        Sample(volume, dimensions, source.X, source.Y, source.Z);
                }
            }
        }

        return sampled;
    }

    private static float Sample(IVoxelVolume volume, VolumeDimensions dimensions, double x, double y, double z)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var z0 = (int)Math.Floor(z);

        if (x0 < 0 || y0 < 0 || z0 < 0
            || x0 + 1 >= dimensions.Columns
            || y0 + 1 >= dimensions.Rows
            || z0 + 1 >= dimensions.Slices)
        {
            return 0f;
        }

        double fx = x - x0, fy = y - y0, fz = z - z0;
        double result = 0;

        for (var dz = 0; dz <= 1; dz++)
        {
            for (var dy = 0; dy <= 1; dy++)
            {
                for (var dx = 0; dx <= 1; dx++)
                {
                    var weight = (dx == 0 ? 1 - fx : fx)
                        * (dy == 0 ? 1 - fy : fy)
                        * (dz == 0 ? 1 - fz : fz);

                    result += weight * volume[x0 + dx, y0 + dy, z0 + dz];
                }
            }
        }

        return (float)result;
    }

    /// <summary>
    /// Сдвиг и множитель шкалы яркости, по гистограмме исходных отсчётов.
    ///
    /// Верх шкалы берётся не по наибольшему значению, а по доле
    /// <see cref="HighCropFraction"/> ненулевых отсчётов.
    /// </summary>
    private static (double Offset, double Scale) RobustScale(
        IVoxelVolume volume,
        CancellationToken cancellationToken)
    {
        const int Bins = 1000;

        var dimensions = volume.Grid.Dimensions;
        double minimum = volume.Minimum, maximum = volume.Maximum;

        if (maximum <= minimum)
        {
            return (minimum, 1.0);
        }

        var histogram = new long[Bins];
        var width = (maximum - minimum) / Bins;
        long nonZero = 0;
        var total = (long)dimensions.Columns * dimensions.Rows * dimensions.Slices;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var value = volume[column, row, slice];

                    if (Math.Abs(value) >= 1e-15)
                    {
                        nonZero++;
                    }

                    histogram[(int)Math.Clamp((value - minimum) / width, 0, Bins - 1)]++;
                }
            }
        }

        var upperCutoff = total - (long)((1.0 - HighCropFraction) * nonZero);
        long cumulative = 0;
        var upperIndex = Bins - 1;

        for (var bin = 0; bin < Bins; bin++)
        {
            cumulative += histogram[bin];

            // Индекс берётся на две корзины назад от первой, где накопленное
            // достигло порога, — так же, как в исходном коде FastSurfer.
            if (cumulative >= upperCutoff)
            {
                upperIndex = bin - 1;
                break;
            }
        }

        var sourceMaximum = minimum + (Math.Max(upperIndex, 0) * width);

        return sourceMaximum <= minimum
            ? (minimum, 1.0)
            : (minimum, MaxIntensity / (sourceMaximum - minimum));
    }

    private static SpatialVector Cross(SpatialVector a, SpatialVector b) => new(
        (a.Y * b.Z) - (a.Z * b.Y),
        (a.Z * b.X) - (a.X * b.Z),
        (a.X * b.Y) - (a.Y * b.X));

    private static void SetColumn(double[,] matrix, int column, SpatialVector direction, double spacing)
    {
        var length = Math.Sqrt(
            (direction.X * direction.X) + (direction.Y * direction.Y) + (direction.Z * direction.Z));
        var unit = length > 0 ? spacing / length : 0.0;

        matrix[0, column] = direction.X * unit;
        matrix[1, column] = direction.Y * unit;
        matrix[2, column] = direction.Z * unit;
    }

    /// <summary>
    /// Применяет однородную матрицу к точке.
    ///
    /// Возвращает кортеж, а не массив: вызывается на каждый отсчёт, а отсчётов
    /// в кубе шестнадцать миллионов. Массив означал бы столько же выделений в
    /// куче, и приведение занимало минуты вместо секунд.
    /// </summary>
    private static (double X, double Y, double Z) Apply(
        double[,] matrix, double i, double j, double k) =>
    (
        (matrix[0, 0] * i) + (matrix[0, 1] * j) + (matrix[0, 2] * k) + matrix[0, 3],
        (matrix[1, 0] * i) + (matrix[1, 1] * j) + (matrix[1, 2] * k) + matrix[1, 3],
        (matrix[2, 0] * i) + (matrix[2, 1] * j) + (matrix[2, 2] * k) + matrix[2, 3]
    );

    /// <summary>Обращение однородной матрицы 4×4 методом Гаусса.</summary>
    private static double[,] Invert(double[,] matrix)
    {
        var working = new double[4, 8];

        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                working[row, column] = matrix[row, column];
            }

            working[row, 4 + row] = 1;
        }

        for (var pivot = 0; pivot < 4; pivot++)
        {
            var best = pivot;

            for (var row = pivot + 1; row < 4; row++)
            {
                if (Math.Abs(working[row, pivot]) > Math.Abs(working[best, pivot]))
                {
                    best = row;
                }
            }

            if (Math.Abs(working[best, pivot]) < 1e-12)
            {
                throw new DomainRuleViolationException(
                    "The series geometry is degenerate: its axes do not span space.");
            }

            if (best != pivot)
            {
                for (var column = 0; column < 8; column++)
                {
                    (working[pivot, column], working[best, column]) =
                        (working[best, column], working[pivot, column]);
                }
            }

            var divisor = working[pivot, pivot];

            for (var column = 0; column < 8; column++)
            {
                working[pivot, column] /= divisor;
            }

            for (var row = 0; row < 4; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = working[row, pivot];

                for (var column = 0; column < 8; column++)
                {
                    working[row, column] -= factor * working[pivot, column];
                }
            }
        }

        var result = new double[4, 4];

        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                result[row, column] = working[row, 4 + column];
            }
        }

        return result;
    }
}
