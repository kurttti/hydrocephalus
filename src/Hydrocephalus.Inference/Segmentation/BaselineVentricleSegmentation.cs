using System.Globalization;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Настройки классической сегментации желудочков.
///
/// Все значения — эвристики, подобранные под задачу, а не полученные обучением.
/// Они вынесены сюда, чтобы их можно было пересматривать явно и чтобы в коде
/// не оставалось чисел без объяснения.
/// </summary>
public sealed record BaselineSegmentationOptions
{
    /// <summary>
    /// Наибольшая доля головы, которую разумно отнести к ликвору.
    ///
    /// Порог внутри головы может встать не между ликвором и тканью, а между
    /// серым и белым веществом — метод Оцу предполагает два класса, а их три.
    /// Тогда «ликвором» окажется половина мозга. Доля выше этой границы означает
    /// именно такую ошибку, и её нужно назвать, а не выдать объём.
    /// </summary>
    public double MaxCsfFractionOfHead { get; init; } = 0.35;

    /// <summary>
    /// Наибольшее удаление самой дальней точки компоненты от центра головы,
    /// при котором компонента считается желудочковой, в долях радиуса головы.
    ///
    /// Условие ставится на самую дальнюю точку, а не на центр компоненты:
    /// у ликвора, охватывающего мозг оболочкой, центр совпадает с центром
    /// головы, и проверка по центру пропустила бы его целиком.
    ///
    /// Порог отделяет желудочки от ликвора в бороздах и наружных пространствах:
    /// по интенсивности они неотличимы, различает их только расположение.
    ///
    /// Значение выбрано с запасом. При гидроцефалии желудочки вытянуты к задним
    /// рогам и уходят далеко от центра; тесный порог отбрасывал бы именно
    /// расширенные желудочки — ровно те, ради измерения которых всё и делается.
    /// </summary>
    public double VentricleCentralityFraction { get; init; } = 0.6;

    /// <summary>Наименьший размер компоненты в отсчётах.</summary>
    public int MinComponentVoxels { get; init; } = 50;

    /// <summary>Наибольшее число оставляемых компонент.</summary>
    public int MaxComponents { get; init; } = 2;
}

/// <summary>
/// Результат классической сегментации.
/// </summary>
/// <param name="Mask">Полученная маска.</param>
/// <param name="Issues">Замечания к результату.</param>
/// <param name="Quality">Достоверность признаков, полученных по этой маске.</param>
public readonly record struct BaselineSegmentationResult(
    VoxelMask Mask,
    IReadOnlyList<QualityIssue> Issues,
    MeasurementQuality Quality);

/// <summary>
/// Классическая сегментация желудочковой системы порогом по интенсивности.
///
/// Это baseline в прямом смысле: точка отсчёта, с которой сравнивается обученная
/// модель, и работающая середина конвейера до её появления. Клинической
/// сегментацией она не является, и результат помечен
/// <see cref="MeasurementQuality.Questionable"/> по построению, а не по решению
/// вызывающего — иначе число из неё в отчёте выглядело бы так же, как
/// проверенное.
///
/// Что метод заведомо не умеет, и это не дефект, а его граница:
///
/// - он выделяет ликвор, а не желудочки. Ликвор в бороздах и наружных
///   пространствах по интенсивности неотличим; разделяет их только эвристика
///   расположения, и при выраженной атрофии она ошибается именно там, где
///   дифференциальный диагноз и нужен;
/// - он не разделяет боковые, третий и четвёртый желудочки — они связаны,
///   и порог видит их одной областью. Поэтому метка одна: желудочковая система;
/// - он зависит от взвешенности серии. При неизвестной взвешенности направление
///   порога определить нельзя, и метод отказывается работать, а не угадывает.
/// </summary>
public static class BaselineVentricleSegmentation
{
    /// <summary>Метка желудочковой системы.</summary>
    public static readonly AnatomicalLabel VentricularSystem = new("ventricular-system");

    /// <summary>
    /// Версия карты меток. Приставка называет метод: результат, полученный
    /// baseline-сегментацией, не должен сравниваться с результатом модели
    /// как одинаковый.
    /// </summary>
    public const string LabelMapVersion = "baseline-1.0.0";

    /// <summary>
    /// Сегментирует желудочковую систему.
    /// </summary>
    /// <param name="volume">Объём.</param>
    /// <param name="weighting">Взвешенность серии.</param>
    /// <param name="options">Настройки; null — значения по умолчанию.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Маска, замечания и достоверность.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если взвешенность неизвестна либо объём непригоден.
    /// </exception>
    public static BaselineSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        BaselineSegmentationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        options ??= new BaselineSegmentationOptions();

