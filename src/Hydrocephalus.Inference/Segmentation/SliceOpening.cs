using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Размыкание маски по толщине, в плоскости среза.
///
/// На срезе ликвор желудочков, борозд и подоболочечного пространства — одно
/// целое: межполушарная щель идёт от передних рогов вперёд тонкой полоской и
/// связывает их с кольцом вокруг коры. Отбор областей, подходящих к средней
/// линии, берёт тогда всю голову, и ширина рогов выходит равной ширине черепа —
/// на выборке так получались индексы 0,87–1,03 вместо 0,25–0,45.
///
/// Различает их толщина. Желудочки на уровне рогов — 10–20 мм поперёк, борозды
/// и щель — 1–3 мм. Размыкание (сжатие, затем расширение) тонкие связи
/// разрывает, толстое оставляет почти в прежних границах.
///
/// Размыкание годится, чтобы **выбрать область и линию**, но не чтобы мерить по
/// нему концы: сжатие срезает сужающиеся концы рогов, а это ровно те точки,
/// которые меряет индекс. Концы берутся продлением по неразомкнутой маске.
///
/// Сжатие и расширение считаются через расстояние, а не перебором окрестности:
/// при шаге 0,45 мм радиус 2 мм — это окрестность 9 на 9, восемьдесят операций
/// на отсчёт против двух проходов.
/// </summary>
public static class SliceOpening
{
    /// <summary>
    /// Размыкает метки маски в плоскостях, перпендикулярных оси.
    /// </summary>
    /// <param name="labels">Метки; изменяются на месте.</param>
    /// <param name="grid">Сетка объёма.</param>
    /// <param name="axis">Ось, поперёк которой лежат плоскости.</param>
    /// <param name="radiusMillimetres">Радиус размыкания.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    public static void Apply(
        byte[] labels,
        VolumeGrid grid,
        VolumeAxis axis,
        double radiusMillimetres,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var dimensions = grid.Dimensions;
        var extent = PlaneAddressing.ExtentOf(grid, axis);
        var planes = PlaneAddressing.CountAlong(grid, axis);
        var area = extent.Width * extent.Height;

        var inPlane = new bool[area];
        var eroded = new bool[area];
        var offsets = new int[area];

        for (var plane = 0; plane < planes; plane++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var y = 0; y < extent.Height; y++)
            {
                for (var x = 0; x < extent.Width; x++)
                {
                    var (column, row, slice) = PlaneAddressing.Locate(axis, plane, x, y);
                    var index = (y * extent.Width) + x;

                    offsets[index] = (((slice * dimensions.Rows) + row) * dimensions.Columns) + column;
                    inPlane[index] = labels[offsets[index]] != 0;
                }
            }

            // Сжатие: отсчёт остаётся, если он дальше радиуса от края маски.
            var inward = Distance(inPlane, extent, inside: true);

            for (var index = 0; index < area; index++)
            {
                eroded[index] = inPlane[index] && inward[index] >= radiusMillimetres;
            }

            // Расширение обратно: отсчёт возвращается, если он ближе радиуса к
            // тому, что пережило сжатие, и при этом был в маске.
            var outward = Distance(eroded, extent, inside: false);

            for (var index = 0; index < area; index++)
            {
                labels[offsets[index]] =
                    inPlane[index] && outward[index] <= radiusMillimetres ? (byte)1 : (byte)0;
            }
        }
    }

    /// <summary>
    /// Расстояние до ближайшего отсчёта вне множества (<c>inside</c>) либо до
    /// ближайшего отсчёта множества (<c>!inside</c>), в миллиметрах.
    ///
    /// Два прохода по Розенфельду, вперёд и назад, с весами шага сетки. Точность
    /// хуже евклидовой на единицы процентов, чего для порога толщины довольно.
    /// </summary>
    private static float[] Distance(bool[] set, PlaneExtent extent, bool inside)
    {
        var width = extent.Width;
        var height = extent.Height;
        var dx = (float)extent.PixelWidthMillimetres;
        var dy = (float)extent.PixelHeightMillimetres;
        var diagonal = MathF.Sqrt((dx * dx) + (dy * dy));
        var distance = new float[set.Length];

        const float Far = float.MaxValue / 4;

        for (var index = 0; index < set.Length; index++)
        {
            distance[index] = set[index] == inside ? Far : 0;
        }

        void Relax(int index, int neighbour, float weight)
        {
            var candidate = distance[neighbour] + weight;

            if (candidate < distance[index])
            {
                distance[index] = candidate;
            }
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;

                if (x > 0)
                {
                    Relax(index, index - 1, dx);
                }

                if (y > 0)
                {
                    Relax(index, index - width, dy);

                    if (x > 0)
                    {
                        Relax(index, index - width - 1, diagonal);
                    }

                    if (x < width - 1)
                    {
                        Relax(index, index - width + 1, diagonal);
                    }
                }
            }
        }

        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = width - 1; x >= 0; x--)
            {
                var index = (y * width) + x;

                if (x < width - 1)
                {
                    Relax(index, index + 1, dx);
                }

                if (y < height - 1)
                {
                    Relax(index, index + width, dy);

                    if (x < width - 1)
                    {
                        Relax(index, index + width + 1, diagonal);
                    }

                    if (x > 0)
                    {
                        Relax(index, index + width - 1, diagonal);
                    }
                }
            }
        }

        return distance;
    }
}
