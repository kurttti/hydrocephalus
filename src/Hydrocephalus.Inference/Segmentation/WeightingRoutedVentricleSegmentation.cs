using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Направляет серию тому способу, который на ней применим.
///
/// Модель обучена и проверена на T1 (ADR 0009, карточка модели). На T2 ликвор
/// яркий — обратен тому, что она видела, — и её разметка там недостоверна. Но
/// отдавать T2 отказ только потому, что установлена модель, тоже неверно:
/// пороговый путь знает направление порога для T2 и FLAIR и до появления модели
/// работал на них. Смена способа не должна отнимать то, что уже считалось.
///
/// Поэтому выбор делается не на приложение, а на серию: T1 — модели, остальное —
/// пороговому пути, как было.
/// </summary>
public sealed class WeightingRoutedVentricleSegmentation : IVentricleSegmentation
{
    private readonly IVentricleSegmentation forT1;
    private readonly IVentricleSegmentation forOthers;

    /// <summary>
    /// Создаёт маршрутизацию.
    /// </summary>
    /// <param name="forT1">Способ для T1.</param>
    /// <param name="forOthers">Способ для остальных взвешенностей.</param>
    public WeightingRoutedVentricleSegmentation(
        IVentricleSegmentation forT1,
        IVentricleSegmentation forOthers)
    {
        ArgumentNullException.ThrowIfNull(forT1);
        ArgumentNullException.ThrowIfNull(forOthers);

        this.forT1 = forT1;
        this.forOthers = forOthers;
    }

    /// <summary>
    /// Называет оба способа.
    ///
    /// Оба, а не действующий на данной серии: это строка о том, как собран
    /// конвейер, и она одна на прогон (<c>PipelineIdentity</c>). Чем получена
    /// отдельная маска, сказано в самой маске — версией её карты меток.
    ///
    /// Латиницей, как и прочие коды: строка попадает в отчёт и сверяется между
    /// сборками, то есть читается не только человеком.
    /// </summary>
    public string Provenance => $"t1:{this.forT1.Provenance}|other:{this.forOthers.Provenance}";

    /// <inheritdoc />
    public VentricleSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken = default)
    {
        var chosen = weighting == SeriesWeighting.T1 ? this.forT1 : this.forOthers;

        return chosen.Segment(volume, weighting, pseudonymousSeriesId, cancellationToken);
    }
}
