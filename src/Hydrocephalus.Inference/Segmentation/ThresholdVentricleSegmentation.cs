using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Пороговый путь за общим договором.
///
/// Обёртка, а не вторая реализация: сам метод остаётся статическим и проверяется
/// своими тестами напрямую. Нужна затем, что способов стало два, и выбирать
/// между ними должен состав приложения, а не условие внутри конвейера.
/// </summary>
public sealed class ThresholdVentricleSegmentation : IVentricleSegmentation
{
    /// <summary>Код способа получения маски.</summary>
    public const string MethodCode = "ventricles-threshold";

    private readonly BaselineSegmentationOptions? options;

    /// <summary>
    /// Создаёт путь.
    /// </summary>
    /// <param name="options">Пороги метода; по умолчанию принятые в нём.</param>
    public ThresholdVentricleSegmentation(BaselineSegmentationOptions? options = null) =>
        this.options = options;

    /// <inheritdoc />
    public string Provenance =>
        $"{MethodCode}/{BaselineVentricleSegmentation.LabelMapVersion}";

    /// <inheritdoc />
    public VentricleSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        _ = pseudonymousSeriesId;

        // Нераспознанная взвешенность — отказ, а не исключение. Сам метод здесь
        // бросает, и это верно для прямого вызова: направление порога угадать
        // нельзя, ошибка выделит ткань вместо ликвора. Но за общим договором
        // неизвестная взвешенность — рядовой исход разбора серии, и конвейеру
        // полагается назвать его причиной наравне с прочими.
        if (weighting is not (SeriesWeighting.T1 or SeriesWeighting.T2 or SeriesWeighting.Flair))
        {
            var grid = volume.Grid;

            return new VentricleSegmentationResult(
                new VoxelMask(
                    grid,
                    new LabelMap
                    {
                        Version = BaselineVentricleSegmentation.LabelMapVersion,
                        Labels = [BaselineVentricleSegmentation.VentricularSystem],
                    },
                    new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]),
                [
                    new QualityIssue
                    {
                        Code = QualityIssueCode.InconsistentGeometry,
                        Severity = QualityIssueSeverity.Blocking,
                        Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["reason"] = "seriesWeightingUnknown",
                        },
                    },
                ],
                MeasurementQuality.Unreliable);
        }

        return BaselineVentricleSegmentation.Segment(
            volume, weighting, this.options, cancellationToken);
    }
}
