using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Отрезки автоматического индекса Эванса поверх разбора метода.
///
/// Число индекса врач проверяет не по числу, а по тому, где стоят его концы:
/// рога, взятые на теле желудочка, и череп, отмеренный по коже, дают
/// правдоподобное отношение. Поэтому отрезки рисуются в той же маске разбора,
/// что показывает выбранный и отброшенный ликвор, и на том же аксиальном
/// срезе, где они измерены.
/// </summary>
public static class EvansOverlay
{
    /// <summary>Метка ширины передних рогов в маске разбора.</summary>
    public const byte FrontalHornsLabel = 3;

    /// <summary>Метка внутреннего диаметра черепа в маске разбора.</summary>
    public const byte InnerSkullLabel = 4;

    /// <summary>Отрезок ширины передних рогов.</summary>
    public static readonly AnatomicalLabel FrontalHorns = new("evans-frontal-horns");

    /// <summary>Отрезок внутреннего диаметра черепа.</summary>
    public static readonly AnatomicalLabel InnerSkull = new("evans-inner-skull");

    /// <summary>
    /// Возвращает маску разбора с нарисованными отрезками.
    /// </summary>
    /// <param name="review">Маска разбора метода.</param>
    /// <param name="segments">Отрезки индекса.</param>
    /// <returns>Новая маска; исходная не меняется.</returns>
    public static VoxelMask Draw(VoxelMask review, EvansSegments segments)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(segments);

        var dimensions = review.Grid.Dimensions;
        var labels = new byte[(long)dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    labels[Offset(dimensions, column, row, slice)] = review[column, row, slice];
                }
            }
        }

        // Череп рисуется первым: там, где отрезки совпали бы, виден рог.
        Line(dimensions, labels, segments.InnerSkullFirst, segments.InnerSkullSecond, InnerSkullLabel);
        Line(dimensions, labels, segments.FrontalHornFirst, segments.FrontalHornSecond, FrontalHornsLabel);

        var structures = new List<AnatomicalLabel>(review.Map.Labels);

        while (structures.Count < InnerSkullLabel)
        {
            // Разбор отказавшей сегментации может не нести метки отброшенного
            // ликвора; номера отрезков от этого сдвигаться не должны.
            structures.Add(new AnatomicalLabel("unused-" + (structures.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        structures[FrontalHornsLabel - 1] = FrontalHorns;
        structures[InnerSkullLabel - 1] = InnerSkull;

        return new VoxelMask(
            review.Grid,
            new LabelMap { Version = review.Map.Version, Labels = structures },
            labels);
    }

    private static void Line(
        Domain.Imaging.VolumeDimensions dimensions,
        byte[] labels,
        VoxelPosition first,
        VoxelPosition second,
        byte label)
    {
        var steps = (int)Math.Ceiling(2 * Math.Max(
            Math.Abs(second.Column - first.Column),
            Math.Max(Math.Abs(second.Row - first.Row), Math.Abs(second.Slice - first.Slice))));

        for (var step = 0; step <= steps; step++)
        {
            var fraction = steps == 0 ? 0 : (double)step / steps;

            // Концы отрезков стоят на границах вокселей; рисуется воксель,
            // внутри которого лежит точка, без выхода за кадр.
            var column = Clamp(first.Column + ((second.Column - first.Column) * fraction), dimensions.Columns);
            var row = Clamp(first.Row + ((second.Row - first.Row) * fraction), dimensions.Rows);
            var slice = Clamp(first.Slice + ((second.Slice - first.Slice) * fraction), dimensions.Slices);

            labels[Offset(dimensions, column, row, slice)] = label;
        }
    }

    private static int Clamp(double coordinate, int count) =>
        Math.Clamp((int)Math.Round(coordinate), 0, count - 1);

    private static long Offset(Domain.Imaging.VolumeDimensions dimensions, int column, int row, int slice) =>
        (((long)slice * dimensions.Rows) + row) * dimensions.Columns + column;
}
