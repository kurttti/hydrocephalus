using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;
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

    [Fact]
    public async Task Every_field_of_an_event_reaches_the_file()
    {
        // Поле, не попавшее в файл, теряется безвозвратно: журнал — единственное
        // место, где оно хранится. Без инициатора отказ по правам превращается
        // в «кто-то попытался», а уборка без чисел — в «что-то убрали».
        using var log = new HashChainAuditLog(this.Path);

        await log.RecordAsync(
            new AuditEvent
            {
                Code = AuditEventCode.ReportExported,
                OccurredAt = Moment,
                PseudonymousStudyId = "study-0001",
                ModelVersion = "model-7",
                PseudonymousActorId = "actor-42",
                ReportExportVariant = ReportExportVariant.Clinical,
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

        var content = await File.ReadAllTextAsync(this.Path, CancellationToken.None);

        // Инициатор ищется в файле как есть: он должен там быть, а не быть
        // выведен разбором, который сам может его придумать.
        Assert.Contains("actor-42", content, StringComparison.Ordinal);

        var lines = await File.ReadAllLinesAsync(this.Path, CancellationToken.None);

        var exported = Payload(lines[0]);

        Assert.Equal("actor-42", exported.GetProperty("pseudonymousActorId").GetString());
        Assert.Equal("model-7", exported.GetProperty("modelVersion").GetString());
        Assert.Equal(
            nameof(ReportExportVariant.Clinical),
            exported.GetProperty("reportExportVariant").GetString());

        // Неудавшееся удаление — ровно то, ради чего журнал ведётся:
        // «данные пациента должны были исчезнуть и не исчезли».
        var retention = Payload(lines[1]).GetProperty("retention");

        Assert.Equal(2, retention.GetProperty("expired").GetInt32());
        Assert.Equal(1, retention.GetProperty("orphaned").GetInt32());
        Assert.Equal(3, retention.GetProperty("failed").GetInt32());
        Assert.Equal(24.0, retention.GetProperty("timeToLiveHours").GetDouble());
    }

    [Fact]
    public async Task A_record_written_before_the_fields_were_added_still_verifies()
    {
        // Состав записи со временем расширяется, а журнал установки не
        // переписывается. Проверка пересчитывает хеш от содержимого, лежащего
        // в файле, а не от собранного заново, — иначе обновление приложения
        // объявляло бы прежние записи подделкой.
        //
        // Формат старой записи выписан здесь дословно, а не взят у кода:
        // тест, пользующийся той же сериализацией, проверял бы её саму собой.
        const string payload =
            """{"code":"StudyImported","occurredAt":"2026-03-14T09:26:53.0000000Z","pseudonymousStudyId":"study-0001","modelVersion":null}""";

        var hash = Sha256(HashChainAuditLog.GenesisHash + "\u001e" + payload);

        var line = JsonSerializer.Serialize(new
        {
            @event = payload,
            previousHash = HashChainAuditLog.GenesisHash,
            hash,
        });

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(this.Path)!);
        await File.WriteAllTextAsync(this.Path, line + "\n", CancellationToken.None);

        // Новая запись продолжает ту же цепочку, уже в расширенном составе.
        await this.WriteAsync(AuditEventCode.ReportStored);

        var verification = await HashChainAuditLog.VerifyAsync(this.Path, CancellationToken.None);

        Assert.True(verification.IsIntact);
        Assert.Equal(2, verification.VerifiedRecords);
    }

    // Содержимое события лежит строкой JSON внутри строки записи: так
    // испорченная запись не мешает прочитать остальные.
    private static JsonElement Payload(string line) =>
        JsonDocument.Parse(
            JsonDocument.Parse(line).RootElement.GetProperty("event").GetString()!)
            .RootElement.Clone();

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

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
