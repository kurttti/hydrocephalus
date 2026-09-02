namespace Hydrocephalus.Domain.Quality;

/// <summary>
/// Причина отказа от ответа. Отказ — корректный результат работы системы
/// (docs/architecture/README.md), а не разновидность отрицательного заключения.
/// </summary>
public enum RefusalCode
{
    /// <summary>Причина не задана.</summary>
    Unspecified = 0,

    /// <summary>Входной контроль качества не пройден.</summary>
    QualityControlFailed = 1,

    /// <summary>Уровень входа ниже требуемого для запрошенных признаков.</summary>
    InsufficientAcquisitionTier = 2,

    /// <summary>Вход распознан как выходящий за пределы обучающего распределения.</summary>
    OutOfDistribution = 3,

    /// <summary>Model package несовместим или не прошёл проверку.</summary>
    ModelPackageUnusable = 4,

    /// <summary>Анализ отменён пользователем.</summary>
    CancelledByUser = 5,
}

/// <summary>
/// Обоснование отказа: код и приведшие к нему проблемы качества.
/// Намеренно не содержит ни вероятностей, ни классов — отказ не преобразуется в диагноз.
/// </summary>
public sealed record RefusalReason
{
    /// <summary>Машинно-читаемый код отказа.</summary>
    public required RefusalCode Code { get; init; }

    /// <summary>Проблемы качества, приведшие к отказу. Может быть пустым для отмены пользователем.</summary>
    public IReadOnlyList<QualityIssue> ContributingIssues { get; init; } = [];

    /// <summary>
    /// Создаёт отказ по непройденному контролю качества.
    /// </summary>
    /// <param name="assessment">Результат QC, который не был пройден.</param>
    /// <returns>Причина отказа со списком блокирующих проблем.</returns>
    /// <exception cref="DomainRuleViolationException">Если QC на самом деле пройден.</exception>
    public static RefusalReason FromFailedQualityControl(QualityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (assessment.IsAcceptable)
        {
            throw new DomainRuleViolationException(
                "Cannot build a refusal from an acceptable quality assessment.");
        }

        return new RefusalReason
        {
            Code = RefusalCode.QualityControlFailed,
            ContributingIssues = assessment.BlockingIssues,
        };
    }
}
