using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Серия, не попавшая в рабочую копию, и замечания, из-за которых она не попала.
///
/// Отбрасывать серию молча нельзя. Врач, открывший исследование, видит одну
/// серию и не может отличить «в исследовании была одна серия» от «было восемь,
/// семь отброшено»: второе — повод пересмотреть выгрузку, а не работать
/// с остатком. Сама серия при этом на диск не пишется — здесь только её
/// описание и причина.
/// </summary>
public sealed record ExcludedSeries
{
    /// <summary>Описание отброшенной серии.</summary>
    public required ImagingSeries Series { get; init; }

    /// <summary>Замечания, из-за которых серия не попала в рабочую копию.</summary>
    public required IReadOnlyList<QualityIssue> Issues { get; init; }
}

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

    /// <summary>
    /// Серии исследования, не попавшие в рабочую копию, с причинами.
    ///
    /// Пустой список означает, что отброшено ничего не было, — это утверждение,
    /// а не отсутствие сведений.
    /// </summary>
    public IReadOnlyList<ExcludedSeries> ExcludedSeries { get; init; } = [];
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
