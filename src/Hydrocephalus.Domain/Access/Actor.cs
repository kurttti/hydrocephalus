namespace Hydrocephalus.Domain.Access;

/// <summary>
/// Отказ по правам доступа.
///
/// Отдельный тип, а не <see cref="DomainRuleViolationException"/>: «вам нельзя»
/// и «с данными что-то не так» — разные сообщения для врача и разные события
/// для журнала. Один тип на оба случая заставил бы интерфейс догадываться.
///
/// Сообщение содержит только код операции и роль: ни имени сотрудника,
/// ни идентификаторов исследования в тексте ошибки быть не должно.
/// </summary>
public sealed class AccessDeniedException : Exception
{
    /// <summary>Создаёт исключение без описания.</summary>
    public AccessDeniedException()
    {
    }

    /// <summary>Создаёт исключение с техническим описанием.</summary>
    /// <param name="message">Описание без персональных данных.</param>
    public AccessDeniedException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с описанием и внутренней причиной.</summary>
    /// <param name="message">Описание без персональных данных.</param>
    /// <param name="innerException">Внутренняя причина.</param>
    public AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Создаёт отказ по отсутствующему праву.
    /// </summary>
    /// <param name="role">Роль, которой не хватило права.</param>
    /// <param name="capability">Запрошенное право.</param>
    /// <returns>Исключение с техническим описанием.</returns>
    public static AccessDeniedException For(ClinicalRole role, Capability capability) =>
        new($"Role {role} does not grant {capability}.");
}

/// <summary>
/// Тот, от чьего имени выполняется операция.
///
/// Назван Actor, а не Operator: последнее — зарезервированное слово в ряде
/// языков, и тип с таким именем неудобен потребителям вне C#. Название
/// совпадает с полем инициатора в журнале аудита.
///
/// Идентификатор и права ездят вместе. Раздельно они разъезжаются: сценарий,
/// получающий отдельно «кто» и отдельно «что можно», рано или поздно получит
/// одно от одного вызывающего, а другое от другого.
///
/// Идентификатор псевдонимный: журнал не должен нести персональных данных
/// ни о пациенте, ни о сотруднике (docs/security/README.md).
/// </summary>
public sealed record Actor
{
    /// <summary>Псевдонимный идентификатор пользователя.</summary>
    public required string PseudonymousUserId { get; init; }

    /// <summary>Роль пользователя.</summary>
    public required ClinicalRole Role { get; init; }

    /// <summary>Права, вытекающие из роли.</summary>
    public IReadOnlySet<Capability> Capabilities => RolePolicy.CapabilitiesOf(this.Role);

    /// <summary>
    /// Создаёт пользователя.
    /// </summary>
    /// <param name="pseudonymousUserId">Псевдонимный идентификатор.</param>
    /// <param name="role">Роль.</param>
    /// <returns>Инициатор операции.</returns>
    /// <exception cref="DomainRuleViolationException">Если идентификатор пуст.</exception>
    public static Actor Create(string pseudonymousUserId, ClinicalRole role)
    {
        if (string.IsNullOrWhiteSpace(pseudonymousUserId))
        {
            // Безымянное действие нельзя ни разобрать, ни оспорить.
            throw new DomainRuleViolationException(
                "An actor must have a pseudonymous identifier.");
        }

        return new Actor { PseudonymousUserId = pseudonymousUserId, Role = role };
    }

    /// <summary>
    /// Проверяет наличие права.
    /// </summary>
    /// <param name="capability">Право.</param>
    /// <returns><see langword="true"/>, если право есть.</returns>
    public bool Can(Capability capability) => this.Capabilities.Contains(capability);

    /// <summary>
    /// Требует наличия права.
    /// </summary>
    /// <param name="capability">Право.</param>
    /// <exception cref="AccessDeniedException">Если права нет.</exception>
    public void Require(Capability capability)
    {
        if (!this.Can(capability))
        {
            throw AccessDeniedException.For(this.Role, capability);
        }
    }
}
