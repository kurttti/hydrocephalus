using Hydrocephalus.Domain.Predictions;

namespace Hydrocephalus.Domain.Provenance;

/// <summary>
/// Идентичность загруженного model package: что именно выдало результат.
/// Перечень поддерживаемых классов входит сюда, потому что прогноз без него
/// невозможно интерпретировать (инвариант доменного слоя).
/// </summary>
public sealed record ModelIdentity
{
    /// <summary>Имя модели.</summary>
    public required string Name { get; init; }

    /// <summary>Версия model package.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 пакета, проверенный при загрузке (ADR 0004).</summary>
    public required string PackageSha256 { get; init; }

    /// <summary>
    /// Классы, на которых модель обучена и валидирована. Вероятность может быть выдана
    /// только для класса из этого перечня.
    /// </summary>
    public required IReadOnlyList<DiagnosticClass> SupportedClasses { get; init; }

    /// <summary>Версия калибровки, применённой к выходу классификатора.</summary>
    public string? CalibrationVersion { get; init; }
}
