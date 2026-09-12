using System.Globalization;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Чтение журнала аудита вместе с проверкой цепочки хешей.
///
/// Обход файла здесь один и тот же и для чтения, и для проверки: вторая
/// реализация той же цепочки разошлась бы с первой незаметно, и тогда экран
/// показывал бы «цепочка цела» там, где проверка сказала бы обратное. Поэтому
/// <see cref="HashChainAuditLog.VerifyAsync"/> выражена через этот обход,
/// а не написана отдельно.
///
/// Читатель ничего не формулирует: он возвращает поля и состояние цепочки,
/// а текст для человека собирается на уровне представления (ADR 0005).
/// </summary>
public sealed class AuditJournalReader : IAuditJournalSource
{
    private readonly string path;

    /// <summary>Создаёт читателя журнала.</summary>
    /// <param name="path">Путь файла журнала.</param>
    public AuditJournalReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    /// <summary>
    /// Читает журнал и проверяет цепочку.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Записи вместе с результатом проверки.</returns>
    public async Task<AuditJournal> ReadAsync(CancellationToken cancellationToken)
    {
        var walk = await WalkAsync(this.path, cancellationToken).ConfigureAwait(false);

        return walk.Journal;
    }

    /// <summary>
    /// Обходит журнал: разбирает записи и проверяет цепочку за один проход.
    /// </summary>
    /// <param name="path">Путь файла журнала.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Журнал и сводный результат проверки.</returns>
    internal static async Task<(AuditJournal Journal, AuditVerification Verification)> WalkAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            // Отсутствие журнала — не нарушение: в установке, где ещё ничего
            // не делали, записывать было нечего.
            return (
                new AuditJournal
                {
                    Records = [],
                    IsIntact = true,
                    LatestHash = HashChainAuditLog.GenesisHash,
                    TailIsIncomplete = false,
                },
                new AuditVerification(
                    IsIntact: true,
                    VerifiedRecords: 0,
                    FirstBrokenRecord: null,
                    HashChainAuditLog.GenesisHash));
        }

        var text = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        // Файл делится вручную, а не читается построчно, ради одного различия:
        // последний отрезок без завершающего перевода строки — это запись,
        // которую дописывают прямо сейчас, а не подделка.
        var segments = text.Split('\n');

        var records = new List<AuditRecord>();
        var previous = HashChainAuditLog.GenesisHash;
        var verified = 0;
        int? firstBroken = null;
        var tailIsIncomplete = false;
        var number = 0;

        for (var index = 0; index < segments.Length; index++)
        {
            var line = segments[index].TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var readable = HashChainAuditLog.TryReadRecord(
                line,
                out var payload,
                out var recordedPrevious,
                out var recordedHash);

            if (!readable && index == segments.Length - 1)
            {
                // Оборванный хвост: запись дописывается в этот момент либо
                // не дописалась. Считать её разрывом цепочки значило бы
                // обвинять журнал в подделке за то, что он работает.
                tailIsIncomplete = true;
                continue;
            }

            number++;

            if (firstBroken is not null)
            {
                // После разрыва опорный хеш утрачен, и сказать об этих записях
                // нечего ни в одну сторону.
                records.Add(Describe(payload, number, AuditRecordIntegrity.Unverifiable, readable));
                continue;
            }

            var broken = !readable
                || !string.Equals(recordedPrevious, previous, StringComparison.Ordinal)
                || !string.Equals(
                    HashChainAuditLog.ComputeHash(previous, payload),
                    recordedHash,
                    StringComparison.Ordinal);

            if (broken)
            {
                firstBroken = index;
                records.Add(Describe(payload, number, AuditRecordIntegrity.Broken, readable));
                continue;
            }

            previous = recordedHash;
            verified++;
            records.Add(Describe(payload, number, AuditRecordIntegrity.Verified, readable: true));
        }

        return (
            new AuditJournal
            {
                Records = records,
                IsIntact = firstBroken is null,
                LatestHash = previous,
                TailIsIncomplete = tailIsIncomplete,
            },
            new AuditVerification(firstBroken is null, verified, firstBroken, previous));
    }

    private static AuditRecord Describe(
        string payload,
        int number,
        AuditRecordIntegrity integrity,
        bool readable)
    {
        if (!readable)
        {
            return new AuditRecord { Number = number, Integrity = integrity, IsReadable = false };
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            return new AuditRecord
            {
                Number = number,
                Integrity = integrity,
                IsReadable = true,
                Code = ReadCode(root),
                CodeName = ReadText(root, "code"),
                OccurredAt = ReadMoment(root),
                PseudonymousStudyId = ReadText(root, "pseudonymousStudyId"),
                ModelVersion = ReadText(root, "modelVersion"),
                PseudonymousActorId = ReadText(root, "pseudonymousActorId"),
                ReportExportVariant = ReadVariant(root),
                Retention = ReadRetention(root),
            };
        }
        catch (JsonException)
        {
            // Содержимое записи не разобралось, хотя оболочка строки разобралась.
            // Запись остаётся в журнале как нечитаемая: пропустить её значило бы
            // показать журнал, в котором её нет.
            return new AuditRecord { Number = number, Integrity = integrity, IsReadable = false };
        }
    }

    // Неизвестный код — не повод потерять запись: событие, добавленное
    // в новой версии, останется видимым в журнале, прочитанном старой,
    // и под своим именем: оно сохраняется отдельно в CodeName.
    private static AuditEventCode ReadCode(JsonElement root) =>
        Enum.TryParse<AuditEventCode>(ReadText(root, "code"), ignoreCase: false, out var code)
            ? code
            : AuditEventCode.Unspecified;

    private static DateTimeOffset? ReadMoment(JsonElement root) =>
        DateTimeOffset.TryParse(
            ReadText(root, "occurredAt"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var moment)
            ? moment
            : null;

    private static ReportExportVariant? ReadVariant(JsonElement root) =>
        Enum.TryParse<ReportExportVariant>(
            ReadText(root, "reportExportVariant"),
            ignoreCase: false,
            out var variant)
            ? variant
            : null;

    private static WorkingCopyRetentionOutcome? ReadRetention(JsonElement root)
    {
        if (!root.TryGetProperty("retention", out var retention)
            || retention.ValueKind != JsonValueKind.Object)
        {
            // Записи, сделанные до появления поля, его не содержат. Это
            // не порча журнала, и объявлять их нечитаемыми нельзя.
            return null;
        }

        return new WorkingCopyRetentionOutcome(
            ReadNumber(retention, "expired"),
            ReadNumber(retention, "orphaned"),
            ReadNumber(retention, "failed"),
            ReadHours(retention));
    }

    private static int ReadNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static double ReadHours(JsonElement element) =>
        element.TryGetProperty("timeToLiveHours", out var value) && value.TryGetDouble(out var hours)
            ? hours
            : 0.0;

    private static string? ReadText(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
