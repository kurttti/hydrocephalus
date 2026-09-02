using Hydrocephalus.Domain.Predictions;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Domain.Reporting;

/// <summary>
/// Итог анализа: либо прогноз, либо отказ. Иерархия закрыта (приватный конструктор),
/// поэтому третьего состояния не существует, а отказ по построению не содержит
/// ни вероятностей, ни классов — он не может быть прочитан как отрицательный диагноз.
/// </summary>
public abstract record AnalysisOutcome
{
    private AnalysisOutcome()
    {
    }

    /// <summary>Анализ выполнен и получен прогноз.</summary>
    public sealed record Completed : AnalysisOutcome
    {
        /// <summary>Полученный прогноз.</summary>
        public required Prediction Prediction { get; init; }
    }

    /// <summary>Система отказалась от ответа.</summary>
    public sealed record Refused : AnalysisOutcome
    {
        /// <summary>Обоснование отказа.</summary>
        public required RefusalReason Reason { get; init; }
    }
}
