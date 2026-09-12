using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;

namespace Hydrocephalus.Application.Tests;

/// <summary>
/// Чтение журнала аудита и права на него.
///
/// Проверяется разграничение, а не вёрстка: выключенная кнопка — удобство,
/// и полагаться на неё как на защиту нельзя. Журнал показывает работу всей
/// установки, и право на него не вытекает из права разбирать один случай.
///
/// Отдельно проверяется несимметричность следа: отказ записывается, успешное
/// чтение — нет. Событием здесь является превышение полномочий; журнал
/// не содержит данных пациента, и чтение его тем, кому это разрешено,
/// ничего не раскрывает.
/// </summary>
public sealed class ReadAuditJournalTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_administrator_reads_the_journal()
    {
        var audit = new RecordingAudit();

        var journal = await UseCase(audit).ExecuteAsync(
            Actor.Create("actor-1", ClinicalRole.Administrator),
            CancellationToken.None);

        Assert.Single(journal.Records);
    }

    [Fact]
    public async Task A_clinician_is_refused()
    {
        // Врач разбирает свой случай, а журнал показывает работу всех.
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<AccessDeniedException>(() => UseCase(audit).ExecuteAsync(
            Actor.Create("actor-2", ClinicalRole.Clinician),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_researcher_is_refused()
    {
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<AccessDeniedException>(() => UseCase(audit).ExecuteAsync(
            Actor.Create("actor-3", ClinicalRole.Researcher),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_leaves_a_record()
    {
        // Попытка, не оставившая следа, ничем не отличается от её отсутствия.
        var audit = new RecordingAudit();

        await Assert.ThrowsAsync<AccessDeniedException>(() => UseCase(audit).ExecuteAsync(
            Actor.Create("actor-2", ClinicalRole.Clinician),
            CancellationToken.None));

        var recorded = Assert.Single(audit.Events);

        Assert.Equal(AuditEventCode.AccessDenied, recorded.Code);
        Assert.Equal("actor-2", recorded.PseudonymousActorId);
    }

    [Fact]
    public async Task A_permitted_read_leaves_no_record()
    {
        // Журнал не содержит данных пациента, и разрешённое чтение ничего
        // не раскрывает. Запись о каждом открытии экрана сделала бы журнал
        // рассказом о самом себе.
        var audit = new RecordingAudit();

        await UseCase(audit).ExecuteAsync(
            Actor.Create("actor-1", ClinicalRole.Administrator),
            CancellationToken.None);

        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task A_refused_read_does_not_touch_the_journal_file()
    {
        // Отказ происходит до чтения, а не после: иначе журнал успевал бы
        // покинуть источник, и разграничение проверяло бы уже выданное.
        var source = new CountingSource();

        var useCase = new ReadAuditJournalUseCase(source, new RecordingAudit(), new FixedTime(Moment));

        await Assert.ThrowsAsync<AccessDeniedException>(() => useCase.ExecuteAsync(
            Actor.Create("actor-2", ClinicalRole.Clinician),
            CancellationToken.None));

        Assert.Equal(0, source.Reads);
    }

    private static ReadAuditJournalUseCase UseCase(IAuditLog audit) =>
        new(new CountingSource(), audit, new FixedTime(Moment));

    private sealed class CountingSource : IAuditJournalSource
    {
        public int Reads { get; private set; }

        public Task<AuditJournal> ReadAsync(CancellationToken cancellationToken)
        {
            this.Reads++;

            return Task.FromResult(new AuditJournal
            {
                Records =
                [
                    new AuditRecord
                    {
                        Number = 1,
                        Integrity = AuditRecordIntegrity.Verified,
                        IsReadable = true,
                        Code = AuditEventCode.StudyImported,
                        OccurredAt = Moment,
                    },
                ],
                IsIntact = true,
                LatestHash = new string('a', 64),
                TailIsIncomplete = false,
            });
        }
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
