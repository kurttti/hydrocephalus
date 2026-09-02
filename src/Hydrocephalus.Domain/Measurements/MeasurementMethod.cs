using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Measurements;

/// <summary>
/// Единица измерения признака. Хранится кодом, а не строкой,
/// чтобы значение нельзя было записать без единицы или с произвольным текстом.
/// </summary>
public enum MeasurementUnit
{
    /// <summary>Единица не задана.</summary>
    Unspecified = 0,

    /// <summary>Миллиметр.</summary>
    Millimetre = 1,

    /// <summary>Миллилитр.</summary>
    Millilitre = 2,

    /// <summary>Градус.</summary>
    Degree = 3,

    /// <summary>Безразмерное отношение (например, индекс Эванса).</summary>
    Ratio = 4,
}

/// <summary>
/// Способ вычисления признака: имя, версия определения и требуемый уровень входа.
/// Версия обязательна — без неё значения разных сборок несопоставимы (docs/ml/README.md).
/// </summary>
public sealed record MeasurementMethod
{
    /// <summary>Стабильный код метода, например "evans_index".</summary>
    public required string Code { get; init; }

    /// <summary>Версия определения признака.</summary>
    public required string DefinitionVersion { get; init; }

    /// <summary>Минимальный уровень входа, при котором метод применим.</summary>
    public required AcquisitionTier RequiredTier { get; init; }

    /// <summary>
    /// Ссылка на алгоритм или публикацию. Пусто допустимо только для методов,
    /// определённых внутри проекта и описанных в docs/clinical.
    /// </summary>
    public string? Reference { get; init; }
}
