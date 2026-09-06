using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Запись манифеста датасета для исследовательского контура.
///
/// Отдельный порт, а не часть хранилища отчётов: отчёт остаётся у врача,
/// а манифест уходит наружу, в обучение. Разные направления и разные правила,
/// и объединять их значило бы позволить одному вызову подменить другой.
/// </summary>
public interface IDatasetManifestStore
{
    /// <summary>
    /// Записывает манифест.
    /// </summary>
    /// <param name="studies">Исследования, попадающие в манифест.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ссылка на записанный манифест в терминах инфраструктуры.</returns>
    Task<string> WriteAsync(IReadOnlyList<ImagingStudy> studies, CancellationToken cancellationToken);
}