        var darkCsf = weighting switch
        {
            SeriesWeighting.T1 => true,
            SeriesWeighting.T2 or SeriesWeighting.Flair => false,

            // Угадать направление порога нельзя: ошибка выделит ткань вместо
            // ликвора и даст объём того же порядка с обратным смыслом.
            _ => throw new DomainRuleViolationException(
                "Baseline segmentation needs a known series weighting to decide the CSF threshold."),
        };

        var headThreshold = IntensityThresholds.Otsu(volume);
        var head = Head(volume, headThreshold, cancellationToken);

        if (head.Count == 0)
        {
            throw new DomainRuleViolationException(
                "Baseline segmentation found no head above the background threshold.");
        }

        // Порог внутри головы, а не по всему кадру: фон занимает большую часть
        // объёма и утянул бы границу к себе.
        var csfThreshold = IntensityThresholds.OtsuWithin(volume, head.Voxels);

        var candidate = Candidate(volume, head.Voxels, csfThreshold, darkCsf, cancellationToken);

        var candidateCount = candidate.Count(value => value != 0);

        if (candidateCount > head.Count * options.MaxCsfFractionOfHead)
        {
            // Порог встал не там, где предполагалось. Отдать половину мозга
            // как объём желудочков хуже, чем не отдать ничего.
            return new BaselineSegmentationResult(
                Empty(volume.Grid),
                [.. Report(rejectedComponents: 0), ThresholdFailed(candidateCount, head.Count)],
                MeasurementQuality.Unreliable);
        }

        var labels = SelectVentricles(volume.Grid, candidate, head, options, out var rejected);

        var mask = new VoxelMask(
            volume.Grid,
            new LabelMap { Version = LabelMapVersion, Labels = [VentricularSystem] },
            labels);

