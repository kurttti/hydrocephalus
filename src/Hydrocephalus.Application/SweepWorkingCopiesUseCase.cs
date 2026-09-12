using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Application;

/// <summary>
/// Уборка рабочих копий перед началом работы.
///
/// Сценарий, а не вызов из сборки приложения, по одной причине: уборка удаляет
/// данные пациента, а всё, что их удаляет, обязано оставлять след в журнале
/// (ADR 0006). Без записи политика хранения недоказуема — по диску видно
/// только, что каталогов нет, и это выглядит одинаково при исправной уборке
/// и при том, что её никогда не выполняли.
///
/// Инициатор здесь не указывается, и это не упущение: уборка выполняется при
/// старте по сроку, а не по чьей-то команде. Приписать её врачу, под которым
/// запущено приложение, значило бы записать в журнал решение, которого он
/// не принимал.
/// </summary>
public sealed class SweepWorkingCopiesUseCase
{
    private readonly IWorkingCopyRetention retention;
    private readonly IAuditLog auditLog;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт сценарий.
    /// </summary>
    /// <param name="retention">Уборка рабочих копий.</param>
    /// <param name="auditLog">Журнал аудита.</param>
    /// <param name="timeProvider">Источник времени.</param>
    public SweepWorkingCopiesUseCase(
        IWorkingCopyRetention retention,
        IAuditLog auditLog,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.retention = retention;
        this.auditLog = auditLog;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Убирает просроченные и осиротевшие рабочие копии и записывает итог в журнал.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Что было убрано и по какому сроку.</returns>
    public async Task<WorkingCopyRetentionOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();

        var outcome = this.retention.Sweep(now);

        // Запись делается всегда, в том числе когда убирать было нечего.
        // «Уборка выполнена, удалять нечего» и «уборка не выполнялась» —
        // разные утверждения, и различить их по молчанию невозможно.
        await this.auditLog.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.WorkingCopiesSwept,
                    OccurredAt = now,
                    Retention = outcome,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return outcome;
    }
}
