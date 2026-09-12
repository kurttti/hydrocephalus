using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application;

/// <summary>
/// Чтение журнала аудита для экрана администрирования.
///
/// Право проверяется здесь, а не экраном: выключенная кнопка — удобство,
/// а не разграничение. Журнал показывает работу всей установки, и доступ к нему
/// не вытекает из права разбирать один случай.
///
/// Успешное чтение в журнал не записывается, а отказ записывается. Это
/// не экономия строк: журнал не содержит данных пациента — только псевдонимы
/// и коды, — и чтение его тем, кому это разрешено, ничего не раскрывает.
/// Событием является превышение полномочий, и оно фиксируется тем же кодом
/// <see cref="AuditEventCode.AccessDenied"/>, что и всякая другая попытка
/// сделать то, на что прав нет.
/// </summary>
public sealed class ReadAuditJournalUseCase
{
    private readonly IAuditJournalSource source;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="source">Источник записей журнала.</param>
    /// <param name="auditLog">Журнал аудита для записи отказов.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public ReadAuditJournalUseCase(
        IAuditJournalSource source,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.source = source;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Читает журнал.
    /// </summary>
    /// <param name="requestedBy">Тот, кто запросил журнал.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Записи вместе с результатом проверки цепочки.</returns>
    /// <exception cref="AccessDeniedException">
    /// Если у инициатора нет права на чтение журнала.
    /// </exception>
    public async Task<AuditJournal> ExecuteAsync(
        Actor requestedBy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedBy);

        if (!requestedBy.Can(Capability.ReadAuditLog))
        {
            await this.auditLog.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.AccessDenied,
                    OccurredAt = this.timeProvider.GetUtcNow(),
                    PseudonymousActorId = requestedBy.PseudonymousUserId,
                },
                cancellationToken).ConfigureAwait(false);

            throw AccessDeniedException.For(requestedBy.Role, Capability.ReadAuditLog);
        }

        return await this.source.ReadAsync(cancellationToken).ConfigureAwait(false);
    }
}