        return new BaselineSegmentationResult(
            mask,
            Report(rejected),

            // Достоверность задаётся здесь, а не вызывающим: метод не
            // валидирован, и забыть пометить его результат нельзя.
            MeasurementQuality.Questionable);
    }

    private static VoxelMask Empty(VolumeGrid grid) => new(
        grid,
        new LabelMap { Version = LabelMapVersion, Labels = [VentricularSystem] },
        new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]);

    private static QualityIssue ThresholdFailed(long selected, long head) => new()
    {
        Code = QualityIssueCode.InconsistentGeometry,
        Severity = QualityIssueSeverity.Blocking,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = "thresholdDidNotIsolateCsf",
            ["selectedFraction"] = ((double)selected / head).ToString("0.###", CultureInfo.InvariantCulture),
        },
    };

    private static List<QualityIssue> Report(int rejectedComponents)
    {
        var issues = new List<QualityIssue>
        {
            // Замечание есть всегда: врач должен видеть, что маска получена
            // невалидированным методом, даже когда она выглядит хорошо.
            new()
            {
                Code = QualityIssueCode.OutOfDistribution,
                Severity = QualityIssueSeverity.Warning,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = "unvalidatedBaselineSegmentation",
                    ["method"] = LabelMapVersion,
                },
            },
        };

        if (rejectedComponents > 0)
        {
            // Отброшенные периферические скопления ликвора — обычное дело,
            // но их число говорит, насколько эвристика расположения работала
            // на этом случае.
            issues.Add(new QualityIssue
            {
                Code = QualityIssueCode.InconsistentGeometry,
                Severity = QualityIssueSeverity.Warning,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = "peripheralCsfDiscarded",
                    ["components"] = rejectedComponents.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        return issues;
    }

    /// <summary>
    /// Выделяет голову: наибольшая связная область выше порога фона плюс залитые
    /// внутренние полости.
    ///
    /// Заливка полостей обязательна, а не украшение. На T1 ликвор темнее середины
    /// распределения, и порог, отделяющий голову от фона, относит желудочки
    /// к фону вместе с воздухом. Без заливки желудочек оказывается вне головы,
    /// и искать его внутри неё бессмысленно.
    /// </summary>
    private static HeadExtent Head(
        IVoxelVolume volume,
        double threshold,
        CancellationToken cancellationToken)
    {
        var dimensions = volume.Grid.Dimensions;
        var size = dimensions.Columns * dimensions.Rows * dimensions.Slices;

        var above = new bool[size];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    above[Offset(dimensions, column, row, slice)] =
                        volume[column, row, slice] > threshold;
                }
            }
        }

        // Порядок важен. Сначала заливка фона от границы кадра: всё, чего она
        // не достигла, лежит внутри головы, включая ликвор в бороздах, который
        // по интенсивности от фона неотличим. Если сначала взять наибольшую
        // связную область, ободок ткани за таким ликвором окажется отдельной
        // областью и потеряется, а голова выйдет меньше, чем она есть.
        var head = FilledFromOutside(dimensions, above);

        // И только теперь — наибольшая область: отдельные яркие пятна вне головы
        // заливкой не убираются, потому что сами лежат выше порога.
        head = LargestComponent(dimensions, head);

        double sumColumn = 0;
        double sumRow = 0;
        double sumSlice = 0;
        long count = 0;

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    if (!head[Offset(dimensions, column, row, slice)])
                    {
                        continue;
                    }

                    sumColumn += column;
                    sumRow += row;
                    sumSlice += slice;
                    count++;
                }
            }
        }

        if (count == 0)
        {
            return new HeadExtent(head, default, 0, 0);
        }

        var centre = new Centre(sumColumn / count, sumRow / count, sumSlice / count);

        // Радиус берётся как радиус шара того же объёма: он устойчив к форме
        // и не зависит от того, попала ли в кадр шея.
        var radius = Math.Cbrt(3.0 * count / (4.0 * Math.PI));

        return new HeadExtent(head, centre, radius, count);
    }

    /// <summary>
    /// Оставляет наибольшую связную область: отдельные яркие пятна вне головы
    /// встречаются и не должны попадать ни в голову, ни в расчёт её центра.
    /// </summary>
    private static bool[] LargestComponent(VolumeDimensions dimensions, bool[] source)
    {
        var visited = new bool[source.Length];
        var stack = new Stack<int>();
        var result = new bool[source.Length];
        var largestSize = 0;
        var largest = new List<int>();

        for (var start = 0; start < source.Length; start++)
        {
            if (!source[start] || visited[start])
            {
                continue;
            }

            var component = new List<int>();

            stack.Clear();
            stack.Push(start);
            visited[start] = true;

            while (stack.Count > 0)
            {
                var offset = stack.Pop();
                component.Add(offset);

                foreach (var next in NeighboursOf(dimensions, offset))
                {
                    if (visited[next] || !source[next])
                    {
                        continue;
                    }

                    visited[next] = true;
                    stack.Push(next);
                }
            }

            if (component.Count > largestSize)
            {
                largestSize = component.Count;
                largest = component;
            }
        }

        foreach (var offset in largest)
        {
            result[offset] = true;
        }

        return result;
    }

    /// <summary>
    /// Возвращает всё, чего не достигает заливка фона от границы кадра.
    /// </summary>
    private static bool[] FilledFromOutside(VolumeDimensions dimensions, bool[] above)
    {
        var head = (bool[])above.Clone();

        var outside = new bool[head.Length];
        var stack = new Stack<int>();

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var onBoundary = column == 0 || row == 0 || slice == 0
                        || column == dimensions.Columns - 1
                        || row == dimensions.Rows - 1
                        || slice == dimensions.Slices - 1;

                    if (!onBoundary)
                    {
                        continue;
                    }

                    var offset = Offset(dimensions, column, row, slice);

                    if (head[offset] || outside[offset])
                    {
                        continue;
                    }

                    outside[offset] = true;
                    stack.Push(offset);
                }
            }
        }

        while (stack.Count > 0)
        {
            var offset = stack.Pop();

            foreach (var next in NeighboursOf(dimensions, offset))
            {
                if (outside[next] || head[next])
                {
                    continue;
                }

                outside[next] = true;
                stack.Push(next);
            }
        }

        for (var offset = 0; offset < head.Length; offset++)
        {
            if (!head[offset] && !outside[offset])
            {
                head[offset] = true;
            }
        }

        return head;
    }

    private static IEnumerable<int> NeighboursOf(VolumeDimensions dimensions, int offset)
    {
        var slice = offset / (dimensions.Columns * dimensions.Rows);
        var remainder = offset % (dimensions.Columns * dimensions.Rows);
        var row = remainder / dimensions.Columns;
        var column = remainder % dimensions.Columns;

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

            yield return Offset(dimensions, nextColumn, nextRow, nextSlice);
        }
    }

    private static byte[] Candidate(
        IVoxelVolume volume,
        bool[] head,
        double csfThreshold,
        bool darkCsf,
        CancellationToken cancellationToken)
    {
        var dimensions = volume.Grid.Dimensions;
        var candidate = new byte[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var offset = Offset(dimensions, column, row, slice);

                    // Ограничение головой обязательно: на T1 фон темнее ликвора,
                    // и без него в маску попал бы воздух вокруг головы.
                    if (!head[offset])
                    {
                        continue;
                    }

                    var value = volume[column, row, slice];
                    // Сравнение несимметрично намеренно: порог — верхняя граница
                    // нижнего класса, поэтому в него он входит, а в верхний нет.
                    var isCsf = darkCsf ? value <= csfThreshold : value > csfThreshold;

                    if (isCsf)
                    {
                        candidate[offset] = 1;
                    }
                }
            }
        }

        return candidate;
    }

    private static byte[] SelectVentricles(
        VolumeGrid grid,
        byte[] candidate,
        HeadExtent head,
        BaselineSegmentationOptions options,
        out int rejected)
    {
        var dimensions = grid.Dimensions;
        var visited = new bool[candidate.Length];
        var components = new List<Component>();

        var stack = new Stack<int>();

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var offset = Offset(dimensions, column, row, slice);

                    if (candidate[offset] == 0 || visited[offset])
                    {
                        continue;
                    }

                    components.Add(Flood(dimensions, candidate, visited, stack, column, row, slice));
                }
            }
        }

        var limit = head.Radius * options.VentricleCentralityFraction;

        var accepted = components
            .Where(component => component.Size >= options.MinComponentVoxels)
            .Where(component => component.FarthestFrom(head.Centre) <= limit)
            .OrderByDescending(component => component.Size)
            .Take(options.MaxComponents)
            .ToList();

        rejected = components.Count(component => component.Size >= options.MinComponentVoxels)
            - accepted.Count;

        var labels = new byte[candidate.Length];

        foreach (var offset in accepted.SelectMany(component => component.Offsets))
        {
            labels[offset] = 1;
        }

        return labels;
    }

    private static Component Flood(
        VolumeDimensions dimensions,
        byte[] candidate,
        bool[] visited,
        Stack<int> stack,
        int startColumn,
        int startRow,
        int startSlice)
    {
        stack.Clear();
        stack.Push(Offset(dimensions, startColumn, startRow, startSlice));
        visited[Offset(dimensions, startColumn, startRow, startSlice)] = true;

        var offsets = new List<int>();
        var coordinates = new List<(int Column, int Row, int Slice)>();

        while (stack.Count > 0)
        {
            var offset = stack.Pop();
            offsets.Add(offset);

            var slice = offset / (dimensions.Columns * dimensions.Rows);
            var remainder = offset % (dimensions.Columns * dimensions.Rows);
            var row = remainder / dimensions.Columns;
            var column = remainder % dimensions.Columns;

            coordinates.Add((column, row, slice));

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

                var next = Offset(dimensions, nextColumn, nextRow, nextSlice);

                if (visited[next] || candidate[next] == 0)
                {
                    continue;
                }

                visited[next] = true;
                stack.Push(next);
            }
        }

        return new Component(offsets, coordinates);
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

    private static int Offset(VolumeDimensions dimensions, int column, int row, int slice) =>
        (((slice * dimensions.Rows) + row) * dimensions.Columns) + column;

    private readonly record struct Centre(double Column, double Row, double Slice);

    private readonly record struct HeadExtent(bool[] Voxels, Centre Centre, double Radius, long Count);

    private sealed record Component(List<int> Offsets, List<(int Column, int Row, int Slice)> Coordinates)
    {
        public int Size => this.Offsets.Count;

        /// <summary>
        /// Удаление самой дальней точки компоненты от заданного центра.
        /// Берётся именно дальняя точка: у оболочки, охватывающей мозг,
        /// центр совпадает с центром головы.
        /// </summary>
        public double FarthestFrom(Centre other)
        {
            var farthest = 0.0;

            foreach (var (column, row, slice) in this.Coordinates)
            {
                var dc = column - other.Column;
                var dr = row - other.Row;
                var ds = slice - other.Slice;

                farthest = Math.Max(farthest, (dc * dc) + (dr * dr) + (ds * ds));
            }

            return Math.Sqrt(farthest);
        }
    }
}
