using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference.Measurements;

/// <summary>
/// Причина, по которой индекс Эванса не выведен из маски.
/// </summary>
public enum AutomaticEvansRefusal
{
    /// <summary>Сегментация отказала: выводить точки не из чего.</summary>
    SegmentationRefused = 1,

    /// <summary>Взвешенность, на которой метод не определён (яркий ликвор T2).</summary>
    WeightingNotSupported = 2,

    /// <summary>Оси сетки не совпадают с осями пациента настолько, чтобы найти аксиальную плоскость.</summary>
    AxesNotAligned = 3,

    /// <summary>В маске нет передних рогов обоих желудочков по обе стороны от средней линии.</summary>
    FrontalHornsNotFound = 4,

    /// <summary>На плоскости рогов не нашлась граница мозга и черепа.</summary>
    InnerSkullNotFound = 5,
}

/// <summary>
/// Отрезки, по которым посчитан индекс: где их показать врачу.
/// </summary>
/// <param name="AxialAcross">Ось объёма, поперёк которой лежит плоскость измерения.</param>
/// <param name="PlaneIndex">Номер плоскости вдоль этой оси.</param>
/// <param name="FrontalHornFirst">Первый конец ширины передних рогов.</param>
/// <param name="FrontalHornSecond">Второй конец ширины передних рогов.</param>
/// <param name="InnerSkullFirst">Первый конец внутреннего диаметра черепа.</param>
/// <param name="InnerSkullSecond">Второй конец внутреннего диаметра черепа.</param>
public sealed record EvansSegments(
    VolumeAxis AxialAcross,
    int PlaneIndex,
    VoxelPosition FrontalHornFirst,
    VoxelPosition FrontalHornSecond,
    VoxelPosition InnerSkullFirst,
    VoxelPosition InnerSkullSecond);

/// <summary>
/// Индекс Эванса, выведенный из маски, либо причина отказа.
/// </summary>
public sealed record AutomaticEvansResult
{
    /// <summary>Признак; <see langword="null"/> при отказе.</summary>
    public Biomarker? Biomarker { get; init; }

    /// <summary>Отрезки измерения; <see langword="null"/> при отказе.</summary>
    public EvansSegments? Segments { get; init; }

    /// <summary>Причина отказа; <see langword="null"/>, если индекс посчитан.</summary>
    public AutomaticEvansRefusal? Refusal { get; init; }
}

/// <summary>
/// Индекс Эванса без участия врача: точки выводятся из маски желудочков
/// объёмной серии.
///
/// Как ставятся точки:
///
/// - **передние рога.** Передней частью желудочков считается передняя треть
///   протяжённости маски спереди назад. На каждой аксиальной плоскости берутся
///   только связные области маски, заходящие к средней линии головы, — так
///   височные рога и отдельные фрагменты в ширину не попадают. Ширина —
///   от самой левой до самой правой точки этих областей на одной линии
///   слева направо; наибольшая по всем плоскостям и линиям. У рогов в форме
///   буквы X это расстояние между их латеральными углами, как и при ручном
///   измерении;
/// - **оба желудочка обязательны.** Ширина засчитывается, только если маска
///   заходит за среднюю линию на обе стороны. Сегментация иногда берёт один
///   боковой желудочек — на IXI так было дважды на сорок серий, — и ширина
///   одного рога дала бы индекс 0.07–0.10, правдоподобный на вид;
/// - **внутренний диаметр черепа** — на той же плоскости. Мозг — связная
///   область ярче порога фона вокруг рогов; к его ширине добавляется половина
///   тёмной полосы снаружи с каждой стороны. Ликвор над корой и кость на T1
///   с шагом около миллиметра неразличимы по яркости, внутренняя пластинка
///   лежит где-то внутри этой полосы, и середина полосы ошибается не больше
///   чем на её половину — один-два миллиметра на сторону при диаметре
///   130–140 мм.
///
/// Плоскость ищется поперёк оси сетки, ближайшей к направлению вверх-вниз,
/// а не на пересэмплированном объёме: сагиттальная 3D T1 содержит аксиальные
/// плоскости с тем же шагом, что и аксиальная. Наклон осей до 37° допускается —
/// на аксиальной серии, выставленной по линии AC-PC, врач меряет так же.
///
/// Метод проверен визуально на IXI T1 (docs/data/README.md) и не валидирован
/// против ручной разметки, поэтому признак всегда помечается как
/// сомнительный.
/// </summary>
public static class AutomaticEvansIndex
{
    /// <summary>Доля протяжённости маски спереди назад, отнесённая к передним рогам.</summary>
    public const double FrontalFraction = 0.35;

