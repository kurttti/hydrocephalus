using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Доступ к воксельным данным рабочей копии.
///
/// Порт нужен слою инференса: измерять он умеет, а читать DICOM — нет и не должен.
/// Реализация живёт в Infrastructure и знает про шифрование, сеансы и формат
/// файлов; слой инференса получает готовый объём и не знает, откуда тот взялся
/// (docs/architecture/README.md).
///
/// Возвращается объём, а не буфер: контракт обязан пережить вынос инференса
/// в отдельный процесс (ADR 0002), и указатели на разделяемую память границу
/// не пересекают.
/// </summary>
public interface IVolumeSource
{
    /// <summary>
    /// Загружает объём серии из рабочей копии.
    /// </summary>
    /// <param name="volumeReference">Ссылка на рабочую копию.</param>
    /// <param name="pseudonymousSeriesId">Псевдонимный идентификатор серии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Воксельный объём серии.</returns>
    Task<IVoxelVolume> LoadAsync(
        string volumeReference,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken);
}
