using System.Globalization;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Сегментация желудочков обученной моделью, встроенная в конвейер (ADR 0009).
///
/// Отличие от <see cref="OnnxVentricleSegmentation"/> в том, что та размечает
/// приведённый куб и ничего не знает ни про серию, ни про отказы. Здесь к ней
/// добавлено всё, без чего её нельзя пускать в клинический путь: приведение
/// объёма, перенос маски назад в сетку серии, проверки правдоподобия и пометка
/// происхождения.
///
/// **Проверки правдоподобия обязательны, а не предусмотрительны.** Модель на
/// непригодном входе возвращает не ошибку, а малое правдоподобное число: на двух
/// клинических сериях она дала 210 и 301 воксель — 0,3 и 0,4 мл. Без порога
/// такая маска вышла бы из конвейера как результат, а индекс отказал бы с
/// причиной «не найдены передние рога», уведя разбор не туда. Это уже
/// происходило с пороговым методом (`docs/data/README.md`).
///
/// Порог взят по измерению, а не назначен: у 150 здоровых серий IXI наименьший
/// объём, выданный моделью, — 6,6 мл, а два известных срыва дали 0,3 и 0,4 мл.
/// Существующие 5 мл
/// <see cref="BaselineSegmentationOptions.MinVentricleMillilitres"/> лежат в этом
/// разрыве с запасом в обе стороны.
/// </summary>
public sealed class ModelVentricleSegmentation : IVentricleSegmentation
{
    /// <summary>Код способа получения маски.</summary>
    public const string MethodCode = "ventricles-model";

    /// <summary>
    /// Наименьшее покрытие по оси срезов, при котором модель применима, мм.
    ///
    /// Прицельный блок модель размечает молча и неверно: желудочки в нём
    /// обрезаны, и она возвращает правдоподобное малое число вместо отказа.
    /// Значение то же, что у входного контроля, где оно пока лишь
    /// предупреждение; здесь — отказ. Ни одна из серий, на которых модель
    /// измеряла верно, под него не попадает: самая короткая покрывает 115 мм.
    /// </summary>
    public const double MinCoverageMillimetres = SeriesGeometry.MinVentricleCoverageMillimetres;

    private readonly IConformedVolumeLabelling model;
    private readonly BaselineSegmentationOptions options;

    /// <summary>
    /// Создаёт путь модели.
    /// </summary>
    /// <param name="model">Открытая модель — разметка приведённого куба.</param>
    /// <param name="modelVersion">Версия пакета модели, как её показывают врачу.</param>
    /// <param name="options">Пороги правдоподобия; по умолчанию те же, что у порогового пути.</param>
    public ModelVentricleSegmentation(
        IConformedVolumeLabelling model,
        string modelVersion,
        BaselineSegmentationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);