    /// <summary>Насколько близко к средней линии должна подходить область маски, мм.</summary>
    public const double MidlineReachMillimetres = 10;

    /// <summary>На сколько маска должна заходить за среднюю линию с каждой стороны, мм.</summary>
    public const double MinimumSideMillimetres = 3;

    /// <summary>Эрозия, отделяющая мозг от кожи через узкие перемычки, мм.</summary>
    public const double BrainSeparationMillimetres = 2;

    /// <summary>Наибольшая толщина тёмной полосы ликвора и кости, мм; шире — край кадра или воздух.</summary>
    public const double MaximumDarkBandMillimetres = 12;

    /// <summary>
    /// Наименьшая доля направления оси сетки вдоль оси пациента: косинус 37°.
    /// </summary>
    public const double MinimumAxisAlignment = 0.8;

    /// <summary>
    /// Выводит индекс Эванса из маски желудочков.
    /// </summary>
    /// <param name="volume">Объём, на котором построена маска.</param>
    /// <param name="segmentation">Результат сегментации.</param>
    /// <param name="weighting">Взвешенность серии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Индекс с отрезками либо причина отказа.</returns>
    public static AutomaticEvansResult Measure(
        IVoxelVolume volume,
        BaselineSegmentationResult segmentation,
        SeriesWeighting weighting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        if (weighting is not (SeriesWeighting.T1 or SeriesWeighting.Flair))
        {
            // Полоса «ликвор и кость» тёмная только при тёмном ликворе.
            return Refused(AutomaticEvansRefusal.WeightingNotSupported);
        }

        if (segmentation.Quality == MeasurementQuality.Unreliable)
        {
            return Refused(AutomaticEvansRefusal.SegmentationRefused);
        }

        if (AxesOf(volume.Geometry) is not { } axes)
        {
            return Refused(AutomaticEvansRefusal.AxesNotAligned);
        }

        var space = new PlaneSpace(volume, segmentation.Mask, axes);
        var threshold = IntensityThresholds.Otsu(volume);

        if (FrontalHorns(space, threshold, cancellationToken) is not { } horns)
        {
            return Refused(AutomaticEvansRefusal.FrontalHornsNotFound);
        }

        if (InnerSkull(space, threshold, horns) is not { } skull)
        {
            return Refused(AutomaticEvansRefusal.InnerSkullNotFound);
        }

        var segments = new EvansSegments(
            axes.AxialAcross,
            horns.Plane,
            space.Position(horns.Start - 0.5, horns.Line, horns.Plane),
            space.Position(horns.End + 0.5, horns.Line, horns.Plane),
            space.Position(skull.Start - 0.5, skull.Line, horns.Plane),
            space.Position(skull.End + 0.5, skull.Line, horns.Plane));

        return new AutomaticEvansResult
        {
            Biomarker = LinearBiomarkers.EvansIndex(
                volume,
                segments.AxialAcross,
                segments.FrontalHornFirst,
                segments.FrontalHornSecond,
                segments.InnerSkullFirst,
                segments.InnerSkullSecond,

                // Метод не валидирован против ручной разметки.
                MeasurementQuality.Questionable),
            Segments = segments,
        };
    }

    private static AutomaticEvansResult Refused(AutomaticEvansRefusal reason) => new() { Refusal = reason };

    private static Axes? AxesOf(SeriesGeometry geometry)
    {
        // Индексы осей сетки: 0 — столбцы, 1 — строки, 2 — срезы.
        SpatialVector[] directions =
        [
            geometry.RowDirection.Normalized(),
            geometry.ColumnDirection.Normalized(),
            geometry.SliceNormal,
        ];

        var leftRight = Dominant(directions, vector => vector.X);
        var anteriorPosterior = Dominant(directions, vector => vector.Y);
        var superiorInferior = Dominant(directions, vector => vector.Z);

        if (leftRight is not { } lr || anteriorPosterior is not { } ap || superiorInferior is not { } si
            || lr == ap || ap == si || lr == si)
        {
            return null;
        }

        return new Axes(
            lr,
            ap,
            si,

            // В LPS +Y ведёт назад: если номер растёт назад, передние линии — младшие.
            AnteriorIsLow: directions[ap].Y > 0);
    }

