using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Отказывает всему, называя причину. Ставится вместо разметки, когда
/// установленный пакет модели не прошёл проверку.
///
/// Нужна потому, что откат на пороговый путь в этом случае **запрещён**. ADR
/// 0008: «если активный пакет несовместим с обновлённым приложением,
/// приложение не подставляет молча другой пакет: анализ блокируется с явным
/// сообщением до решения администратора». Причина клиническая — врач не должен
/// получить число, посчитанное не тем способом, которым, по экрану, считает
/// программа.
///
/// Отсутствие пакета вовсе — другой случай и блокировкой не является:
/// приложение поставляется без модели (ADR 0008 — установщик её не приносит) и
/// до установки работает пороговым путём, о чём и сообщает.
/// </summary>
public sealed class BlockedVentricleSegmentation : IVentricleSegmentation
{
    private readonly QualityIssue reason;

    /// <summary>
    /// Создаёт блокировку.
    /// </summary>
    /// <param name="rejection">Причина отказа пакета, как её назвала проверка.</param>
    /// <param name="detail">Уточнение к причине: имя файла или версия. Без PHI.</param>
    public BlockedVentricleSegmentation(string rejection, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejection);

        this.reason = new QualityIssue
        {
            Code = QualityIssueCode.OutOfDistribution,
            Severity = QualityIssueSeverity.Blocking,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "modelPackageRefused",
                ["rejection"] = rejection,
                ["detail"] = detail,
            },
        };
    }

    /// <inheritdoc />
    public string Provenance => "ventricles-blocked";

    /// <inheritdoc />
    public VentricleSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);

        var grid = volume.Grid;

        return new VentricleSegmentationResult(
            new VoxelMask(
                grid,
                new LabelMap { Version = this.Provenance, Labels = [] },
                new byte[grid.Dimensions.Columns * grid.Dimensions.Rows * grid.Dimensions.Slices]),
            [this.reason],
            MeasurementQuality.Unreliable);
    }
}
