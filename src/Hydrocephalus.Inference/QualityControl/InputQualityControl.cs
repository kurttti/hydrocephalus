using System.Globalization;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Inference.QualityControl;

/// <summary>
/// Входной контроль качества по геометрии и метаданным серии.
///
/// Это первая половина этапа «QC/OOD входа». Здесь проверяется только то, что видно
/// в геометрии: непротиворечивость, поддерживаемый размер вокселя, поле обзора,
/// уровень входа и характер серии. Проверки, которым нужны воксели — двигательные
/// артефакты, фактическое обрезание головы, выход за пределы обучающего
/// распределения, — требуют загруженного объёма и model package и появятся вместе
/// с ними (M4). Их отсутствие названо явно, чтобы «QC пройден» не читалось как
/// «изображение проверено целиком».
///
/// Отказ формируется не здесь: контроль возвращает список проблем, а решение
/// принимает <see cref="QualityAssessment"/> по их составу — иначе можно было бы
/// объявить QC пройденным при блокирующей проблеме.
/// </summary>
public sealed class InputQualityControl
{
    private readonly InputQualityLimits limits;

    /// <summary>Создаёт контроль с границами по умолчанию.</summary>
    public InputQualityControl()
        : this(new InputQualityLimits())
    {
    }

    /// <summary>Создаёт контроль с заданными границами.</summary>
    /// <param name="limits">Границы пригодности входа.</param>
    public InputQualityControl(InputQualityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        this.limits = limits;
    }