    private static int? Dominant(SpatialVector[] directions, Func<SpatialVector, double> component)
    {
        var best = 0;

        for (var axis = 1; axis < directions.Length; axis++)
        {
            if (Math.Abs(component(directions[axis])) > Math.Abs(component(directions[best])))
            {
                best = axis;
            }
        }

        return Math.Abs(component(directions[best])) >= MinimumAxisAlignment ? best : null;
    }

    private static Horns? FrontalHorns(PlaneSpace space, double threshold, CancellationToken cancellationToken)
    {
        var (anteriorMost, span) = space.MaskExtentFrontToBack();

        if (anteriorMost < 0)
        {
            return null;
        }

        var frontalLines = (int)(span * FrontalFraction);
        var reach = MidlineReachMillimetres / space.WidthSpacing;
        var side = MinimumSideMillimetres / space.WidthSpacing;

        Horns? best = null;

        for (var plane = 0; plane < space.Planes; plane++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mask = space.MaskPlane(plane);

            if (mask is null || space.HeadMidline(plane, threshold) is not { } midline)
            {
                continue;
            }

            var central = CentralRegions(mask, space.Width, space.Lines, midline, reach);

            for (var step = 0; step <= frontalLines; step++)
            {
                var line = space.LineFromFront(anteriorMost, step);
                var (start, end) = Extent(central, space.Width, line);

                if (start < 0 || start > midline - side || end < midline + side)
                {
                    continue;
                }

                if (best is null || end - start > best.End - best.Start)
                {
                    best = new Horns(plane, line, start, end);
                }
            }
        }

        return best;
    }

    private static bool[] CentralRegions(bool[] mask, int width, int lines, double midline, double reach)
    {
        var central = new bool[mask.Length];
        var visited = new bool[mask.Length];

        for (var offset = 0; offset < mask.Length; offset++)
        {
            if (!mask[offset] || visited[offset])
            {
                continue;
            }

            // Восьмисвязность: тонкие рога в форме X соединены с телом
            // желудочка только по диагонали, и по четырём соседям их
            // латеральные углы отпадали бы от центральной области.
            var region = Flood(mask, visited, width, lines, offset, diagonal: true);

            if (region.Exists(point => Math.Abs((point % width) - midline) <= reach))
            {
                foreach (var point in region)
                {
                    central[point] = true;
                }
            }
        }

        return central;
    }

    private static Skull? InnerSkull(PlaneSpace space, double threshold, Horns horns)
    {
        var width = space.Width;
        var lines = space.Lines;
        var tissue = new bool[width * lines];

        for (var line = 0; line < lines; line++)
        {
            for (var across = 0; across < width; across++)
            {
                // Желудочки темнее порога, но принадлежат мозгу, а не фону.
                tissue[(line * width) + across] = space.Value(across, line, horns.Plane) > threshold
                    || space.IsMask(across, line, horns.Plane);
            }
        }

        // Кожа касается мозга через сосуды и костный мозг кости; эрозия рвёт
        // эти перемычки, область мозга выбирается от середины рогов,
        // и расширение возвращает ей снятый край — только в пределах ткани.
        var steps = Math.Max(1, (int)Math.Round(BrainSeparationMillimetres / space.WidthSpacing));
        var core = tissue;

        for (var step = 0; step < steps; step++)
        {
            core = Erode(core, width, lines);
        }

        var brain = RegionNearest(core, width, lines, (horns.Line * width) + ((horns.Start + horns.End) / 2));

        if (brain is null)
        {
            return null;
        }

        for (var step = 0; step < steps; step++)
        {
            brain = Dilate(brain, width, lines, tissue);
        }

        var maximumBand = (int)Math.Ceiling(MaximumDarkBandMillimetres / space.WidthSpacing);
        Skull? best = null;
        var bestWidth = 0.0;

        for (var line = 0; line < lines; line++)
        {
            var (start, end) = Extent(brain, width, line);

            if (start < 0)
            {
                continue;
            }

            var left = DarkBand(space, threshold, horns.Plane, line, start, -1, maximumBand);
            var right = DarkBand(space, threshold, horns.Plane, line, end, +1, maximumBand);

            if (left is not { } leftBand || right is not { } rightBand)
            {
                continue;
            }

            var inner = (end - start + 1) + ((leftBand + rightBand) / 2.0);

            if (inner > bestWidth)
            {
                bestWidth = inner;
                best = new Skull(line, start - (leftBand / 2.0), end + (rightBand / 2.0));
            }
        }

        return best;
    }

