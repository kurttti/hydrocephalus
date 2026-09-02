using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Domain.Measurements;

/// <summary>
/// Флаг качества измерения. Значение может быть вычислено, но не пригодно для интерпретации.
/// </summary>
public enum MeasurementQuality
{
    /// <summary>Качество не оценено.</summary>
    Unspecified = 0,

    /// <summary>Измерение надёжно.</summary>
    Reliable = 1,

    /// <summary>Измерение сомнительно и требует проверки врачом.</summary>
    Questionable = 2,

    /// <summary>Измерение непригодно для интерпретации.</summary>
    Unreliable = 3,
}

/// <summary>
/// Допустимый диапазон значений признака.
/// </summary>
/// <param name="Minimum">Нижняя граница включительно.</param>
/// <param name="Maximum">Верхняя граница включительно.</param>
public readonly record struct MeasurementRange(double Minimum, double Maximum)
{
    /// <summary>Проверяет попадание значения в диапазон.</summary>
    /// <param name="value">Проверяемое значение.</param>
    /// <returns><see langword="true"/>, если значение внутри диапазона.</returns>
    public bool Contains(double value) => value >= Minimum && value <= Maximum;
}

/// <summary>
/// Вычисленный количественный признак. Состав полей задан docs/ml/README.md:
/// значение неотделимо от метода, версии определения, единицы, качества,
/// допустимого диапазона и масок, из которых оно получено.
/// </summary>
public sealed record Biomarker
{
    /// <summary>Метод вычисления вместе с версией определения.</summary>
    public required MeasurementMethod Method { get; init; }

    /// <summary>Значение признака.</summary>
    public required double Value { get; init; }

    /// <summary>Единица измерения.</summary>
    public required MeasurementUnit Unit { get; init; }

    /// <summary>Флаг качества измерения.</summary>
    public required MeasurementQuality Quality { get; init; }

    /// <summary>Допустимый диапазон значений.</summary>
    public required MeasurementRange AllowedRange { get; init; }

    /// <summary>Метки сегментации, из которых получено значение.</summary>
    public IReadOnlyList<AnatomicalLabel> SourceLabels { get; init; } = [];

    /// <summary>Признак выхода значения за допустимый диапазон.</summary>
    public bool IsOutOfRange => !AllowedRange.Contains(Value);

    /// <summary>
    /// Создаёт признак, проверяя, что уровень входа достаточен для выбранного метода.
    /// Объёмный признак не может быть получен из 2D-серии, и это должно быть невозможно
    /// выразить, а не отлавливаться на review.
    /// </summary>
    /// <param name="method">Метод вычисления.</param>
    /// <param name="availableTier">Фактический уровень входа исследования.</param>
    /// <param name="value">Значение признака.</param>
    /// <param name="unit">Единица измерения.</param>
    /// <param name="quality">Флаг качества.</param>
    /// <param name="allowedRange">Допустимый диапазон.</param>
    /// <param name="sourceLabels">Метки, из которых получено значение.</param>
    /// <returns>Признак с проверенными предусловиями.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если уровень входа ниже требуемого методом или единица не задана.
    /// </exception>
    public static Biomarker Create(
        MeasurementMethod method,
        AcquisitionTier availableTier,
        double value,
        MeasurementUnit unit,
        MeasurementQuality quality,
        MeasurementRange allowedRange,
        IReadOnlyList<AnatomicalLabel>? sourceLabels = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (unit == MeasurementUnit.Unspecified)
        {
            throw new DomainRuleViolationException(
                $"Biomarker '{method.Code}' must declare a unit.");
        }

        if (availableTier < method.RequiredTier)
        {
            throw new DomainRuleViolationException(
                $"Biomarker '{method.Code}' requires acquisition tier {method.RequiredTier}, "
                + $"but the study provides {availableTier}.");
        }

        return new Biomarker
        {
            Method = method,
            Value = value,
            Unit = unit,
            Quality = quality,
            AllowedRange = allowedRange,
            SourceLabels = sourceLabels ?? [],
        };
    }
}
