using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Domain.Predictions;

/// <summary>
/// Калиброванный прогноз модели. Создаётся только через <see cref="Create"/>:
/// прямое конструирование закрыто, чтобы прогноз нельзя было получить в обход инвариантов.
/// </summary>
public sealed record Prediction
{
    private Prediction()
    {
    }

    /// <summary>Модель, выдавшая прогноз, вместе с перечнем поддерживаемых классов.</summary>
    public required ModelIdentity Model { get; init; }

    /// <summary>Вероятности классов.</summary>
    public required IReadOnlyList<ClassProbability> Probabilities { get; init; }

    /// <summary>Оценка неопределённости.</summary>
    public required Uncertainty Uncertainty { get; init; }

    /// <summary>Наиболее вероятный класс.</summary>
    public ClassProbability MostLikely => Probabilities.MaxBy(probability => probability.Probability);

    /// <summary>
    /// Создаёт прогноз, проверяя доменные инварианты.
    /// </summary>
    /// <param name="quality">Результат входного контроля качества.</param>
    /// <param name="model">Модель, выдавшая прогноз.</param>
    /// <param name="probabilities">Вероятности классов.</param>
    /// <param name="uncertainty">Оценка неопределённости.</param>
    /// <returns>Прогноз с проверенными предусловиями.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если QC не пройден, список вероятностей пуст, содержит повторяющиеся классы,
    /// класс вне перечня поддерживаемых моделью, либо вероятность вне диапазона от 0 до 1.
    /// </exception>
    public static Prediction Create(
        QualityAssessment quality,
        ModelIdentity model,
        IReadOnlyList<ClassProbability> probabilities,
        Uncertainty uncertainty)
    {
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(probabilities);
        ArgumentNullException.ThrowIfNull(uncertainty);

        // Инвариант: прогноз невозможен без успешного QC.
        if (!quality.IsAcceptable)
        {
            throw new DomainRuleViolationException(
                "A prediction cannot be produced when quality control has not passed.");
        }

        if (probabilities.Count == 0)
        {
            throw new DomainRuleViolationException("A prediction must contain at least one class probability.");
        }

        // Инвариант: прогноз связан с моделью и перечнем поддерживаемых классов.
        // Сумма вероятностей намеренно не проверяется: калибровка применяется отдельным
        // артефактом (ADR 0004) и не обязана давать нормированный вектор.
        var supported = model.SupportedClasses.ToHashSet();

        if (supported.Count == 0)
        {
            throw new DomainRuleViolationException(
                "A prediction requires a model that declares its supported classes.");
        }

        var seen = new HashSet<DiagnosticClass>();

        foreach (var probability in probabilities)
        {
            if (!supported.Contains(probability.Class))
            {
                throw new DomainRuleViolationException(
                    $"Class '{probability.Class.Code}' is outside the classes supported by model "
                    + $"'{model.Name}' {model.Version}.");
            }

            if (!seen.Add(probability.Class))
            {
                throw new DomainRuleViolationException(
                    $"Class '{probability.Class.Code}' appears more than once in the prediction.");
            }

            if (probability.Probability is < 0 or > 1 || double.IsNaN(probability.Probability))
            {
                throw new DomainRuleViolationException(
                    $"Probability for class '{probability.Class.Code}' must lie between 0 and 1.");
            }
        }

        return new Prediction
        {
            Model = model,
            Probabilities = probabilities,
            Uncertainty = uncertainty,
        };
    }
}