    /// <summary>
    /// Толщина тёмной полосы снаружи от края мозга. Полоса без светлой ткани
    /// за ней — воздух или край кадра, а не череп.
    /// </summary>
    private static int? DarkBand(PlaneSpace space, double threshold, int plane, int line, int edge, int direction, int maximum)
    {
        for (var band = 0; band < maximum; band++)
        {
            var across = edge + (direction * (band + 1));

            if (across < 0 || across >= space.Width)
            {
                return null;
            }

            if (space.Value(across, line, plane) > threshold)
            {
                return band;
            }
        }

        return null;
    }

    private static (int Start, int End) Extent(bool[] plane, int width, int line)
    {
        var start = -1;
        var end = -1;

        for (var across = 0; across < width; across++)
        {
            if (plane[(line * width) + across])
            {
                start = start < 0 ? across : start;
                end = across;
            }
        }

        return (start, end);
    }

    private static List<int> Flood(bool[] plane, bool[] visited, int width, int lines, int seed, bool diagonal = false)
    {
        var region = new List<int>();
        var pending = new Stack<int>();

        visited[seed] = true;
        pending.Push(seed);

        while (pending.TryPop(out var offset))
        {
            region.Add(offset);

            var across = offset % width;
            var line = offset / width;

            Visit(across - 1, line);
            Visit(across + 1, line);
            Visit(across, line - 1);
            Visit(across, line + 1);

            if (diagonal)
            {
                Visit(across - 1, line - 1);
                Visit(across + 1, line - 1);
                Visit(across - 1, line + 1);
                Visit(across + 1, line + 1);
            }
        }

        return region;

        void Visit(int across, int line)
        {
            if (across < 0 || line < 0 || across >= width || line >= lines)
            {
                return;
            }

            var offset = (line * width) + across;

            if (plane[offset] && !visited[offset])
            {
                visited[offset] = true;
                pending.Push(offset);
            }
        }
    }

    private static bool[]? RegionNearest(bool[] plane, int width, int lines, int seed)
    {
        if (!plane[seed])
        {
            // Середина рогов после эрозии могла оказаться вне ткани.
            var nearest = -1;
            var nearestDistance = int.MaxValue;

            for (var offset = 0; offset < plane.Length; offset++)
            {
                if (!plane[offset])
                {
                    continue;
                }

                var distance = Math.Abs((offset % width) - (seed % width)) + Math.Abs((offset / width) - (seed / width));

                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = offset;
                }
            }

            if (nearest < 0)
            {
                return null;
            }

            seed = nearest;
        }

        var region = new bool[plane.Length];

        foreach (var offset in Flood(plane, new bool[plane.Length], width, lines, seed))
        {
            region[offset] = true;
        }

