namespace Hydrocephalus.Domain.Access;

/// <summary>
/// Роль пользователя приложения.
/// </summary>
public enum ClinicalRole
{
    /// <summary>
    /// Роль не задана.
    ///
    /// Не даёт ни одного права. Значение по умолчанию у перечисления получают
    /// неинициализированные данные, и роль по умолчанию, дающая доступ, —
    /// это дыра, которая открывается сама.
    /// </summary>
    Unspecified = 0,

    /// <summary>Врач: разбирает случай и выдаёт отчёт.</summary>
    Clinician = 1,

    /// <summary>Исследователь: работает с обезличенными данными и выборками.</summary>
    Researcher = 2,

    /// <summary>Администратор: обслуживает установку.</summary>
    Administrator = 3,
}

/// <summary>
/// Права ролей — единственное место, где записана политика доступа.
///
/// Разграничение выполняется на уровне сценария и фиксируется в аудите
/// (ADR 0005): рендерер отчёта не решает, что скрывать, а интерфейс не решает,
/// что показать. Оба спрашивают здесь.
/// </summary>
public static class RolePolicy
{
    private static readonly Dictionary<ClinicalRole, IReadOnlySet<Capability>> Granted =
        new Dictionary<ClinicalRole, IReadOnlySet<Capability>>
        {
            // Врач разбирает случай и выдаёт отчёт в обоих вариантах,
            // но выборками не занимается: сбор датасета — не лечебная работа.
            [ClinicalRole.Clinician] = new HashSet<Capability>
            {
                Capability.AnalyseStudy,
                Capability.ExportDeidentifiedReport,
                Capability.ExportClinicalReport,
            },

            // Исследователь работает с обезличенными данными. Клинический
            // вариант отчёта ему недоступен: он несёт идентификаторы пациента,
            // а исследовательская задача в них не нуждается.
            [ClinicalRole.Researcher] = new HashSet<Capability>
            {
                Capability.AnalyseStudy,
                Capability.ExportDeidentifiedReport,
                Capability.ExportDatasetManifest,
            },

            // Администратор обслуживает установку и прав на данные пациентов
            // не получает. Это решение, а не упущение: обслуживание системы
            // и доступ к её содержимому — разные задачи, и совмещать их
            // по умолчанию значит раздавать доступ там, где он не нужен.
            //
            // Журнал аудита — единственное, что ему доступно, и это не
            // исключение из правила, а то же правило: журнал не содержит данных
            // пациента, а отвечает на вопрос, кто и что делал с установкой.
            // Врачу его не дают по обратной причине — журнал показывает работу
            // всех, а разбор одного случая этого не требует.
            [ClinicalRole.Administrator] = new HashSet<Capability>
            {
                Capability.ReadAuditLog,
            },

            [ClinicalRole.Unspecified] = new HashSet<Capability>(),
        };

    /// <summary>
    /// Возвращает права роли.
    /// </summary>
    /// <param name="role">Роль.</param>
    /// <returns>Права; пустое множество для неизвестной роли.</returns>
    public static IReadOnlySet<Capability> CapabilitiesOf(ClinicalRole role) =>
        // Неизвестная роль не даёт прав: новая роль, добавленная в перечисление
        // и забытая здесь, должна ничего не мочь, а не всё.
        Granted.TryGetValue(role, out var capabilities) ? capabilities : new HashSet<Capability>();
}
