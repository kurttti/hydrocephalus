using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Application;

/// <summary>
/// Запрос на экспорт манифеста датасета.
/// </summary>
public sealed record DatasetExportRequest
{
    /// <summary>Исследования, попадающие в манифест.</summary>
    public required IReadOnlyList<ImagingStudy> Studies { get; init; }

    /// <summary>
    /// Псевдонимный идентификатор того, кто запросил экспорт.
    ///
    /// Обязателен: экспорт выборки наружу — действие, за которое кто-то
    /// отвечает, и безымянный экспорт нельзя ни разобрать, ни оспорить.
    /// </summary>
    public required string RequestedByPseudonymousUserId { get; init; }

    /// <summary>
    /// Явное подтверждение экспорта.
    ///
    /// Отдельное поле, а не сам факт вызова: сбор обучающего датасета не должен
    /// быть побочным эффектом клинической работы. Подтверждение делает намерение
    /// видимым в коде и не даёт вызвать экспорт мимоходом.
    /// </summary>
    public required bool Confirmed { get; init; }
}

/// <summary>
/// Экспорт манифеста датасета в исследовательский контур.
///
/// Отдельный сценарий, а не шаг импорта. Импорт выполняется ради конкретного
/// пациента, а манифест описывает выборку и уходит за пределы клинического
/// контура: собирать его молча, попутно с лечебной работой, нельзя.
///
/// Сценарий не ищет данные сам. Исследования передаются ему явно, и это тоже
/// граница: у сценария, который сам обходит хранилище рабочих копий, нет
/// естественного предела — он выгрузит всё, что найдёт.
///
/// Персональных данных манифест не несёт: только псевдонимы и параметры серий,
/// без референсного диагноза (docs/data/README.md). Обеспечивает это запись
/// манифеста; здесь проверяется, что экспортировать вообще есть что.
/// </summary>
public sealed class ExportDatasetManifestUseCase
{
    private readonly IDatasetManifestStore store;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="store">Хранилище манифестов.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public ExportDatasetManifestUseCase(
        IDatasetManifestStore store,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Выполняет экспорт.
    /// </summary>
    /// <param name="request">Запрос на экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Ссылка на записанный манифест.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если экспорт не подтверждён, инициатор не назван либо экспортировать нечего.
    /// </exception>
    public async Task<string> ExecuteAsync(
        DatasetExportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Confirmed)
        {
            throw new DomainRuleViolationException(
                "Exporting a dataset manifest requires explicit confirmation.");
        }

        if (string.IsNullOrWhiteSpace(request.RequestedByPseudonymousUserId))
        {
            throw new DomainRuleViolationException(
                "Exporting a dataset manifest requires a named requester.");
        }

        if (request.Studies.Count == 0)
        {
            // Пустой экспорт создал бы файл, по которому нельзя отличить
            // «данных нет» от «выгрузка не сработала».
            throw new DomainRuleViolationException(
                "There is nothing to export: no studies were supplied.");
        }

        if (request.Studies.All(study => study.Series.Count == 0))
        {
            throw new DomainRuleViolationException(
                "There is nothing to export: none of the studies has a series.");
        }

        var reference = await this.store.WriteAsync(request.Studies, cancellationToken)
            .ConfigureAwait(false);

        // Событие пишется после успешной записи: журнал должен отражать
        // состоявшийся экспорт, а не намерение.
        await this.auditLog.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.DatasetManifestExported,
                OccurredAt = this.timeProvider.GetUtcNow(),
                PseudonymousActorId = request.RequestedByPseudonymousUserId,
            },
            cancellationToken).ConfigureAwait(false);

        return reference;
    }
}
