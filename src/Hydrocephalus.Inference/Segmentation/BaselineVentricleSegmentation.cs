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
/// не оставалось чисел без объяснения. Как они проверялись на публичных
/// данных, записано в docs/data/README.md.
/// </summary>
public sealed record BaselineSegmentationOptions
{
    /// <summary>
    /// Наибольшая доля головы, которую разумно отнести к ликвору.
    ///
    /// Порог внутри головы может встать не между ликвором и тканью, а между
    /// серым и белым веществом — метод Оцу предполагает два класса, а их три.
    /// Тогда «ликвором» окажется половина мозга. Доля выше этой границы означает
    /// именно такую ошибку, и её нужно назвать, а не выдать объём. Та же граница
    /// применяется и к итоговой маске.
    /// </summary>
    public double MaxCsfFractionOfHead { get; init; } = 0.35;

    /// <summary>
    /// Наименьшее расстояние от поверхности головы, мм, на котором должна
    /// лежать вся компонента, чтобы считаться желудочковой.
    ///
    /// Ликвор в бороздах и наружных пространствах по интенсивности неотличим
    /// от желудочкового; различает их только расположение. Раньше расположение
    /// мерилось удалением от центра головы, но центр тяжести головы уезжает
    /// в шею, если она попала в кадр, и тогда отбрасывались как раз желудочки
    /// (так было на всех сериях T1 набора IXI). Глубина от поверхности от шеи
    /// не зависит.
    ///
    /// Условие ставится на самую мелкую точку компоненты: ликвор, дотянувшийся
    /// до поверхности, отбрасывается целиком, даже если большая его часть лежит
    /// глубоко. При гидроцефалии желудочки растут, но остаются в глубине —
    /// в отличие от центральности, это условие расширенные желудочки не теряет.
    /// </summary>
    public double MinDepthMillimetres { get; init; } = 12;

    /// <summary>
    /// На сколько отсчётов раздувается голова перед заливкой фона снаружи.
    ///
    /// Заливка фона от края кадра проходит в голову по любой тёмной щели,
    /// дотянувшейся до края: на T1 это ликвор вокруг спинного мозга в шее.
    /// Дальше она растекается по субарахноидальному пространству, и голова
    /// кончается на поверхности мозга, а не на коже. Раздувание закрывает
    /// такие щели на время заливки; после неё голова сжимается обратно.
    /// </summary>
    public int LeakClosingVoxels { get; init; } = 2;

    /// <summary>
    /// На сколько миллиметров маска T1 дорастает за порог фона в сторону
    /// частичного объёма.
    ///
    /// На T1 ликвор отделяется от ткани порогом фона — он надёжен, но берёт
    /// только тёмную сердцевину желудочка. Граница желудочка лежит в частичном
    /// объёме между ликвором и тканью, и без дорастания объём занижается вдвое.
    /// Дорастание ограничено расстоянием, а не только интенсивностью: иначе
    /// через частичный объём маска перетекла бы в борозды.
    /// </summary>
    public double BoundaryGrowthMillimetres { get; init; } = 3;

    /// <summary>
    /// Наименьший правдоподобный объём желудочковой системы, мл.
    ///
    /// Маска меньше этого — не малые желудочки, а промах: метод захватил
    /// один рог или посторонний ликвор (на IXI такие маски лежали в шее).
    /// Отказ здесь заменяет число, которое выглядело бы как находка.
    /// </summary>
    public double MinVentricleMillilitres { get; init; } = 5;

    /// <summary>
    /// Радиус закрытия головы для отбора по ядрам, мм. Закрывает тёмную полосу
    /// ликвора и кости, по которой заливка фона затекала в борозды.
    /// </summary>
    public double CoreHeadClosingMillimetres { get; init; } = 10;

    /// <summary>
    /// Толщина ликвора, с которой начинается ядро, мм: расстояние до ближайшего
    /// не-ликвора. Перемычки к бороздам и цистернам тоньше, тела расширенных
    /// желудочков — толще.
    /// </summary>
    public double CoreThicknessMillimetres { get; init; } = 3;

    /// <summary>Наименьшая глубина всех отсчётов желудочкового ядра, мм.</summary>
    public double CoreMinDepthMillimetres { get; init; } = 25;

