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

    /// <summary>
    /// Принятые файлы каждой серии, по псевдонимному идентификатору серии.
    ///
    /// Внутреннее, а не открытое: пути источника содержат фамилии пациентов,
    /// и наружу из инфраструктуры они не выходят. Нужны импорту, чтобы записать
    /// ровно те экземпляры, которые принял разбор, — без повторов и без второго
    /// обхода источника.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> SourceFilesBySeries { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>
    /// Перечисляет причины отказов с числом файлов по каждой.
    ///
    /// Нужно отказу «в источнике нет читаемого исследования»: без причин папка
    /// компьютерной томографии отвергается теми же словами, что папка с шестью
    /// снимками экрана, и врач не может отличить «не тот аппарат» от «здесь
    /// ничего нет». Печатаются только коды и числа: пути содержат фамилии.
    /// </summary>
    /// <returns>Строка вида «NotMagneticResonance 443»; пусто, если отказов не было.</returns>
    public string DescribeRejections() =>
        string.Join(
            ", ",
            this.Rejections
                .GroupBy(rejection => rejection.Code)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key.ToString(), StringComparer.Ordinal)
                .Select(group => $"{group.Key} {group.Count()}"));
}
