namespace Hydrocephalus.Domain;

/// <summary>
/// Нарушение доменного инварианта. Сообщение содержит только технические сведения:
/// доменные исключения не должны нести ФИО, идентификаторы исследования и пиксельные данные.
/// </summary>
public sealed class DomainRuleViolationException : InvalidOperationException
{
    /// <summary>Создаёт исключение без описания.</summary>
    public DomainRuleViolationException()
    {
    }

    /// <summary>Создаёт исключение с техническим описанием нарушенного инварианта.</summary>
    /// <param name="message">Описание без PHI.</param>
    public DomainRuleViolationException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с описанием и внутренней причиной.</summary>
    /// <param name="message">Описание без PHI.</param>
    /// <param name="innerException">Внутренняя причина.</param>
    public DomainRuleViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
