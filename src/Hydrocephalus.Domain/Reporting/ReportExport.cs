using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Domain.Reporting;

/// <summary>
/// Вариант экспорта отчёта (ADR 0005).
/// </summary>
public enum ReportExportVariant
{
    /// <summary>Вариант не задан.</summary>
    Unspecified = 0,

    /// <summary>
    /// Обезличенный: только псевдонимные идентификаторы. Пригоден для передачи
    /// и разбора случаев.
    /// </summary>
    Deidentified = 1,

    /// <summary>
    /// Клинический: с идентификаторами пациента из внешнего защищённого реестра.
    /// Для локального использования в организации.
    /// </summary>
    Clinical = 2,
}

/// <summary>
/// Идентификаторы пациента, восстановленные из внешнего защищённого реестра.
///
/// В доменной модели их нет нигде, кроме этого типа, и попадают они сюда только
/// на время формирования клинического варианта экспорта. Хранить их рядом
/// с отчётом нельзя: отчёт может быть передан вовне.
/// </summary>
public sealed record PatientIdentity
{
    /// <summary>Фамилия, имя и отчество.</summary>
    public required string FullName { get; init; }

    /// <summary>Номер медицинской карты.</summary>
    public required string MedicalRecordNumber { get; init; }

    /// <summary>Дата рождения, если она известна реестру.</summary>
    public DateOnly? BirthDate { get; init; }
}

/// <summary>
/// Отчёт, подготовленный к экспорту.
///
/// Состав определяется здесь, а не при отрисовке: рендерер не решает, что
/// скрывать (ADR 0005). Фильтрация выполняется при экспорте, а не при показе,
/// потому что PHI не должна попадать в файл, который может быть передан вовне.
///
/// Связь варианта и идентификаторов задана конструкцией: собрать обезличенный
/// экспорт с идентификаторами или клинический без них нельзя — не потому, что
/// это проверяется, а потому, что фабрика такого объекта не создаёт.
/// </summary>
public sealed record ReportExport
{
    private ReportExport()
    {
    }

    /// <summary>Отчёт.</summary>
    public required AnalysisReport Report { get; init; }

    /// <summary>Вариант экспорта.</summary>
    public required ReportExportVariant Variant { get; init; }

    /// <summary>Псевдонимный идентификатор того, кто выполнил экспорт.</summary>
    public required string ExportedByPseudonymousUserId { get; init; }

    /// <summary>Момент экспорта.</summary>
    public required DateTimeOffset ExportedAt { get; init; }

    /// <summary>
    /// Идентификаторы пациента. Заполнены только в клиническом варианте.
    /// </summary>
    public PatientIdentity? PatientIdentity { get; init; }

    /// <summary>
    /// Собирает обезличенный экспорт.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="exportedBy">Инициатор экспорта.</param>
    /// <param name="exportedAt">Момент экспорта.</param>
    /// <returns>Экспорт без идентификаторов пациента.</returns>
    public static ReportExport Deidentified(
        AnalysisReport report,
        Actor exportedBy,
        DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(exportedBy);

        return new ReportExport
        {
            Report = report,
            Variant = ReportExportVariant.Deidentified,
            ExportedByPseudonymousUserId = exportedBy.PseudonymousUserId,
            ExportedAt = exportedAt,
        };
    }

    /// <summary>
    /// Собирает клинический экспорт.
    /// </summary>
    /// <param name="report">Отчёт.</param>
    /// <param name="identity">Идентификаторы пациента из реестра.</param>
    /// <param name="exportedBy">Инициатор экспорта.</param>
    /// <param name="exportedAt">Момент экспорта.</param>
    /// <returns>Экспорт с идентификаторами пациента.</returns>
    /// <exception cref="DomainRuleViolationException">Если идентификаторы пусты.</exception>
    public static ReportExport Clinical(
        AnalysisReport report,
        PatientIdentity identity,
        Actor exportedBy,
        DateTimeOffset exportedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(exportedBy);

        if (string.IsNullOrWhiteSpace(identity.FullName)
            || string.IsNullOrWhiteSpace(identity.MedicalRecordNumber))
        {
            // Клинический вариант без идентификаторов — это обезличенный отчёт
            // под клиническим названием: врач решит, что перед ним карта
            // конкретного пациента.
            throw new DomainRuleViolationException(
                "A clinical export requires the patient identifiers it is named for.");
        }

        return new ReportExport
        {
            Report = report,
            Variant = ReportExportVariant.Clinical,
            ExportedByPseudonymousUserId = exportedBy.PseudonymousUserId,
            ExportedAt = exportedAt,
            PatientIdentity = identity,
        };
    }
}