    /// <summary>
    /// Проверяет серию, выбранную для анализа.
    /// </summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <returns>Результат контроля качества.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если запрос называет серию, которой нет в исследовании.
    /// </exception>
    public QualityAssessment Evaluate(AnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var series = request.Study.Series
            .FirstOrDefault(item => string.Equals(
                item.PseudonymousSeriesId,
                request.PseudonymousSeriesId,
                StringComparison.Ordinal));

        if (series is null)
        {
            // Не проблема качества, а рассогласование запроса и рабочей копии:
            // молчаливо вернуть «чистый QC» здесь означало бы допустить анализ
            // неизвестно чего.
            throw new DomainRuleViolationException(
                "The analysis request names a series that the study does not contain.");
        }

        return new QualityAssessment { Issues = [.. this.Inspect(series)] };
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

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private IEnumerable<QualityIssue> Inspect(ImagingSeries series)
    {
        var geometry = series.Geometry;

        if (!geometry.IsWellFormed)
        {
            // Дальнейшие проверки опираются на размеры и направляющие косинусы:
            // на противоречивой геометрии они дали бы правдоподобные, но
            // бессмысленные значения.
            yield return Issue(
                QualityIssueCode.InconsistentGeometry,
                QualityIssueSeverity.Blocking);

            yield break;
        }

        if (series.IsContrastEnhanced)
        {
            // Проверка намеренно дублирует отбор серии в сценарии анализа:
            // контроль качества не должен зависеть от того, что кто-то раньше
            // отфильтровал вход правильно.
            yield return Issue(
                QualityIssueCode.ContrastEnhancedSeries,
                QualityIssueSeverity.Blocking);
        }

        if (series.Tier == AcquisitionTier.Unusable)
        {
            yield return Issue(
                QualityIssueCode.AcquisitionTierTooLow,
                QualityIssueSeverity.Blocking);
        }

        foreach (var issue in this.InspectVoxelGeometry(geometry))
        {
            yield return issue;
        }

        foreach (var issue in this.InspectFieldOfView(geometry, series.Tier))
        {
            yield return issue;
        }
    }

    private IEnumerable<QualityIssue> InspectVoxelGeometry(SeriesGeometry geometry)
    {
        var thickness = geometry.SliceThicknessMillimetres;

        if (thickness > this.limits.MaxSliceThicknessMillimetres
            || thickness < this.limits.MinSliceThicknessMillimetres)
        {
            yield return Issue(
                QualityIssueCode.UnsupportedVoxelGeometry,
                QualityIssueSeverity.Blocking,
                ("parameter", "sliceThickness"),
                ("valueMm", Format(thickness)),
                ("maxMm", Format(this.limits.MaxSliceThicknessMillimetres)));
        }

        var row = geometry.PixelSpacing.RowMillimetres;
        var column = geometry.PixelSpacing.ColumnMillimetres;

        foreach (var spacing in new[] { row, column })
        {
            if (spacing > this.limits.MaxInPlaneSpacingMillimetres
                || spacing < this.limits.MinInPlaneSpacingMillimetres)
            {
                yield return Issue(
                    QualityIssueCode.UnsupportedVoxelGeometry,
                    QualityIssueSeverity.Blocking,
                    ("parameter", "pixelSpacing"),
                    ("valueMm", Format(spacing)),
                    ("maxMm", Format(this.limits.MaxInPlaneSpacingMillimetres)));
            }
        }

        if (geometry.HasSliceGap)
        {
            // Замечание повторяет проверку приёмки намеренно: замечания импорта
            // не входят в запрос на анализ, а объём, посчитанный по толщине среза
            // при наличии зазора, занижен ровно на долю неполученной ткани.
            yield return Issue(
                QualityIssueCode.UnsupportedVoxelGeometry,
                QualityIssueSeverity.Warning,
                ("parameter", "sliceGap"),
                ("spacingMm", Format(geometry.SliceSpacingMillimetres)),
                ("thicknessMm", Format(thickness)));
        }

        var anisotropy = Math.Max(row, column) / Math.Min(row, column);

        if (anisotropy > this.limits.MaxInPlaneAnisotropy)
        {
            // Единственный результат базового уровня входа — линейные измерения,
            // и сильно неквадратный пиксель искажает именно их.
            yield return Issue(
                QualityIssueCode.UnsupportedVoxelGeometry,
                QualityIssueSeverity.Blocking,
                ("parameter", "inPlaneAnisotropy"),
                ("value", Format(anisotropy)),
                ("max", Format(this.limits.MaxInPlaneAnisotropy)));
        }
    }

    private IEnumerable<QualityIssue> InspectFieldOfView(SeriesGeometry geometry, AcquisitionTier tier)
    {
        var width = geometry.Dimensions.Columns * geometry.PixelSpacing.ColumnMillimetres;
        var height = geometry.Dimensions.Rows * geometry.PixelSpacing.RowMillimetres;

        if (width < this.limits.MinInPlaneExtentMillimetres
            || height < this.limits.MinInPlaneExtentMillimetres)
        {
            // Проверяется только поле обзора: голова взрослого в такое поле не
            // помещается физически. Голову, смещённую за край достаточно большого
            // поля, по метаданным не увидеть — это работа проверки по вокселям.
            yield return Issue(
                QualityIssueCode.HeadTruncated,
                QualityIssueSeverity.Blocking,
                ("parameter", "fieldOfView"),
                ("widthMm", Format(width)),
                ("heightMm", Format(height)),
                ("minMm", Format(this.limits.MinInPlaneExtentMillimetres)));
        }

        if (tier != AcquisitionTier.Extended)
        {
            // Линейные измерения выполняются на одном срезе, поэтому неполное
            // покрытие по оси срезов базовому уровню не мешает.
            yield break;
        }

        var coverage = geometry.Dimensions.Slices * geometry.SliceThicknessMillimetres;

        if (coverage < this.limits.MinVolumeCoverageMillimetres)
        {
            // Замечание, а не отказ: объёмные признаки на неполном покрытии
            // недостоверны, но линейные измерения по такой серии возможны.
            yield return Issue(
                QualityIssueCode.HeadTruncated,
                QualityIssueSeverity.Warning,
                ("parameter", "sliceCoverage"),
                ("valueMm", Format(coverage)),
                ("minMm", Format(this.limits.MinVolumeCoverageMillimetres)));
        }
    }
}
