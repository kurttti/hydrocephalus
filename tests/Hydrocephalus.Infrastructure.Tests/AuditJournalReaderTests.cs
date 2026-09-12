using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Чтение журнала аудита вместе с проверкой цепочки.
///
/// Проверяется не вёрстка и не тексты — их собирает уровень представления, —
/// а два обещания читателя. Первое: из файла возвращается ровно то, что туда
/// записали, включая поля, которых у ранних записей нет. Второе: состояние
/// цепочки различается на три положения, а не на два, потому что «запись
/// изменена» и «об этой записи сказать нечего» — разные утверждения,
/// и второе относится ко всему, что идёт после разрыва.
/// </summary>
public sealed class AuditJournalReaderTests : IDisposable
{
    private static readonly DateTimeOffset Moment =
        new(2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

    private readonly DirectoryInfo root = SyntheticDicom.CreateTempDirectory();

    private string Path => System.IO.Path.Combine(this.root.FullName, "audit", "audit.log");

    public void Dispose()
    {
        if (this.root.Exists)
        {
            this.root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_journal_is_empty_and_intact()
    {
        // В установке, где ещё ничего не делали, записывать было нечего.
        var journal = await this.ReadAsync();

        Assert.Empty(journal.Records);
        Assert.True(journal.IsIntact);
        Assert.Equal(HashChainAuditLog.GenesisHash, journal.LatestHash);
        Assert.False(journal.TailIsIncomplete);
    }

    [Fact]
    public async Task Every_field_written_is_read_back()
    {
        using (var log = new HashChainAuditLog(this.Path))
        {
            await log.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.ReportExported,
                    OccurredAt = Moment,
                    PseudonymousStudyId = "study-0001",
                    ModelVersion = "model-7",
                    PseudonymousActorId = "actor-42",
                    ReportExportVariant = ReportExportVariant.Deidentified,
                },
                CancellationToken.None);

            await log.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.WorkingCopiesSwept,
                    OccurredAt = Moment.AddSeconds(1),
                    Retention = new WorkingCopyRetentionOutcome(
                        Expired: 2,
                        Orphaned: 1,
                        Failed: 3,
                        TimeToLiveHours: 24.0),
                },
                CancellationToken.None);
        }

        var journal = await this.ReadAsync();

        var exported = journal.Records[0];

        Assert.Equal(AuditEventCode.ReportExported, exported.Code);
        Assert.Equal(Moment, exported.OccurredAt);
        Assert.Equal("study-0001", exported.PseudonymousStudyId);
        Assert.Equal("model-7", exported.ModelVersion);
        Assert.Equal("actor-42", exported.PseudonymousActorId);
        Assert.Equal(ReportExportVariant.Deidentified, exported.ReportExportVariant);

        var swept = journal.Records[1];

        Assert.Equal(new WorkingCopyRetentionOutcome(2, 1, 3, 24.0), swept.Retention);