        this.model = model;
        this.options = options ?? new BaselineSegmentationOptions();
        this.Provenance = $"{MethodCode}/{modelVersion}";
    }

    /// <inheritdoc />
    public string Provenance { get; }

    /// <inheritdoc />
    public VentricleSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        // Псевдоним серии здесь не нужен: разметка зависит от объёма, а не от
        // того, чей он. Он есть в договоре ради кэша, который и держит ответ.
        _ = pseudonymousSeriesId;

        var grid = volume.Grid;

        // Взвешенность проверяется до работы, а не после: модель обучена и
        // проверена на T1, а на T2 ликвор яркий, то есть обратен тому, что она
        // видела. Отдавать такую маску молча нельзя, и подменять путь здесь —
        // тоже: выбор способа принадлежит вызывающему.
        if (weighting != SeriesWeighting.T1)
        {
            return this.Refused(grid, NotT1(weighting));
        }

        var coverage = grid.Dimensions.Slices * grid.SliceSpacingMillimetres;

        if (coverage < MinCoverageMillimetres)
        {
            return this.Refused(grid, TargetedBlock(coverage));
        }

        var conformed = VolumeConforming.Conform(volume, cancellationToken);
        var labelled = this.model.Segment(conformed, progress: null, cancellationToken);
        var labels = VolumeConforming.ProjectBack(labelled, volume, cancellationToken);

        var selected = 0L;

        foreach (var label in labels)
        {
            if (label != 0)
            {
                selected++;
            }
        }

        if (selected == 0)
        {
            return this.Refused(grid, NothingFound());
        }

        var voxelMillilitres = grid.ColumnSpacingMillimetres * grid.RowSpacingMillimetres
            * grid.SliceSpacingMillimetres / 1000.0;
        var millilitres = selected * voxelMillilitres;

        if (millilitres < this.options.MinVentricleMillilitres)
        {
            return this.Refused(grid, TooSmall(millilitres));
        }

        return new VentricleSegmentationResult(
            Mask(grid, labels, this.model.LabelMapVersion),
            [SingleView()],

            // Достоверность задаётся здесь и остаётся «сомнительно», пока модель
            // не проверена против ручной разметки: ADR 0009 требует этого прямо,
            // а Dice 0,9998 против исследовательского контура говорит о верности
            // переноса, не о верности самой разметки.
            MeasurementQuality.Questionable);
    }

    private static VoxelMask Mask(VolumeGrid grid, byte[] labels, string labelMapVersion) => new(
        grid,
        new LabelMap
        {
            Version = labelMapVersion,
            Labels = [OnnxVentricleSegmentation.VentricularSystem],
        },
        labels);

    private static QualityIssue Issue(
        QualityIssueCode code,
        QualityIssueSeverity severity,
        params (string Key, string Value)[] parameters) => new()
        {
            Code = code,
            Severity = severity,
            Parameters = parameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value,
            StringComparer.Ordinal),
        };

    /// <summary>
    /// Ослабленный режим: работает один аксиальный вид из трёх.
    ///
    /// Замечание, а не отказ, и оно сопровождает каждый успешный результат.
    /// Полная модель складывает три вида и требует изотропного объёма около
    /// миллиметра — в клинической выборке таких серий 4 из 26.
    /// </summary>
    private static QualityIssue SingleView() => Issue(
        QualityIssueCode.OutOfDistribution,
        QualityIssueSeverity.Warning,
        ("reason", "ventricleModelSingleAxialView"));

    private static QualityIssue NotT1(SeriesWeighting weighting) => Issue(
        QualityIssueCode.OutOfDistribution,
        QualityIssueSeverity.Blocking,
        ("reason", "ventricleModelNeedsT1"),
        ("weighting", weighting.ToString()));

    private static QualityIssue TargetedBlock(double coverage) => Issue(
        QualityIssueCode.HeadTruncated,
        QualityIssueSeverity.Blocking,
        ("reason", "ventricleModelNeedsHeadCoverage"),
        ("coverageMm", Format(coverage)),
        ("minMm", Format(MinCoverageMillimetres)));

    private static QualityIssue NothingFound() => Issue(
        QualityIssueCode.InconsistentGeometry,
        QualityIssueSeverity.Blocking,
        ("reason", "ventricularSystemNotFound"));

    private static QualityIssue TooSmall(double millilitres) => Issue(
        QualityIssueCode.InconsistentGeometry,
        QualityIssueSeverity.Blocking,
        ("reason", "ventricularSystemImplausiblySmall"),
        ("millilitres", Format(millilitres)));

    private static string Format(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Отказ: маска пуста, замечание названо, достоверность — непригодно.
    ///
    /// Разбора решения для врача здесь нет. У порогового пути он показывает,
    /// какой ликвор метод отбросил и почему; у модели промежуточного решения
    /// такого рода не существует — она выдаёт разметку целиком, и показывать
    /// было бы нечего.
    /// </summary>
    private VentricleSegmentationResult Refused(VolumeGrid grid, QualityIssue reason) => new(
        Mask(
            grid,
            new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices],
            this.model.LabelMapVersion),
        [reason],
        MeasurementQuality.Unreliable);
}
