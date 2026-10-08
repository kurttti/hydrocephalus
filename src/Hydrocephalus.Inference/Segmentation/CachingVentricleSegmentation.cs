using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Помнит разметку последней серии.
///
/// Одну и ту же серию размечают дважды: просмотрщик — чтобы врач увидел маску и
/// отрезки на снимке, конвейер — чтобы посчитать признаки в отчёт. С пороговым
/// методом это стоило секунды и внимания не требовало. С моделью каждый раз —
/// около минуты, и открытие исследования встало бы на это время дважды.
///
/// Хранится ровно один ответ, а не словарь: врач работает с одной серией за раз,
/// а маска — это байт на воксель, то есть мегабайты. Словарь на всё
/// исследование съел бы память ради случая, которого в работе нет.
///
/// Ответ привязан к псевдониму серии. Один псевдоним — одна серия и один и тот
/// же объём, поэтому возврат запомненного ответа не меняет результата. Отказ
/// кэшируется наравне с успехом: он тоже стоил полной разметки.
/// </summary>
public sealed class CachingVentricleSegmentation : IVentricleSegmentation
{
    private readonly IVentricleSegmentation inner;
    private readonly object gate = new();

    private string? seriesId;
    private VentricleSegmentationResult remembered;

    /// <summary>
    /// Оборачивает способ разметки.
    /// </summary>
    /// <param name="inner">Способ, ответы которого запоминаются.</param>
    public CachingVentricleSegmentation(IVentricleSegmentation inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        this.inner = inner;
    }

    /// <inheritdoc />
    public string Provenance => this.inner.Provenance;

    /// <inheritdoc />
    public VentricleSegmentationResult Segment(
        IVoxelVolume volume,
        SeriesWeighting weighting,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonymousSeriesId);

        lock (this.gate)
        {
            if (string.Equals(this.seriesId, pseudonymousSeriesId, StringComparison.Ordinal))
            {
                return this.remembered;
            }
        }

        // Разметка идёт вне замка. Два одновременных запроса одной новой серии
        // посчитают её дважды, и это дешевле обратного: замок, удерживаемый
        // минуту, подвесил бы и просмотрщик, и конвейер. Результат от повтора
        // не меняется — метод детерминирован.
        var result = this.inner.Segment(volume, weighting, pseudonymousSeriesId, cancellationToken);

        lock (this.gate)
        {
            this.seriesId = pseudonymousSeriesId;
            this.remembered = result;
        }

        return result;
    }
}
