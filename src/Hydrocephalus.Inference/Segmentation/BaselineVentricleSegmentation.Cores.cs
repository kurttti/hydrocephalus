using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Отбор желудочков по толстым ядрам ликвора.
///
/// Прежний отбор требовал, чтобы вся связная область ликвора лежала глубже
/// заданной глубины. На клинических MPRAGE пациентов с нормотензивной
/// гидроцефалией порог отделяет ликвор отлично, но расширенные желудочки
/// связаны с бороздами и цистернами через частичный объём, и вся система
/// оказывалась одной областью в сотни миллилитров, касающейся поверхности.
/// Правило отбрасывало её целиком и оставляло фрагменты в бороздах по 1–3 мл.
/// Разбор с просмотром масок записан в docs/data/README.md.
///
/// Здесь связь рвётся не глубиной, а толщиной. Желудочки при гидроцефалии —
/// самые толстые скопления ликвора в мозге; перемычки к бороздам и цистернам
/// тонкие. Порядок такой:
///
/// 1. голова закрывается шаром радиусом около сантиметра — прежняя голова
///    была дырявой: заливка фона затекала через тёмную полосу ликвора и кости
///    в борозды, и глубина считалась от этих дыр (на клинике не больше 30 мм);
/// 2. кандидаты — ликвор глубже поверхности головы; ядра — кандидаты, от
///    которых до ближайшего не-ликвора не меньше заданной толщины;
/// 3. ядро признаётся желудочковым, если оно глубоко целиком, по яркости
///    ликвор, а не воздух пазух и сосцевидных отростков (он темнее), и лежит
///    под сводом черепа, а не у основания: глотка, клиновидная пазуха и
///    базальные цистерны отстоят от свода больше чем на 80 мм. Свод должен
///    быть в кадре, а выбранное — лежать по обе стороны средней линии:
///    боковые желудочки парные;
/// 4. выбранным ядрам возвращается ликвор на расстоянии нескольких
///    миллиметров, связанный с ними, и край до полувысоты между ликвором
///    и окружающей тканью.
///
/// Если ни одного ядра нет — у здоровых узких желудочков на IXI так почти
/// всегда, — работает прежний отбор, и его результаты не меняются.
/// </summary>
public static partial class BaselineVentricleSegmentation
{
    private static CoreSelection? SelectByCores(
        IVoxelVolume volume,
        double threshold,
        BaselineSegmentationOptions options,
        CancellationToken cancellationToken)
    {
        var grid = volume.Grid;
        var dimensions = grid.Dimensions;
        var total = dimensions.Columns * dimensions.Rows * dimensions.Slices;
        var voxelMillilitres = grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres * grid.SliceSpacingMillimetres / 1000.0;

        var values = new float[total];
        var above = new bool[total];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var offset = Offset(dimensions, column, row, slice);

                    values[offset] = volume[column, row, slice];
                    above[offset] = values[offset] > threshold;
                }
            }
        }

        var head = SolidHead(grid, above, options.CoreHeadClosingMillimetres, cancellationToken);

        if (!Array.Exists(head, inside => inside))
        {
            return null;
        }

        var depth = DistanceToOutside(grid, head, cancellationToken);
        var candidate = new bool[total];

        for (var offset = 0; offset < total; offset++)
        {
            candidate[offset] = head[offset] && !above[offset] && depth[offset] >= options.MinDepthMillimetres;
        }

        var thickness = DistanceToOutside(grid, candidate, cancellationToken);
        var core = new bool[total];

        for (var offset = 0; offset < total; offset++)
        {
            core[offset] = candidate[offset] && thickness[offset] >= options.CoreThicknessMillimetres;
        }

        var superior = SuperiorAxis(volume.Geometry);
        var chosen = new bool[total];
        var visited = new bool[total];
        var stack = new Stack<int>();
        var rejected = 0;

        for (var start = 0; start < total; start++)
        {
            if (!core[start] || visited[start])
            {
                continue;
            }

            var component = Flood(dimensions, core, visited, stack, start);

            if (component.Count * voxelMillilitres < options.MinCoreMillilitres)
            {
                continue;
            }

            var shallowest = float.MaxValue;
            var brightness = 0.0;
            long column = 0, row = 0, slice = 0;

            foreach (var offset in component)
            {
                shallowest = Math.Min(shallowest, depth[offset]);
                brightness += values[offset];

                var (c, r, s) = Locate(dimensions, offset);
                column += c;
                row += r;
                slice += s;
            }

            // Свод ищется по ткани выше порога, а не по закрытой голове:
            // закрытие раздувает голову до края кадра, и после сжатия у края
            // остаётся «голова», которой на снимке нет.
            var headroom = Headroom(
                grid,
                above,
                superior,
                (int)(column / component.Count),
                (int)(row / component.Count),
                (int)(slice / component.Count));

            var isVentricle = shallowest >= options.CoreMinDepthMillimetres
                && brightness / component.Count >= threshold * options.CoreMinCsfToThreshold
                && headroom <= options.CoreMaxHeadroomMillimetres;

            if (!isVentricle)
            {
                rejected++;
                continue;
            }

            foreach (var offset in component)
            {
                chosen[offset] = true;
            }
        }

        if (!Array.Exists(chosen, inside => inside) || !IsBilateral(volume.Geometry, dimensions, head, chosen, options))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Ядру возвращается ликвор, снятый порогом толщины: связный с ядром
        // и не дальше заданного расстояния от него. Ограничение расстоянием,
        // а не шагами, не зависит от анизотропии сетки.
        var nearCore = DistanceToOutside(grid, Inverted(chosen), cancellationToken);
        var ventricle = Reconstruct(
            dimensions,
            chosen,
            offset => candidate[offset] && nearCore[offset] <= options.CoreGrowthMillimetres);

        var nearVentricle = DistanceToOutside(grid, Inverted(ventricle), cancellationToken);
        var boundary = HalfMaximum(values, head, chosen, ventricle, nearVentricle);

        ventricle = Reconstruct(
            dimensions,
            ventricle,
            offset => head[offset] && values[offset] <= boundary && nearVentricle[offset] <= options.CoreRimMillimetres);

        return new CoreSelection(
            [.. ventricle.Select(inside => inside ? (byte)1 : (byte)0)],

            // В разбор идёт весь тёмный ликвор головы, а не только глубокий:
            // врач должен видеть и то, что отброшено как наружное.
            [.. Enumerable.Range(0, total).Select(offset => head[offset] && !above[offset] ? (byte)1 : (byte)0)],
            head.Count(inside => inside),
            rejected);
    }

    /// <summary>
    /// Боковые желудочки парные: выбранные ядра должны лежать по обе стороны
    /// от средней линии головы. У здоровых узких желудочков на IXI толстое
    /// ядро находилось иногда только в одном из них, и маска одного желудочка
    /// заменила бы верную маску прежнего отбора.
    /// </summary>
    private static bool IsBilateral(
        SeriesGeometry geometry,
        VolumeDimensions dimensions,
        bool[] head,
        bool[] chosen,
        BaselineSegmentationOptions options)
    {
        SpatialVector[] directions =
        [
            geometry.RowDirection.Normalized(),
            geometry.ColumnDirection.Normalized(),
            geometry.SliceNormal,
        ];

        var axis = 0;

        for (var candidate = 1; candidate < directions.Length; candidate++)
        {
            if (Math.Abs(directions[candidate].X) > Math.Abs(directions[axis].X))
            {
                axis = candidate;
            }
        }

        double headSum = 0;
        long headCount = 0;

        for (var offset = 0; offset < head.Length; offset++)
        {
            if (head[offset])
            {
                headSum += CoordinateOf(dimensions, offset, axis);
                headCount++;
            }
        }

        var midline = headSum / headCount;
        long first = 0, second = 0;

        for (var offset = 0; offset < chosen.Length; offset++)
        {
            if (!chosen[offset])
            {
                continue;
            }

            if (CoordinateOf(dimensions, offset, axis) < midline)
            {
                first++;
            }
            else
            {
                second++;
            }
        }

        return Math.Min(first, second) >= (first + second) * options.CoreMinSideFraction;
    }

    private static int CoordinateOf(VolumeDimensions dimensions, int offset, int axis)
    {
        var (column, row, slice) = Locate(dimensions, offset);

        return axis switch
        {
            0 => column,
            1 => row,
            _ => slice,
        };
    }

    /// <summary>
    /// Голова, закрытая шаром заданного радиуса: раздувание расстоянием до
    /// ткани, заливка фона снаружи и сжатие обратно расстоянием до фона.
    /// </summary>
    private static bool[] SolidHead(VolumeGrid grid, bool[] above, double radius, CancellationToken cancellationToken)
    {
        var toTissue = DistanceToOutside(grid, Inverted(above), cancellationToken);
        var dilated = new bool[above.Length];

        for (var offset = 0; offset < above.Length; offset++)
        {
            dilated[offset] = above[offset] || toTissue[offset] <= radius;
        }

        var filled = LargestComponent(grid.Dimensions, FilledFromOutside(grid.Dimensions, dilated));
        var fromBackground = DistanceToOutside(grid, filled, cancellationToken);
        var head = new bool[above.Length];

        for (var offset = 0; offset < above.Length; offset++)
        {
            head[offset] = fromBackground[offset] > radius;
        }

        return head;
    }

    /// <summary>
    /// Граница полувысоты между ликвором ядер и тканью вокруг желудочка:
    /// середина между средней яркостью ядер и медианой оболочки 3–6 мм.
    /// Порог фона берёт только тёмную сердцевину, а стенка желудочка лежит
    /// в частичном объёме; прежнее дорастание по порогу внутри тёмных
    /// отсчётов на MPRAGE переходило стенку на 3 мм.
    /// </summary>
    private static double HalfMaximum(
        float[] values,
        bool[] head,
        bool[] core,
        bool[] ventricle,
        float[] fromVentricle)
    {
        var csf = 0.0;
        long csfCount = 0;

        for (var offset = 0; offset < core.Length; offset++)
        {
            if (core[offset])
            {
                csf += values[offset];
                csfCount++;
            }
        }

        var shell = new List<float>();

        for (var offset = 0; offset < ventricle.Length; offset++)
        {
            if (!ventricle[offset] && head[offset] && fromVentricle[offset] is >= 3 and <= 6)
            {
                shell.Add(values[offset]);
            }
        }

        var csfLevel = csf / csfCount;

        if (shell.Count == 0)
        {
            return csfLevel;
        }

        shell.Sort();

        return (csfLevel + shell[shell.Count / 2]) / 2;
    }

    private static bool[] Reconstruct(VolumeDimensions dimensions, bool[] seeds, Func<int, bool> allowed)
    {
        var result = (bool[])seeds.Clone();
        var stack = new Stack<int>();

        for (var offset = 0; offset < seeds.Length; offset++)
        {
            if (seeds[offset])
            {
                stack.Push(offset);
            }
        }

        while (stack.Count > 0)
        {
            var offset = stack.Pop();

            foreach (var next in NeighboursOf(dimensions, offset))
            {
                if (result[next] || !allowed(next))
                {
                    continue;
                }

                result[next] = true;
                stack.Push(next);
            }
        }

        return result;
    }

    /// <summary>
    /// Расстояние от точки вверх до последнего отсчёта ткани вдоль оси сетки,
    /// ближайшей к направлению «вверх», мм; бесконечность, если свод вне кадра.
    /// </summary>
    private static double Headroom(VolumeGrid grid, bool[] tissue, (int Axis, int Step) superior, int column, int row, int slice)
    {
        var dimensions = grid.Dimensions;
        int[] position = [column, row, slice];
        int[] sizes = [dimensions.Columns, dimensions.Rows, dimensions.Slices];
        double[] spacings = [grid.ColumnSpacingMillimetres, grid.RowSpacingMillimetres, grid.SliceSpacingMillimetres];

        var origin = position[superior.Axis];
        var last = origin;

        for (var index = origin; index >= 0 && index < sizes[superior.Axis]; index += superior.Step)
        {
            position[superior.Axis] = index;

            if (tissue[Offset(dimensions, position[0], position[1], position[2])])
            {
                last = index;
            }
        }

        if (last == 0 || last == sizes[superior.Axis] - 1)
        {
            // Голова доходит до края кадра: свод не снят, и расстояние до него
            // неизвестно. Прицельный блок у основания черепа иначе делал
            // клиновидную пазуху «ядром под сводом».
            return double.PositiveInfinity;
        }

        return Math.Abs(last - origin) * spacings[superior.Axis];
    }

    private static (int Axis, int Step) SuperiorAxis(SeriesGeometry geometry)
    {
        SpatialVector[] directions =
        [
            geometry.RowDirection.Normalized(),
            geometry.ColumnDirection.Normalized(),
            geometry.SliceNormal,
        ];

        var axis = 0;

        for (var candidate = 1; candidate < directions.Length; candidate++)
        {
            if (Math.Abs(directions[candidate].Z) > Math.Abs(directions[axis].Z))
            {
                axis = candidate;
            }
        }

        // В LPS +Z ведёт вверх.
        return (axis, directions[axis].Z >= 0 ? 1 : -1);
    }

    private static bool[] Inverted(bool[] source)
    {
        var inverted = new bool[source.Length];

        for (var offset = 0; offset < source.Length; offset++)
        {
            inverted[offset] = !source[offset];
        }

        return inverted;
    }

    private static (int Column, int Row, int Slice) Locate(VolumeDimensions dimensions, int offset)
    {
        var plane = dimensions.Columns * dimensions.Rows;

        return (offset % dimensions.Columns, (offset / dimensions.Columns) % dimensions.Rows, offset / plane);
    }

    private static List<int> Flood(VolumeDimensions dimensions, bool[] mask, bool[] visited, Stack<int> stack, int start)
    {
        stack.Clear();
        stack.Push(start);
        visited[start] = true;

        var offsets = new List<int>();

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            offsets.Add(offset);

            foreach (var next in NeighboursOf(dimensions, offset))
            {
                if (visited[next] || !mask[next])
                {
                    continue;
                }

                visited[next] = true;
                stack.Push(next);
            }
        }

        return offsets;
    }

    /// <summary>Результат отбора по ядрам.</summary>
    /// <param name="Labels">Выбранные желудочки.</param>
    /// <param name="Candidate">Ликвор, из которого шёл отбор, — для разбора решения.</param>
    /// <param name="HeadVoxels">Объём закрытой головы в отсчётах.</param>
    /// <param name="Rejected">Число отвергнутых ядер.</param>
    private sealed record CoreSelection(byte[] Labels, byte[] Candidate, long HeadVoxels, int Rejected);
}
