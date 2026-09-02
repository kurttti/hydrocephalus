using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Рабочая копия исследования: деидентифицированное исследование и ссылка на воксельные данные
/// в защищённом каталоге. Домен не знает, файл это или запись хранилища.
/// </summary>
public sealed record WorkingCopy
{
    /// <summary>Деидентифицированное исследование.</summary>
    public required ImagingStudy Study { get; init; }

    /// <summary>Ссылка на воксельные данные рабочей копии.</summary>
    public required string VolumeReference { get; init; }
}

/// <summary>
/// Импорт исследования: чтение источника, карантин, деидентификация и создание рабочей копии.
/// Реализация живёт в Infrastructure и скрывает DICOM-библиотеку (ADR 0003).
/// </summary>
public interface IStudyImporter
{
    /// <summary>
    /// Импортирует исследование и создаёт рабочую копию.
    /// </summary>
    /// <param name="sourceReference">Ссылка на источник в терминах инфраструктуры.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Рабочая копия исследования.</returns>
    Task<WorkingCopy> ImportAsync(string sourceReference, CancellationToken cancellationToken);
}