    /// <summary>
    /// Наименьшая средняя яркость ядра в долях порога фона. Воздух пазух
    /// и сосцевидных отростков темнее ликвора: на клинике 0.13–0.16 против 0.40–0.43.
    /// </summary>
    public double CoreMinCsfToThreshold { get; init; } = 0.25;

    /// <summary>
    /// Наибольшее расстояние от центра ядра до свода черепа, мм. Желудочки
    /// лежат в 35–75 мм от свода; глотка, пазухи и базальные цистерны — дальше 100.
    /// </summary>
    public double CoreMaxHeadroomMillimetres { get; init; } = 80;

    /// <summary>
    /// Наименьшая доля выбранных ядер по меньшую сторону средней линии.
    /// </summary>
    public double CoreMinSideFraction { get; init; } = 0.2;

    /// <summary>Наименьший объём ядра, мл.</summary>
    public double MinCoreMillilitres { get; init; } = 0.5;

    /// <summary>Насколько ядру возвращается связанный с ним ликвор, мм.</summary>
    public double CoreGrowthMillimetres { get; init; } = 6;

    /// <summary>Насколько маска дорастает до полувысоты у стенки, мм.</summary>
    public double CoreRimMillimetres { get; init; } = 2;

    /// <summary>
    /// Ширина сглаживания на полувысоте для последней ступени отбора по ядрам, мм.
    /// Шум шумных и низкоконтрастных серий дробит ликвор желудочков; сглаживание
    /// до этой ширины собирает его обратно и не стирает стенку.
    /// </summary>
    public double CoreSmoothingMillimetres { get; init; } = 1.5;

    /// <summary>
    /// Наименьший запас от маски сглаженной ступени до края кадра, мм. У верных
    /// клинических масок он 38–57 мм; маска прицельного блока, обрезанная
    /// кадром, после сглаживания лежала в 2 мм от края, не касаясь его.
    /// </summary>
    public double SmoothedCoreFrameMarginMillimetres { get; init; } = 10;

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
    MeasurementQuality Quality)
{
    /// <summary>
    /// Разбор решения метода для просмотра врачом: метка 1 — область, которую
    /// метод выбрал как желудочки, метка 2 — ликвор, который он отбросил.
    ///
    /// Нужен именно при отказе. Маска для измерения при отказе пуста, и по ней
    /// не видно, что метод нашёл и почему не отдал: фрагмент в пару миллилитров,
    /// область у края кадра или ликвор, отброшенный как периферический. Врач
    /// различает эти случаи на снимке за секунды; из числа их не различить.
    /// В измерение разбор не идёт никогда.
    /// </summary>
    public VoxelMask? Review { get; init; }
}

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
public static partial class BaselineVentricleSegmentation
{
    /// <summary>Метка желудочковой системы.</summary>
    public static readonly AnatomicalLabel VentricularSystem = new("ventricular-system");

    /// <summary>
    /// Версия карты меток. Приставка называет метод: результат, полученный
    /// baseline-сегментацией, не должен сравниваться с результатом модели
    /// как одинаковый.
    /// </summary>
    public const string LabelMapVersion = "baseline-1.3.0";

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
            // На FLAIR сигнал ликвора подавлен, и он тёмный, как на T1. Прежде
            // FLAIR шёл вместе с T2 — порог искал яркий ликвор и выбирал ткань.
            SeriesWeighting.T1 or SeriesWeighting.Flair => true,
            SeriesWeighting.T2 => false,

