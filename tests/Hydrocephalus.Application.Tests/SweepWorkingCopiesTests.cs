using Hydrocephalus.Application;
using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Уборка рабочих копий и её след в журнале.
///
/// ADR 0006 требует, чтобы удаление по сроку оставляло запись. Проверяется
/// именно это: без записи политика хранения недоказуема — по диску видно
/// только, что каталогов нет, и это выглядит одинаково при исправной уборке
/// и при том, что её никогда не выполняли.
/// </summary>
public sealed class SweepWorkingCopiesTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task What_was_deleted_reaches_the_journal()
    {
        var audit = new RecordingAudit();

        await UseCase(new StubRetention(new WorkingCopyRetentionOutcome(3, 1, 0, 24)), audit)
            .ExecuteAsync(CancellationToken.None);

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.WorkingCopiesSwept, recorded.Code);
        Assert.Equal(new WorkingCopyRetentionOutcome(3, 1, 0, 24), recorded.Retention);
    }

    [Fact]
    public async Task A_sweep_that_removed_nothing_is_still_recorded()
    {
        // «Уборка выполнена, удалять нечего» и «уборка не выполнялась» —
        // разные утверждения, и различить их по молчанию невозможно.
        var audit = new RecordingAudit();

        await UseCase(new StubRetention(new WorkingCopyRetentionOutcome(0, 0, 0, 24)), audit)
            .ExecuteAsync(CancellationToken.None);

        Assert.Single(audit.Events);
    }

    [Fact]
    public async Task The_sweep_is_not_attributed_to_anyone()
    {
        // Уборка выполняется при старте по сроку, а не по чьей-то команде.
        // Приписать её врачу, под которым запущено приложение, значило бы
        // записать в журнал решение, которого он не принимал.
        var audit = new RecordingAudit();

        await UseCase(new StubRetention(new WorkingCopyRetentionOutcome(1, 0, 0, 24)), audit)
            .ExecuteAsync(CancellationToken.None);

        var recorded = Assert.Single(audit.Events);

        Assert.Null(recorded.PseudonymousActorId);
        Assert.Null(recorded.PseudonymousStudyId);
    }

    [Fact]
    public async Task A_copy_that_could_not_be_deleted_is_named_as_such()
    {
        // «Данные пациента должны были исчезнуть и не исчезли» — ровно то,
        // ради чего журнал и ведётся. Спрятать это среди удалённых значило бы
        // записать неправду.
        var audit = new RecordingAudit();

        var outcome = await UseCase(
                new StubRetention(new WorkingCopyRetentionOutcome(2, 0, 1, 24)),
                audit)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(1, outcome.Failed);
        Assert.Equal(1, Assert.Single(audit.Events).Retention?.Failed);
    }

    [Fact]
    public async Task The_period_the_sweep_applied_is_recorded_with_it()
    {
        // Срок задаёт установка и он может отличаться от значения по умолчанию.
        // Без него запись отвечает «сколько удалено», но не «по какому правилу».
        var audit = new RecordingAudit();

        await UseCase(new StubRetention(new WorkingCopyRetentionOutcome(1, 0, 0, 4)), audit)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(4, Assert.Single(audit.Events).Retention?.TimeToLiveHours);
    }

    private static SweepWorkingCopiesUseCase UseCase(IWorkingCopyRetention retention, IAuditLog audit) =>
        new(retention, audit, new FixedTime(Moment));

    private sealed class StubRetention(WorkingCopyRetentionOutcome outcome) : IWorkingCopyRetention
    {
        public WorkingCopyRetentionOutcome Sweep(DateTimeOffset now) => outcome;
    }

    private sealed class RecordingAudit : IAuditLog
    {
        private readonly List<AuditEvent> events = [];

        public IReadOnlyList<AuditEvent> Events => this.events;

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            this.events.Add(auditEvent);

            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset moment) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => moment;
    }
}
