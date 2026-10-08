namespace Hydrocephalus.Inference.Segmentation;

/// <summary>
/// Разметка приведённого куба: то единственное, что делает сеть.
///
/// Выделено в договор по двум причинам. ADR 0002 требует держать инференс за
/// интерфейсом, чтобы вынести его в отдельный процесс, не переделывая
/// вызывающих. И проверить всё, что вокруг сети — отказы, пороги, перенос маски
/// назад в сетку серии, — нужно без весов: веса в репозиторий не входят, CI их
/// не качает, и тесты, требующие их, включаются переменной среды.
/// </summary>
public interface IConformedVolumeLabelling
{
    /// <summary>Версия карты меток разметки.</summary>
    string LabelMapVersion { get; }

    /// <summary>
    /// Размечает приведённый куб.
    /// </summary>
    /// <param name="conformed">
    /// Куб 256 с шагом 1 мм, укладка LIA, яркость 0–255 —
    /// как его отдаёт <see cref="VolumeConforming.Conform"/>.
    /// </param>
    /// <param name="progress">Доля размеченных срезов, если нужна.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Метки куба: 1 — желудочки, 0 — остальное.</returns>
    byte[] Segment(
        byte[] conformed,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
