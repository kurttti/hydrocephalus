using Hydrocephalus.Domain.Imaging;
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

    /// <summary>Метка отрезка рогов, отмеренного врачом.</summary>
    public const byte MarkedHornsLabel = 5;

    /// <summary>Метка внутреннего диаметра черепа, отмеренного врачом.</summary>
    public const byte MarkedSkullLabel = 6;

    /// <summary>Метка самих отмеченных точек.</summary>
    public const byte MarkedPointLabel = 7;

    /// <summary>Отрезок рогов, отмеренный врачом.</summary>
    public static readonly AnatomicalLabel MarkedHorns = new("evans-marked-frontal-horns");

    /// <summary>Внутренний диаметр черепа, отмеренный врачом.</summary>
    public static readonly AnatomicalLabel MarkedSkull = new("evans-marked-inner-skull");

    /// <summary>Отмеченная точка.</summary>
    public static readonly AnatomicalLabel MarkedPoint = new("evans-marked-point");

    /// <summary>
    /// Возвращает слой с разметкой врача: сами нажатые точки и, когда их
    /// четыре, два полученных отрезка.
    ///
    /// Нужен затем, что без него врач не видит, куда поставил точки, — а это
    /// единственный способ проверить постановку. Число индекса такой проверки
    /// не даёт: 0,33 выходит и из верно отмеченных рогов, и из точек, съехавших
    /// на борозду, и из перепутанного порядка нажатий.
    ///
    /// Точки рисуются отдельной меткой от отрезков **намеренно**. Отрезок рогов
    /// не проходит через нажатые точки: обе величины определения поперечные, и
    /// отрезок рогов проецируется на ось, заданную точками черепа
    /// (см. `ManualEvansIndex`). Видя и нажатия, и проекцию, врач видит
    /// и саму поправку на наклон, а не только её результат.
    ///
    /// Слой отдельный, а не нарисованный в маске разбора: разбора может не быть
    /// вовсе — на толстосрезовых сериях сегментация отказывает, и ручное
    /// измерение для них и сделано.
    /// </summary>
    /// <param name="grid">Сетка объёма.</param>
    /// <param name="points">Нажатые точки в порядке постановки.</param>
    /// <param name="segments">Полученные отрезки либо <see langword="null"/>.</param>
    /// <returns>Слой разметки; вне линий и точек он пуст.</returns>
    public static VoxelMask Marks(
        VolumeGrid grid,
        IReadOnlyList<VoxelPosition> points,
        EvansSegments? segments)
    {
        ArgumentNullException.ThrowIfNull(points);

        var dimensions = grid.Dimensions;
        var labels = new byte[(long)dimensions.Columns * dimensions.Rows * dimensions.Slices];

        if (segments is { } measured)
        {
            Line(dimensions, labels, measured.InnerSkullFirst, measured.InnerSkullSecond, MarkedSkullLabel);
            Line(dimensions, labels, measured.FrontalHornFirst, measured.FrontalHornSecond, MarkedHornsLabel);
        }

        // Точки рисуются последними: нажатие важнее проведённой через него
        // линии, и на пересечении должно остаться видно именно его.
        foreach (var point in points)
        {
            Dot(dimensions, labels, point);
        }

        return new VoxelMask(
            grid,
            new LabelMap
            {
                Version = "evans-marks-1.0.0",
                Labels =
                [
                    new AnatomicalLabel("unused-1"),
                    new AnatomicalLabel("unused-2"),
                    FrontalHorns,
                    InnerSkull,
                    MarkedHorns,
                    MarkedSkull,
                    MarkedPoint,
                ],
            },
            labels);
    }

    /// <summary>
    /// Рисует крест в три отсчёта: одиночный воксель на снимке в полный экран
    /// теряется, а крест виден и при этом не закрывает то, по чему мерили.
    /// </summary>
    private static void Dot(Domain.Imaging.VolumeDimensions dimensions, byte[] labels, VoxelPosition point)
    {
        (int Column, int Row)[] cross =
        [
            (0, 0),
            (-1, 0),
            (1, 0),
            (0, -1),
            (0, 1),
        ];

        // Точка приходит в непрерывных координатах сетки: рисуется воксель,
        // внутри которого она лежит, как и у концов отрезков.
        var centreColumn = (int)Math.Round(point.Column);
        var centreRow = (int)Math.Round(point.Row);
        var centreSlice = (int)Math.Round(point.Slice);

        foreach (var (column, row) in cross)
        {
            var at = (Column: centreColumn + column, Row: centreRow + row, Slice: centreSlice);

            if (at.Column < 0 || at.Column >= dimensions.Columns
                || at.Row < 0 || at.Row >= dimensions.Rows
                || at.Slice < 0 || at.Slice >= dimensions.Slices)
            {
                continue;
            }

            labels[Offset(dimensions, at.Column, at.Row, at.Slice)] = MarkedPointLabel;
        }
    }

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
