using System.Globalization;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Границы правдоподобия маски сегментации.
/// </summary>
public sealed record MaskGeometryLimits
{
    /// <summary>
    /// Наибольшее число связных компонент структуры, при котором маска ещё
    /// считается целой. Боковые желудочки — две компоненты, и распад на десятки
    /// означает не анатомию, а шум сегментации.
    /// </summary>
    public int MaxComponentsPerStructure { get; init; } = 2;

    /// <summary>
    /// Наименьший размер компоненты в отсчётах, при котором она учитывается.
    /// Одиночные отсчёты на границе — обычный шум, и считать их отдельными
    /// компонентами значило бы сообщать о распаде на каждой маске.
    /// </summary>
    public int MinComponentVoxels { get; init; } = 20;
}

/// <summary>
/// Геометрический контроль маски — этап между сегментацией и вычислением
/// признаков.
///
/// Проверяется не совпадение с истиной, а внутренняя правдоподобность: истины
/// здесь нет, а числа из неправдоподобной маски выглядят так же, как из хорошей.
///
/// Замечания почти все предупреждающие: сегментация редко бывает идеальной,
/// и блокировать анализ из-за одиночного постороннего пятна значило бы
/// отказывать в работе на ровном месте. Блокирует единственное — отсутствие
/// структуры, без которой запрошенный признак не вычислить.
/// </summary>
public static class MaskGeometryChecks
{
    /// <summary>
    /// Проверяет маску.
    /// </summary>
    /// <param name="mask">Маска сегментации.</param>
    /// <param name="required">Структуры, без которых анализ невозможен.</param>
    /// <param name="limits">Границы правдоподобия; null — значения по умолчанию.</param>
    /// <returns>Найденные замечания.</returns>
    public static IReadOnlyList<QualityIssue> Inspect(
        VoxelMask mask,
        IReadOnlyList<AnatomicalLabel> required,
        MaskGeometryLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(required);

        limits ??= new MaskGeometryLimits();

        var issues = new List<QualityIssue>();
        var requiredCodes = required.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);

        for (var label = 1; label <= mask.Map.Labels.Count; label++)
        {
            var structure = mask.Map.Labels[label - 1];
            var statistics = Analyse(mask, (byte)label, limits.MinComponentVoxels);

            if (statistics.VoxelCount == 0)
            {
                if (requiredCodes.Contains(structure.Code))
                {
                    // Без структуры запрошенный признак не вычислить, и подставить
                    // ноль вместо объёма значило бы выдать отсутствие сегментации
                    // за отсутствие ткани.
                    issues.Add(Issue(
                        QualityIssueCode.InconsistentGeometry,
                        QualityIssueSeverity.Blocking,
                        ("reason", "requiredStructureMissing"),
                        ("structure", structure.Code)));
                }

                continue;
            }

            if (statistics.Components > limits.MaxComponentsPerStructure)
            {
                issues.Add(Issue(
                    QualityIssueCode.InconsistentGeometry,
                    QualityIssueSeverity.Warning,
                    ("reason", "fragmentedStructure"),
                    ("structure", structure.Code),
                    ("components", statistics.Components.ToString(CultureInfo.InvariantCulture))));
            }

            if (statistics.TouchesBoundary)
            {
                // Структура, упирающаяся в край объёма, скорее всего обрезана полем
                // обзора: её объём занижен на неизвестную величину, и понять это
                // по самому числу нельзя.
                issues.Add(Issue(
                    QualityIssueCode.HeadTruncated,
                    QualityIssueSeverity.Warning,
                    ("reason", "structureTouchesVolumeBoundary"),
                    ("structure", structure.Code)));
            }
        }

        return issues;
    }

    /// <summary>
    /// Считает связные компоненты структуры.
    ///
    /// Связность по шести соседям, а не по двадцати шести: диагональное касание
    /// углами не делает две области одной структурой, и по такой связности
    /// разделённые доли сливались бы в одну компоненту.
    /// </summary>
    /// <param name="mask">Маска.</param>
    /// <param name="label">Номер метки.</param>
    /// <param name="minComponentVoxels">Наименьший учитываемый размер компоненты.</param>
    /// <returns>Число компонент не меньше заданного размера.</returns>
    public static int CountComponents(VoxelMask mask, byte label, int minComponentVoxels = 1)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return Analyse(mask, label, minComponentVoxels).Components;
    }

    private static QualityIssue Issue(
        QualityIssueCode code,
        QualityIssueSeverity severity,
        params (string Key, string Value)[] parameters) =>
        new()
        {
            Code = code,
            Severity = severity,
            Parameters = parameters.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal),
        };

    private static Statistics Analyse(VoxelMask mask, byte label, int minComponentVoxels)
    {
        var dimensions = mask.Grid.Dimensions;
        var visited = new bool[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        var voxelCount = 0L;
        var components = 0;
        var touchesBoundary = false;

        // Обход явным стеком, а не рекурсией: у структуры бывают миллионы
        // отсчётов, и рекурсия такой глубины кладёт процесс.
        var stack = new Stack<(int Column, int Row, int Slice)>();

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (mask[column, row, slice] != label || visited[Offset(dimensions, column, row, slice)])
                    {
                        continue;
                    }

                    var size = Flood(mask, label, visited, stack, column, row, slice, ref touchesBoundary);

                    voxelCount += size;

                    if (size >= minComponentVoxels)
                    {
                        components++;
                    }
                }
            }
        }

        return new Statistics(voxelCount, components, touchesBoundary);
    }

    private static int Flood(
        VoxelMask mask,
        byte label,
        bool[] visited,
        Stack<(int Column, int Row, int Slice)> stack,
        int startColumn,
        int startRow,
        int startSlice,
        ref bool touchesBoundary)
    {
        var dimensions = mask.Grid.Dimensions;

        stack.Clear();
        stack.Push((startColumn, startRow, startSlice));
        visited[Offset(dimensions, startColumn, startRow, startSlice)] = true;

        var size = 0;

        while (stack.Count > 0)
        {
            var (column, row, slice) = stack.Pop();
            size++;

            if (column == 0 || row == 0 || slice == 0
                || column == dimensions.Columns - 1
                || row == dimensions.Rows - 1
                || slice == dimensions.Slices - 1)
            {
                touchesBoundary = true;
            }

            foreach (var (dc, dr, ds) in Neighbours)
            {
                var nextColumn = column + dc;
                var nextRow = row + dr;
                var nextSlice = slice + ds;

                if (nextColumn < 0 || nextRow < 0 || nextSlice < 0
                    || nextColumn >= dimensions.Columns
                    || nextRow >= dimensions.Rows
                    || nextSlice >= dimensions.Slices)
                {
                    continue;
                }

                var offset = Offset(dimensions, nextColumn, nextRow, nextSlice);

                if (visited[offset] || mask[nextColumn, nextRow, nextSlice] != label)
                {
                    continue;
                }

                visited[offset] = true;
                stack.Push((nextColumn, nextRow, nextSlice));
            }
        }

        return size;
    }

    private static readonly (int Column, int Row, int Slice)[] Neighbours =
    [
        (1, 0, 0),
        (-1, 0, 0),
        (0, 1, 0),
        (0, -1, 0),
        (0, 0, 1),
        (0, 0, -1),
    ];

    private static int Offset(
        Domain.Imaging.VolumeDimensions dimensions,
        int column,
        int row,
        int slice) =>
        (((slice * dimensions.Rows) + row) * dimensions.Columns) + column;

    private readonly record struct Statistics(long VoxelCount, int Components, bool TouchesBoundary);
}
