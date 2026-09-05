using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Infrastructure.Reporting;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Журнал аудита с цепочкой хешей.
///
/// Тесты фиксируют и то, что защита ловит, и то, чего она не ловит: правка задним
/// числом обнаруживается, а удаление хвоста журнала — нет. Обещать второе, не имея
/// внешнего якоря, было бы неправдой.
/// </summary>
public sealed class HashChainAuditLogTests : IDisposable
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
    public async Task An_intact_log_verifies()
    {
        await this.WriteAsync(AuditEventCode.StudyImported, AuditEventCode.ReportStored);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(2, verification.VerifiedRecords);
        Assert.Null(verification.FirstBrokenRecord);
    }

    [Fact]
    public async Task A_missing_log_is_not_a_failure()
    {
        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(0, verification.VerifiedRecords);
    }

    [Fact]
    public async Task Editing_a_record_after_the_fact_breaks_the_chain()
    {
        await this.WriteAsync(
            AuditEventCode.StudyImported,
            AuditEventCode.AnalysisRefused,
            AuditEventCode.ReportStored);

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);

        // Отказ переписывается в успешный анализ — ровно та правка, ради которой
        // журнал и защищается.
        lines[1] = lines[1].Replace(
            nameof(AuditEventCode.AnalysisRefused),
            nameof(AuditEventCode.AnalysisCompleted),
            StringComparison.Ordinal);

        await File.WriteAllLinesAsync(this.Path, lines, CancellationToken.None);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.False(verification.IsIntact);
        Assert.Equal(1, verification.FirstBrokenRecord);
        Assert.Equal(1, verification.VerifiedRecords);
    }

    [Fact]
    public async Task Removing_a_record_from_the_middle_breaks_the_chain()
    {
        await this.WriteAsync(
            AuditEventCode.StudyImported,
            AuditEventCode.AnalysisStarted,
            AuditEventCode.ReportStored);

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);

        await File.WriteAllLinesAsync(this.Path, [lines[0], lines[2]], CancellationToken.None);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.False(verification.IsIntact);
        Assert.Equal(1, verification.FirstBrokenRecord);
    }

    [Fact]
    public async Task Truncating_the_tail_is_not_detected_without_an_external_anchor()
    {
        // Известная граница защиты: у того, кто может писать в файл, остаётся
        // возможность удалить хвост, и оставшаяся цепочка корректна. Для внешнего
        // якоря наружу отдаётся хеш последней записи.
        var latest = await this.WriteAsync(
            AuditEventCode.StudyImported,
            AuditEventCode.AnalysisRefused,
            AuditEventCode.ReportStored);

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);

        await File.WriteAllLinesAsync(this.Path, [lines[0]], CancellationToken.None);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(1, verification.VerifiedRecords);

        // Обнаружить усечение можно только сравнением с якорем, сохранённым
        // снаружи: сам журнал после обрезки внутренне непротиворечив.
        Assert.NotEqual(latest, verification.LatestHash);
    }

    [Fact]
    public async Task Garbage_appended_to_the_log_is_reported()
    {
        await this.WriteAsync(AuditEventCode.StudyImported);

        await File.AppendAllTextAsync(this.Path, "not json\n", CancellationToken.None);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.False(verification.IsIntact);
        Assert.Equal(1, verification.FirstBrokenRecord);
    }

    [Fact]
    public async Task The_chain_continues_across_restarts()
    {
        // Цепочка продолжается от того, что лежит в файле, а не от того,
        // что помнит объект: иначе перезапуск приложения разрывал бы журнал.
        await this.WriteAsync(AuditEventCode.StudyImported);
        await this.WriteAsync(AuditEventCode.ReportStored);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(2, verification.VerifiedRecords);
    }

    [Fact]
    public async Task The_log_carries_no_free_text()
    {
        // В журнал попадают только псевдонимные идентификаторы и коды.
        await this.WriteAsync(AuditEventCode.StudyImported);

        var content = await File.ReadAllTextAsync(this.Path, CancellationToken.None);

        Assert.Contains("study-0001", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Ivanov", content, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> WriteAsync(params AuditEventCode[] codes)
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

        return log.LatestHash;
    }
}
