using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Замечание к конкретной серии, выявленное на приёмке.
/// </summary>
/// <param name="PseudonymousSeriesId">Псевдонимный идентификатор серии.</param>
/// <param name="Issue">Замечание.</param>
public readonly record struct SeriesFinding(string PseudonymousSeriesId, QualityIssue Issue);

/// <summary>
/// Результат обхода каталога: что удалось разобрать, что отклонено и к чему есть замечания.
/// Отклонённые файлы не прерывают импорт — реальный экспорт содержит посторонние файлы.
/// </summary>
public sealed record DicomScanResult
{
    /// <summary>Разобранные исследования.</summary>
    public required IReadOnlyList<ImagingStudy> Studies { get; init; }

    /// <summary>Файлы, не принятые к обработке, с машинно-читаемыми кодами.</summary>
    public required IReadOnlyList<ImportRejection> Rejections { get; init; }

    /// <summary>Замечания к принятым сериям.</summary>
    public required IReadOnlyList<SeriesFinding> Findings { get; init; }
}
