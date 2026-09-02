namespace Hydrocephalus.Domain.Predictions;

/// <summary>
/// Оценка неопределённости прогноза. Отчёт MVP обязан показывать доверительный интервал
/// или иную оценку неопределённости (README.md, раздел о безопасном результате).
/// </summary>
public sealed record Uncertainty
{
    /// <summary>Нижняя граница интервала для вероятности наиболее вероятного класса.</summary>
    public required double LowerBound { get; init; }

    /// <summary>Верхняя граница интервала для вероятности наиболее вероятного класса.</summary>
    public required double UpperBound { get; init; }

    /// <summary>Номинальный уровень доверия, например 0.95.</summary>
    public required double ConfidenceLevel { get; init; }

    /// <summary>Ширина интервала.</summary>
    public double Width => UpperBound - LowerBound;
}
