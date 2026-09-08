using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Загрузка объёма серии из шифрованной рабочей копии.
///
/// Адаптер порта <see cref="IVolumeSource"/>: слой инференса просит объём
/// по ссылке и не знает ни про сеансы, ни про шифрование, ни про DICOM.
/// Ссылка на рабочую копию адресует сеанс, а не каталог на диске — сеанс
/// владеет ключом, и без него файлы нечитаемы (ADR 0006).
/// </summary>
public sealed class WorkingCopyVolumeSource : IVolumeSource
{
    private readonly StudyImporter importer;

    /// <summary>Создаёт источник объёмов.</summary>
    /// <param name="importer">Импортёр, владеющий сеансами рабочих копий.</param>
    public WorkingCopyVolumeSource(StudyImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        this.importer = importer;
    }

    /// <summary>
    /// Загружает объём серии.
    /// </summary>
    /// <param name="volumeReference">Ссылка на рабочую копию.</param>
    /// <param name="pseudonymousSeriesId">Псевдонимный идентификатор серии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Воксельный объём серии.</returns>
    public async Task<IVoxelVolume> LoadAsync(
        string volumeReference,
        string pseudonymousSeriesId,
        CancellationToken cancellationToken) =>
        await DicomVolumeReader
            .LoadAsync(
                this.importer.SessionFor(volumeReference),
                pseudonymousSeriesId,
                cancellationToken)
            .ConfigureAwait(false);
}