        // Уборка не относится ни к исследованию, ни к человеку, и пустые поля
        // у неё — это состояние дел, а не потеря при чтении.
        Assert.Null(swept.PseudonymousActorId);
        Assert.Null(swept.PseudonymousStudyId);
    }

    [Fact]
    public async Task Records_of_an_intact_journal_are_all_verified()
    {
        await this.WriteAsync(AuditEventCode.StudyImported, AuditEventCode.ReportStored);

        var journal = await this.ReadAsync();

        Assert.True(journal.IsIntact);
        Assert.Equal([1, 2], journal.Records.Select(record => record.Number));
        Assert.All(journal.Records, record =>
            Assert.Equal(AuditRecordIntegrity.Verified, record.Integrity));
    }

    [Fact]
    public async Task After_a_break_the_rest_is_unverifiable_rather_than_broken()
    {
        // Опорный хеш после разрыва утрачен, и записи за ним не проверены
        // ни в одну сторону. Объявить их изменёнными значило бы обвинить
        // в подделке то, чего никто не трогал; объявить целыми — выдать
        // непроверенное за проверенное.
        await this.WriteAsync(
            AuditEventCode.StudyImported,
            AuditEventCode.AnalysisRefused,
            AuditEventCode.ReportStored);

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);

        lines[1] = lines[1].Replace(
            nameof(AuditEventCode.AnalysisRefused),
            nameof(AuditEventCode.AnalysisCompleted),
            StringComparison.Ordinal);

        await File.WriteAllLinesAsync(this.Path, lines, CancellationToken.None);

        var journal = await this.ReadAsync();

        Assert.False(journal.IsIntact);
        Assert.Equal(AuditRecordIntegrity.Verified, journal.Records[0].Integrity);
        Assert.Equal(AuditRecordIntegrity.Broken, journal.Records[1].Integrity);
        Assert.Equal(AuditRecordIntegrity.Unverifiable, journal.Records[2].Integrity);

        // Содержимое подделанной записи остаётся видимым: разбирается тот,
        // кто смотрит журнал, а не читатель за него.
        Assert.Equal(AuditEventCode.AnalysisCompleted, journal.Records[1].Code);
    }

    [Fact]
    public async Task An_unreadable_line_stays_in_the_journal()
    {
        // Пропустить нечитаемую строку значило бы показать журнал, в котором
        // её нет, — то есть скрыть ровно то место, где что-то не так.
        await this.WriteAsync(AuditEventCode.StudyImported);

        await File.AppendAllTextAsync(this.Path, "not json\n", CancellationToken.None);

        var journal = await this.ReadAsync();

        Assert.False(journal.IsIntact);
        Assert.Equal(2, journal.Records.Count);
        Assert.False(journal.Records[1].IsReadable);
        Assert.Equal(AuditRecordIntegrity.Broken, journal.Records[1].Integrity);
        Assert.False(journal.TailIsIncomplete);
    }

    [Fact]
    public async Task A_torn_last_line_is_not_called_a_broken_chain()
    {
        // Журнал дописывается в конец, и чтение застаёт запись незавершённой.
        // Назвать это подделкой значило бы обвинить журнал в том, что он
        // работает: следующее чтение увидит строку целой.
        await this.WriteAsync(AuditEventCode.StudyImported);

        await File.AppendAllTextAsync(this.Path, """{"event":"{\"co""", CancellationToken.None);

        var journal = await this.ReadAsync();

        Assert.True(journal.IsIntact);
        Assert.True(journal.TailIsIncomplete);
        Assert.Single(journal.Records);
    }

    [Fact]
    public async Task An_intact_journal_still_offers_its_hash_as_an_anchor()
    {
        // Целостность цепочки не означает полноты журнала: обрезанный хвост
        // оставляет цепочку корректной. Обнаружить это можно только сравнением
        // с якорем, сохранённым вне файла.
        await this.WriteAsync(AuditEventCode.StudyImported, AuditEventCode.ReportStored);

        var full = await this.ReadAsync();

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);
        await File.WriteAllLinesAsync(this.Path, [lines[0]], CancellationToken.None);

        var truncated = await this.ReadAsync();

        Assert.True(truncated.IsIntact);
        Assert.NotEqual(full.LatestHash, truncated.LatestHash);
    }

    [Fact]
    public async Task A_code_from_a_newer_version_keeps_its_name()
    {
        // Более новая версия пишет событие, которого эта не знает. Разбор кода
        // даёт Unspecified, а имя должно дожить до экрана: «событие неизвестного
        // кода» без указания какого не помогает разбирающему журнал.
        const string payload =
            """{"code":"ModelPackageLoaded","occurredAt":"2026-03-14T09:26:53.0000000Z"}""";

        var separator = ((char)0x1E).ToString();
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(HashChainAuditLog.GenesisHash + separator + payload)));

        var line = JsonSerializer.Serialize(new
        {
            @event = payload,
            previousHash = HashChainAuditLog.GenesisHash,
            hash,
        });

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.Path)!);
        await File.WriteAllTextAsync(this.Path, line + Environment.NewLine, CancellationToken.None);

        var record = Assert.Single((await this.ReadAsync()).Records);

        Assert.Equal(AuditRecordIntegrity.Verified, record.Integrity);
        Assert.Equal(AuditEventCode.Unspecified, record.Code);
        Assert.Equal("ModelPackageLoaded", record.CodeName);
    }

    private Task<AuditJournal> ReadAsync() =>
        new AuditJournalReader(this.Path).ReadAsync(CancellationToken.None);

    private async Task WriteAsync(params AuditEventCode[] codes)
    {
        using var log = new HashChainAuditLog(this.Path);

        for (var index = 0; index < codes.Length; index++)
        {
            await log.RecordAsync(
                new AuditEvent
                {
                    Code = codes[index],
                    OccurredAt = Moment.AddSeconds(index),
                    PseudonymousStudyId = "study-0001",
                },
                CancellationToken.None);
        }
    }
}