        return region;
    }

    private static bool[] Erode(bool[] source, int width, int lines)
    {
        var eroded = new bool[source.Length];

        for (var line = 1; line < lines - 1; line++)
        {
            for (var across = 1; across < width - 1; across++)
            {
                var offset = (line * width) + across;

                eroded[offset] = source[offset]
                    && source[offset - 1]
                    && source[offset + 1]
                    && source[offset - width]
                    && source[offset + width];
            }
        }

        return eroded;
    }

    private static bool[] Dilate(bool[] source, int width, int lines, bool[] within)
    {
        var dilated = (bool[])source.Clone();

        for (var line = 1; line < lines - 1; line++)
        {
            for (var across = 1; across < width - 1; across++)
            {
                var offset = (line * width) + across;

                if (!source[offset]
                    && within[offset]
                    && (source[offset - 1] || source[offset + 1] || source[offset - width] || source[offset + width]))
                {
                    dilated[offset] = true;
                }
            }
        }

        return dilated;
    }

    /// <summary>Оси сетки по направлениям пациента: 0 — столбцы, 1 — строки, 2 — срезы.</summary>
    private readonly record struct Axes(int LeftRight, int AnteriorPosterior, int SuperiorInferior, bool AnteriorIsLow)
    {
        public VolumeAxis AxialAcross => SuperiorInferior switch
        {
            0 => VolumeAxis.AcrossColumns,
            1 => VolumeAxis.AcrossRows,
            _ => VolumeAxis.AcrossSlices,
        };
    }

    private sealed record Horns(int Plane, int Line, int Start, int End);

    private sealed record Skull(int Line, double Start, double End);

    /// <summary>
    /// Объём, адресуемый аксиальными плоскостями: номер плоскости снизу вверх
    /// или сверху вниз, линия спереди назад, положение слева направо.
    /// </summary>
    private sealed class PlaneSpace
    {
        private readonly IVoxelVolume volume;
        private readonly VoxelMask mask;
        private readonly Axes axes;
        private readonly int[] sizes;

        public PlaneSpace(IVoxelVolume volume, VoxelMask mask, Axes axes)
        {
            this.volume = volume;
            this.mask = mask;
            this.axes = axes;

            var dimensions = volume.Grid.Dimensions;
            this.sizes = [dimensions.Columns, dimensions.Rows, dimensions.Slices];

            double[] spacings =
            [
                volume.Grid.ColumnSpacingMillimetres,
                volume.Grid.RowSpacingMillimetres,
                volume.Grid.SliceSpacingMillimetres,
            ];

            this.WidthSpacing = spacings[axes.LeftRight];
        }

        public int Width => this.sizes[this.axes.LeftRight];

        public int Lines => this.sizes[this.axes.AnteriorPosterior];

        public int Planes => this.sizes[this.axes.SuperiorInferior];

        public double WidthSpacing { get; }

        public float Value(int across, int line, int plane)
        {
            var (column, row, slice) = this.Locate(across, line, plane);

            return this.volume[column, row, slice];
        }

        public bool IsMask(int across, int line, int plane)
        {
            var (column, row, slice) = this.Locate(across, line, plane);

            return this.mask[column, row, slice] != 0;
        }

        public VoxelPosition Position(double across, int line, int plane)
        {
            double[] coordinates = [0, 0, 0];

            coordinates[this.axes.LeftRight] = across;
            coordinates[this.axes.AnteriorPosterior] = line;
            coordinates[this.axes.SuperiorInferior] = plane;

            return new VoxelPosition(coordinates[0], coordinates[1], coordinates[2]);
        }

        public int LineFromFront(int anteriorMost, int step) =>
            this.axes.AnteriorIsLow ? anteriorMost + step : anteriorMost - step;

        /// <summary>Самая передняя линия маски и протяжённость маски спереди назад; (-1, 0) у пустой маски.</summary>
        public (int AnteriorMost, int Span) MaskExtentFrontToBack()
        {
            var low = int.MaxValue;
            var high = -1;

            for (var plane = 0; plane < this.Planes; plane++)
            {
                for (var line = 0; line < this.Lines; line++)
                {
                    for (var across = 0; across < this.Width; across++)
                    {
                        if (this.IsMask(across, line, plane))
                        {
                            low = Math.Min(low, line);
                            high = Math.Max(high, line);
                        }
                    }
                }
            }

            if (high < 0)
            {
                return (-1, 0);
            }

            return (this.axes.AnteriorIsLow ? low : high, high - low);
        }

        public bool[]? MaskPlane(int plane)
        {
            var values = new bool[this.Width * this.Lines];
            var any = false;

            for (var line = 0; line < this.Lines; line++)
            {
                for (var across = 0; across < this.Width; across++)
                {
                    var inside = this.IsMask(across, line, plane);

                    values[(line * this.Width) + across] = inside;
                    any |= inside;
                }
            }

            return any ? values : null;
        }

        /// <summary>
        /// Средняя линия головы на плоскости: середина протяжённости ткани
        /// слева направо, усреднённая по линиям. Центр маски для этого не
        /// годится — у маски с одним желудочком он лежит в самом желудочке.
        /// </summary>
        public double? HeadMidline(int plane, double threshold)
        {
            var sum = 0.0;
            var count = 0;

            for (var line = 0; line < this.Lines; line++)
            {
                var first = 0;

                while (first < this.Width && this.Value(first, line, plane) <= threshold)
                {
                    first++;
                }

                if (first == this.Width)
                {
                    continue;
                }

                var last = this.Width - 1;

                while (this.Value(last, line, plane) <= threshold)
                {
                    last--;
                }

                sum += (first + last) / 2.0;
                count++;
            }

            return count == 0 ? null : sum / count;
        }

        private (int Column, int Row, int Slice) Locate(int across, int line, int plane)
        {
            Span<int> coordinates = stackalloc int[3];

            coordinates[this.axes.LeftRight] = across;
            coordinates[this.axes.AnteriorPosterior] = line;
            coordinates[this.axes.SuperiorInferior] = plane;

            return (coordinates[0], coordinates[1], coordinates[2]);
        }
    }
}
