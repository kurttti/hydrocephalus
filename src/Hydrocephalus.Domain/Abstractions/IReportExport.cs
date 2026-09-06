using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Внешний защищённый реестр соответствия псевдонимов и пациентов.
///
/// Живёт вне приложения: связь рабочей копии с исходными данными существует
/// только там (docs/data/README.md). Приложение обращается к нему лишь тогда,
/// когда врач запрашивает клинический вариант отчёта, и не хранит полученное.
/// </summary>
public interface IPatientIdentityRegistry
{
    /// <summary>
    /// Восстанавливает идентификаторы пациента по псевдониму исследования.
    /// </summary>
    /// <param name="pseudonymousStudyId">Псевдонимный идентификатор исследования.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Идентификаторы либо <see langword="null"/>, если соответствия нет.</returns>
    Task<PatientIdentity?> ResolveAsync(
        string pseudonymousStudyId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Хранилище экспортированных отчётов.
///
/// Отдельно от хранилища канонических отчётов: то остаётся внутри приложения,
/// а экспорт делается ради передачи наружу. Разные направления и разные
/// правила, и объединять их значило бы позволить одному вызову подменить другой.
/// </summary>
public interface IReportExportStore
{
    /// <summary>
    /// Записывает экспортированный отчёт.
    /// </summary>
    /// <param name="prepared">Подготовленный экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ссылка на записанный файл в терминах инфраструктуры.</returns>
    Task<string> WriteAsync(ReportExport prepared, CancellationToken cancellationToken);
}