            // Угадать направление порога нельзя: ошибка выделит ткань вместо
            // ликвора и даст объём того же порядка с обратным смыслом.
            _ => throw new DomainRuleViolationException(
                "Baseline segmentation needs a known series weighting to decide the CSF threshold."),
        };

        var grid = volume.Grid;
        var headThreshold = IntensityThresholds.Otsu(volume);
        var (head, open) = Head(volume, headThreshold, options.LeakClosingVoxels, cancellationToken);
        var headCount = head.Count(inside => inside);

        if (headCount == 0)
        {
            throw new DomainRuleViolationException(
                "Baseline segmentation found no head above the background threshold.");
        }

        // Порог между тканями считается внутри головы, а не по всему кадру:
        // фон занимает большую часть объёма и утянул бы границу к себе.
        // И по голове без закрытия щелей: закрытие запирает внутри воздух
        // пазух, рта и глотки, и этот воздух утянул бы к себе границу
        // частичного объёма — на IXI объём желудочков падал на треть.
        var tissueThreshold = IntensityThresholds.OtsuWithin(volume, open);

        // Направления несимметричны не случайно. На T2 ликвор — самый яркий
        // класс головы, и порог Оцу внутри неё отделяет его от ткани. На T1
        // ликвор — тёмный хвост в один-два процента головы: его вклад в
        // межклассовую дисперсию ничтожен рядом с парой «серое — белое вещество»,
        // и порог внутри головы встаёт между ними, забирая шестьдесят процентов
        // головы. Ни трёхклассовый, ни повторный порог этого не исправляют.
        // Отделяет ликвор T1 порог фона: всё, что темнее ткани и лежит внутри
        // головы, — ликвор, кость и воздух пазух, а последние два отсекает
        // глубина.
        var csfThreshold = darkCsf ? headThreshold : tissueThreshold;

        var (byDepth, selectedByDepth) = SelectByDepth(volume, head, open, headCount, tissueThreshold, csfThreshold, darkCsf, options, cancellationToken);

        // Отбор по ядрам включается только там, где прежний отбор отказал.
        // Порядок важен: на IXI ядра иногда находятся и у обычных желудочков,
        // но растут они от ядра не дальше нескольких миллиметров, и маска
        // выходила меньше прежней — на IXI175 вдвое (80.8 против 38.6 мл).
        // Прежний отбор на таких сериях проверен глазами, а ядра нужны там,
        // где он не даёт ничего: на клинических объёмных T1 он отказывал всегда.
        // Пустая маска без отказа — тоже «ничего не найдено»: отказ об этом
        // не объявляется только потому, что отбрасывать было нечего.
        if (!darkCsf || (byDepth.Quality != MeasurementQuality.Unreliable && selectedByDepth != 0))
        {
            return byDepth;
        }

        var refusal = byDepth;

        if (SelectByCores(volume, headThreshold, options, cancellationToken) is { } cores)
        {
            var byCores = Finish(grid, cores.Labels, cores.Candidate, cores.HeadVoxels, cores.Rejected, options);

            if (byCores.Quality != MeasurementQuality.Unreliable)
            {
                return byCores;
            }

            // Отказ прежнего отбора на отказ по ядрам меняется только тогда, когда
            // прежний отбор не нашёл вообще ничего: причина отказа — часть разбора,
            // и разбор по ядрам в этом случае единственный, где что-то видно.
            if (selectedByDepth == 0)
            {
                refusal = byCores;
            }
        }

        return SelectBySmoothedCores(volume, options, cancellationToken) is { Quality: not MeasurementQuality.Unreliable } bySmoothedCores
            ? bySmoothedCores
            : refusal;
    }

    private static (BaselineSegmentationResult Result, long Selected) SelectByDepth(
        IVoxelVolume volume,
        bool[] head,
        bool[] open,
        long headCount,
        double tissueThreshold,
        double csfThreshold,
        bool darkCsf,
        BaselineSegmentationOptions options,
        CancellationToken cancellationToken)
    {
        var grid = volume.Grid;
        var candidate = Candidate(volume, head, csfThreshold, darkCsf, cancellationToken);
        var candidateCount = candidate.Count(value => value != 0);

        if (candidateCount > headCount * options.MaxCsfFractionOfHead)
        {
            // Порог встал не там, где предполагалось. Отдать половину мозга
            // как объём желудочков хуже, чем не отдать ничего.
            return (Refused(grid, ThresholdFailed(candidateCount, headCount), selected: null, candidate), 0);
        }

        var depth = DistanceToOutside(grid, head, cancellationToken);

        var labels = SelectVentricles(grid.Dimensions, candidate, depth, options, out var rejected);

        if (darkCsf)
        {
            labels = GrowIntoPartialVolume(volume, head, open, labels, tissueThreshold, csfThreshold, options, cancellationToken);
        }

        return (Finish(grid, labels, candidate, headCount, rejected, options), labels.LongCount(value => value != 0));
    }

    private static BaselineSegmentationResult Finish(
        VolumeGrid grid,
        byte[] labels,
        byte[] candidate,
        long headCount,
        int rejected,
        BaselineSegmentationOptions options)
    {
        var voxelMillilitres = grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres
            * grid.SliceSpacingMillimetres / 1000.0;
        var selected = labels.Count(value => value != 0);

        if (selected > headCount * options.MaxCsfFractionOfHead)
        {
            return Refused(grid, ThresholdFailed(selected, headCount), labels, candidate);
        }

        if (TouchesFrame(grid.Dimensions, labels))
        {
            // Маска упирается в край кадра: структура обрезана, и объём —
            // объём её части. Проверяется маска, а не охват серии: прицельный
            // блок, в который желудочки поместились целиком, измерять можно,
            // а в клинической выборке почти все серии T2 уровня Extended —
            // блоки толщиной 30–80 мм.
            return Refused(grid, TruncatedByFrame(), labels, candidate);
        }

        var millilitres = selected * voxelMillilitres;

        if (selected > 0 && millilitres < options.MinVentricleMillilitres)
        {
            return Refused(grid, TooSmall(millilitres), labels, candidate);
        }

        var mask = new VoxelMask(
            grid,
            new LabelMap { Version = LabelMapVersion, Labels = [VentricularSystem] },
            labels);

        return new BaselineSegmentationResult(
            mask,
            Report(rejected),

            // Достоверность задаётся здесь, а не вызывающим: метод не
            // валидирован, и забыть пометить его результат нельзя.
            MeasurementQuality.Questionable)
        {
            Review = ReviewOf(grid, labels, candidate),
        };
    }

    private static BaselineSegmentationResult Refused(
        VolumeGrid grid,
        QualityIssue reason,
        byte[]? selected,
        byte[] candidate) => new(
        new VoxelMask(
            grid,
            new LabelMap { Version = LabelMapVersion, Labels = [VentricularSystem] },
            new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]),
        [.. Report(rejectedComponents: 0), reason],
        MeasurementQuality.Unreliable)
        {
            Review = ReviewOf(grid, selected, candidate),
        };

    /// <summary>Метка отброшенного ликвора в разборе решения.</summary>
    public static readonly AnatomicalLabel DiscardedCsf = new("discarded-csf");

    private static VoxelMask ReviewOf(VolumeGrid grid, byte[]? selected, byte[] candidate)
    {
        var labels = new byte[candidate.Length];

        for (var offset = 0; offset < labels.Length; offset++)
        {
            // Выбранное берёт верх: дорастание выводит маску T1 за кандидатов,
            // и эта кромка тоже часть решения метода.
            labels[offset] = selected is not null && selected[offset] != 0
                ? (byte)1
                : candidate[offset] != 0 ? (byte)2 : (byte)0;
        }

        return new VoxelMask(
            grid,
            new LabelMap { Version = LabelMapVersion, Labels = [VentricularSystem, DiscardedCsf] },
            labels);
    }

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

    private static QualityIssue TruncatedByFrame() => new()
    {
        Code = QualityIssueCode.HeadTruncated,
        Severity = QualityIssueSeverity.Blocking,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = "ventricularSystemTruncatedByFrame",
        },
    };

    private static bool TouchesFrame(VolumeDimensions dimensions, byte[] labels)
    {
        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var onFrame = column == 0 || row == 0 || slice == 0
                        || column == dimensions.Columns - 1
                        || row == dimensions.Rows - 1
                        || slice == dimensions.Slices - 1;

                    if (onFrame && labels[Offset(dimensions, column, row, slice)] != 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static QualityIssue TooSmall(double millilitres) => new()
    {
        Code = QualityIssueCode.InconsistentGeometry,
        Severity = QualityIssueSeverity.Blocking,
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = "ventricularSystemImplausiblySmall",
            ["millilitres"] = millilitres.ToString("0.0", CultureInfo.InvariantCulture),
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
    /// <returns>
    /// Голову с закрытыми щелями — по ней ищется ликвор и меряется глубина —
    /// и голову без закрытия — по ней считаются пороги между тканями.
    /// </returns>
    private static (bool[] Closed, bool[] Open) Head(
        IVoxelVolume volume,
        double threshold,
        int closingVoxels,
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

        var open = LargestComponent(dimensions, FilledFromOutside(dimensions, above));

        // Раздувание перед заливкой закрывает тёмные щели, по которым фон
        // затёк бы внутрь, — см. LeakClosingVoxels.
        var closed = above;

        for (var step = 0; step < closingVoxels; step++)
        {
            closed = Dilate(dimensions, closed);
        }

        // Порядок важен. Сначала заливка фона от границы кадра: всё, чего она
        // не достигла, лежит внутри головы, включая ликвор в бороздах, который
        // по интенсивности от фона неотличим. Если сначала взять наибольшую
        // связную область, ободок ткани за таким ликвором окажется отдельной
        // областью и потеряется, а голова выйдет меньше, чем она есть.
        var head = FilledFromOutside(dimensions, closed);

        // И только теперь — наибольшая область: отдельные яркие пятна вне головы
        // заливкой не убираются, потому что сами лежат выше порога.
        head = LargestComponent(dimensions, head);

        // Сжатие возвращает границу на кожу: иначе в голову вошёл бы слой
        // воздуха толщиной в раздувание, а на T1 он темнее порога и попал бы
        // в кандидаты.
        for (var step = 0; step < closingVoxels; step++)
        {
            head = Erode(dimensions, head);
        }

        return (head, open);
    }

    private static bool[] Dilate(VolumeDimensions dimensions, bool[] source)
    {
        var result = (bool[])source.Clone();

        for (var offset = 0; offset < source.Length; offset++)
        {
            if (source[offset])
            {
                continue;
            }

            foreach (var next in NeighboursOf(dimensions, offset))
            {
                if (source[next])
                {
                    result[offset] = true;
                    break;
                }
            }
        }

        return result;
    }

    private static bool[] Erode(VolumeDimensions dimensions, bool[] source)
    {
        var result = (bool[])source.Clone();

        for (var offset = 0; offset < source.Length; offset++)
        {
            if (!source[offset])
            {
                continue;
            }

            foreach (var next in NeighboursOf(dimensions, offset))
            {
                if (!source[next])
                {
                    result[offset] = false;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Оставляет наибольшую связную область: отдельные яркие пятна вне головы
    /// встречаются и не должны попадать в голову.
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

    /// <summary>
    /// Расстояние каждого отсчёта маски до ближайшего отсчёта вне её, мм.
    ///
    /// Точное евклидово расстояние с учётом шага по каждой оси: клинические
    /// серии бывают с толстыми срезами, и расстояние в отсчётах занизило бы
    /// его поперёк срезов втрое. Вне маски расстояние нулевое. Край кадра
    /// границей не считается: голова, обрезанная кадром, от этого мельче
    /// не становится.
    /// </summary>
    private static float[] DistanceToOutside(VolumeGrid grid, bool[] inside, CancellationToken cancellationToken)
    {
        var dimensions = grid.Dimensions;
        var squared = new float[inside.Length];

        for (var offset = 0; offset < inside.Length; offset++)
        {
            squared[offset] = inside[offset] ? float.PositiveInfinity : 0;
        }

        // Разделимое преобразование расстояния (Felzenszwalb, Huttenlocher):
        // одномерный проход по каждой оси по очереди даёт точный квадрат
        // евклидова расстояния.
        var columns = dimensions.Columns;
        var rows = dimensions.Rows;
        var slices = dimensions.Slices;
        var longest = Math.Max(columns, Math.Max(rows, slices));
        var parallel = new ParallelOptions { CancellationToken = cancellationToken };

        // Линии одного прохода не пересекаются, поэтому идут параллельно:
        // результат тот же, что при последовательном обходе, а голова
        // 256×256×150 обрабатывается за доли секунды, а не за секунду.
        Parallel.For(0, slices, parallel, () => new LineBuffers(longest), (slice, _, buffers) =>
        {
            for (var row = 0; row < rows; row++)
            {
                var start = Offset(dimensions, 0, row, slice);
                PassAlong(squared, start, 1, columns, grid.ColumnSpacingMillimetres, buffers);
            }

            return buffers;
        }, _ => { });

        Parallel.For(0, slices, parallel, () => new LineBuffers(longest), (slice, _, buffers) =>
        {
            for (var column = 0; column < columns; column++)
            {
                var start = Offset(dimensions, column, 0, slice);
                PassAlong(squared, start, columns, rows, grid.RowSpacingMillimetres, buffers);
            }

            return buffers;
        }, _ => { });

        Parallel.For(0, rows, parallel, () => new LineBuffers(longest), (row, _, buffers) =>
        {
            for (var column = 0; column < columns; column++)
            {
                var start = Offset(dimensions, column, row, 0);
                PassAlong(squared, start, columns * rows, slices, grid.SliceSpacingMillimetres, buffers);
            }

            return buffers;
        }, _ => { });

        for (var offset = 0; offset < squared.Length; offset++)
        {
            squared[offset] = MathF.Sqrt(squared[offset]);
        }

        return squared;
    }

    private sealed class LineBuffers(int length)
    {
        public double[] Line { get; } = new double[length];

        public double[] Output { get; } = new double[length];

        public int[] Hull { get; } = new int[length];

        public double[] Bounds { get; } = new double[length + 1];
    }

    private static void PassAlong(
        float[] squared,
        int start,
        int stride,
        int count,
        double spacing,
        LineBuffers buffers)
    {
        var line = buffers.Line;
        var output = buffers.Output;
        var hull = buffers.Hull;
        var bounds = buffers.Bounds;

        for (var index = 0; index < count; index++)
        {
            line[index] = squared[start + (index * stride)];
        }

        // Нижняя огибающая парабол (x - q)^2 + f(q) в миллиметрах.
        var parabolas = -1;

        for (var q = 0; q < count; q++)
        {
            if (double.IsPositiveInfinity(line[q]))
            {
                continue;
            }

            var position = q * spacing;
            double intersection;

            while (true)
            {
                if (parabolas < 0)
                {
                    intersection = double.NegativeInfinity;
                    break;
                }

                var other = hull[parabolas] * spacing;
                intersection = (line[q] + (position * position) - line[hull[parabolas]] - (other * other))
                    / (2 * (position - other));

                if (intersection > bounds[parabolas])
                {
                    break;
                }

                parabolas--;
            }

            parabolas++;
            hull[parabolas] = q;
            bounds[parabolas] = intersection;
            bounds[parabolas + 1] = double.PositiveInfinity;
        }

        if (parabolas < 0)
        {
            // Вне головы на этой линии ничего нет: расстояние по ней не
            // определено и остаётся бесконечным до проходов по другим осям.
            return;
        }

        var current = 0;

        for (var index = 0; index < count; index++)
        {
            var position = index * spacing;

            while (bounds[current + 1] < position)
            {
                current++;
            }

            var nearest = hull[current] * spacing;
            output[index] = ((position - nearest) * (position - nearest)) + line[hull[current]];
        }

        for (var index = 0; index < count; index++)
        {
            squared[start + (index * stride)] = (float)output[index];
        }
    }

    /// <summary>
    /// Соседи отсчёта по граням. Перечислитель — структура: раздувание,
    /// сжатие и заливки обходят каждый отсчёт объёма, и итератор с выделением
    /// памяти на каждый отсчёт вместе с последовательным расстоянием вдвое
    /// замедлял сегментацию.
    /// </summary>
    private static NeighbourOffsets NeighboursOf(VolumeDimensions dimensions, int offset) => new(dimensions, offset);

    private struct NeighbourOffsets
    {
        private readonly VolumeDimensions dimensions;
        private readonly int column;
        private readonly int row;
        private readonly int slice;
        private int index;

        public NeighbourOffsets(VolumeDimensions dimensions, int offset)
        {
            var plane = dimensions.Columns * dimensions.Rows;
            var remainder = offset % plane;

            this.dimensions = dimensions;
            slice = offset / plane;
            row = remainder / dimensions.Columns;
            column = remainder % dimensions.Columns;
            index = -1;
            Current = -1;
        }

        public int Current { get; private set; }

        public readonly NeighbourOffsets GetEnumerator() => this;

        public bool MoveNext()
        {
            while (++index < Neighbours.Length)
            {
                var (dc, dr, ds) = Neighbours[index];
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

                Current = Offset(dimensions, nextColumn, nextRow, nextSlice);
                return true;
            }

            return false;
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

    /// <summary>
    /// Дорастание маски T1 от тёмной сердцевины к границе частичного объёма.
    ///
    /// Граница частичного объёма — порог Оцу среди отсчётов головы темнее
    /// порога между тканями: он отделяет ликвор с примесью ткани от серого
    /// вещества. Маска растёт только в такие отсчёты и не дальше
    /// <see cref="BaselineSegmentationOptions.BoundaryGrowthMillimetres"/>
    /// по каждой оси.
    /// </summary>
    private static byte[] GrowIntoPartialVolume(
        IVoxelVolume volume,
        bool[] head,
        bool[] open,
        byte[] labels,
        double tissueThreshold,
        double csfThreshold,
        BaselineSegmentationOptions options,
        CancellationToken cancellationToken)
    {
        var grid = volume.Grid;
        var dimensions = grid.Dimensions;
        var darker = new bool[head.Length];

        for (var slice = 0; slice < dimensions.Slices; slice++)
        {
            for (var row = 0; row < dimensions.Rows; row++)
            {
                for (var column = 0; column < dimensions.Columns; column++)
                {
                    var offset = Offset(dimensions, column, row, slice);
                    darker[offset] = open[offset] && volume[column, row, slice] <= tissueThreshold;
                }
            }
        }

        if (!Array.Exists(labels, value => value != 0) || !Array.Exists(darker, value => value))
        {
            // Расти нечему или некуда: темнее ткани в голове без закрытия ничего
            // нет, и границы частичного объёма не существует.
            return labels;
        }

        // Порог частичного объёма не может быть строже порога сердцевины:
        // тогда дорастать было бы некуда, а не наоборот.
        var boundary = Math.Max(csfThreshold, IntensityThresholds.OtsuWithin(volume, darker));

        var steps = new[]
        {
            StepsWithin(options.BoundaryGrowthMillimetres, grid.ColumnSpacingMillimetres),
            StepsWithin(options.BoundaryGrowthMillimetres, grid.RowSpacingMillimetres),
            StepsWithin(options.BoundaryGrowthMillimetres, grid.SliceSpacingMillimetres),
        };

        var grown = labels;

        for (var step = 1; step <= steps.Max(); step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = (byte[])grown.Clone();

            for (var slice = 0; slice < dimensions.Slices; slice++)
            {
                for (var row = 0; row < dimensions.Rows; row++)
                {
                    for (var column = 0; column < dimensions.Columns; column++)
                    {
                        var offset = Offset(dimensions, column, row, slice);

                        if (grown[offset] != 0 || !head[offset] || volume[column, row, slice] > boundary)
                        {
                            continue;
                        }

                        var touches =
                            (step <= steps[0] && ((column > 0 && grown[offset - 1] != 0)
                                || (column < dimensions.Columns - 1 && grown[offset + 1] != 0)))
                            || (step <= steps[1] && ((row > 0 && grown[offset - dimensions.Columns] != 0)
                                || (row < dimensions.Rows - 1 && grown[offset + dimensions.Columns] != 0)))
                            || (step <= steps[2] && ((slice > 0 && grown[offset - (dimensions.Columns * dimensions.Rows)] != 0)
                                || (slice < dimensions.Slices - 1 && grown[offset + (dimensions.Columns * dimensions.Rows)] != 0)));

                        if (touches)
                        {
                            next[offset] = 1;
                        }
                    }
                }
            }

            grown = next;
        }

        return grown;
    }

    private static int StepsWithin(double millimetres, double spacing) =>
        spacing > 0 ? (int)Math.Floor((millimetres / spacing) + 1e-9) : 0;

    private static byte[] SelectVentricles(
        VolumeDimensions dimensions,
        byte[] candidate,
        float[] depth,
        BaselineSegmentationOptions options,
        out int rejected)
    {
        var visited = new bool[candidate.Length];
        var components = new List<List<int>>();

        var stack = new Stack<int>();

        for (var start = 0; start < candidate.Length; start++)
        {
            if (candidate[start] == 0 || visited[start])
            {
                continue;
            }

            components.Add(Flood(dimensions, candidate, visited, stack, start));
        }

        var accepted = components
            .Where(component => component.Count >= options.MinComponentVoxels)
            .Where(component => component.Min(offset => depth[offset]) >= options.MinDepthMillimetres)
            .OrderByDescending(component => component.Count)
            .Take(options.MaxComponents)
            .ToList();

        rejected = components.Count(component => component.Count >= options.MinComponentVoxels)
            - accepted.Count;

        var labels = new byte[candidate.Length];

        foreach (var offset in accepted.SelectMany(component => component))
        {
            labels[offset] = 1;
        }

        return labels;
    }

    private static List<int> Flood(
        VolumeDimensions dimensions,
        byte[] candidate,
        bool[] visited,
        Stack<int> stack,
        int start)
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
                if (visited[next] || candidate[next] == 0)
                {
                    continue;
                }

                visited[next] = true;
                stack.Push(next);
            }
        }

        return offsets;
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
}
