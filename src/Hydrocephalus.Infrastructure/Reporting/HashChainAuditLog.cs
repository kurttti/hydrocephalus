using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Результат проверки целостности журнала.
/// </summary>
/// <param name="IsIntact">Признак того, что цепочка не нарушена.</param>
/// <param name="VerifiedRecords">Число записей, прошедших проверку.</param>
/// <param name="FirstBrokenRecord">
/// Номер первой записи, на которой цепочка разошлась, либо <see langword="null"/>.
/// </param>
/// <param name="LatestHash">
/// Хеш последней проверенной записи. Сравнение с сохранённым снаружи якорем —
/// единственный способ заметить, что у журнала удалили хвост.
/// </param>
public readonly record struct AuditVerification(
    bool IsIntact,
    int VerifiedRecords,
    int? FirstBrokenRecord,
    string LatestHash);

/// <summary>
/// Журнал аудита в файле, защищённый цепочкой хешей.
///
/// Каждая запись хранит хеш предыдущей, поэтому правка любой записи задним числом
/// разрывает цепочку и обнаруживается проверкой. Это защита от **незаметного**
/// редактирования (docs/security/README.md), а не запрет на редактирование:
/// у того, кто может писать в файл, остаётся возможность удалить хвост журнала
/// целиком — оставшаяся цепочка будет корректной. Обнаружение такого усечения
/// требует внешнего якоря, и для этого наружу отдаётся <see cref="LatestHash"/>.
///
/// Записи хранятся по одной в строке: испорченная запись не мешает прочитать
/// остальные, а дописывание не требует переписывать файл.
/// </summary>
public sealed class HashChainAuditLog : IAuditLog, IDisposable
{
    /// <summary>Хеш, с которого начинается цепочка пустого журнала.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private static readonly JsonWriterOptions Options = new() { Indented = false };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string path;

    // Файл перечитывается один раз, при первой записи: дальше хвост цепочки известен.
    // Перечитывать его на каждое событие значило бы делать журнал тем медленнее,
    // чем он длиннее, а он не ротируется и растёт всю жизнь установки.
    private bool chainLoaded;

    /// <summary>Создаёт журнал.</summary>
    /// <param name="path">Путь файла журнала.</param>
    public HashChainAuditLog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = path;
    }

    /// <summary>
    /// Хеш последней записи. Внешний якорь для обнаружения усечения журнала.
    /// </summary>
    public string LatestHash { get; private set; } = GenesisHash;

    /// <summary>Освобождает примитив синхронизации записи.</summary>
    public void Dispose() => this.gate.Dispose();

    /// <summary>
    /// Записывает событие.
    /// </summary>
    /// <param name="auditEvent">Событие.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(this.path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!this.chainLoaded)
            {
                // Цепочка продолжается от того, что уже лежит в файле, а не от
                // того, что помнит объект: журнал переживает перезапуск приложения.
                this.LatestHash = await ReadLatestHashAsync(this.path, cancellationToken)
                    .ConfigureAwait(false);

                this.chainLoaded = true;
            }

            var previous = this.LatestHash;

            var payload = SerializeEvent(auditEvent);
            var hash = ComputeHash(previous, payload);
            var line = BuildLine(payload, previous, hash);

            await File.AppendAllTextAsync(this.path, line + "\n", Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            this.LatestHash = hash;
        }
        finally
        {
            this.gate.Release();
        }
    }

    /// <summary>
    /// Проверяет целостность журнала.
    ///
    /// Обход выполняет <see cref="AuditJournalReader"/>: цепочка читается
    /// и проверяется одним и тем же кодом. Вторая реализация той же проверки
    /// разошлась бы с первой молча, и экран администрирования показывал бы
    /// не то, что показывает проверка.
    /// </summary>
    /// <param name="path">Путь файла журнала.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат проверки.</returns>
    public static async Task<AuditVerification> VerifyAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var walk = await AuditJournalReader.WalkAsync(path, cancellationToken).ConfigureAwait(false);

        return walk.Verification;
    }

    private static string BuildLine(string payload, string previousHash, string hash)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("event", payload);
            writer.WriteString("previousHash", previousHash);
            writer.WriteString("hash", hash);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool TryReadRecord(
        string line,
        out string payload,
        out string previousHash,
        out string hash)
    {
        payload = string.Empty;
        previousHash = string.Empty;
        hash = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            payload = root.GetProperty("event").GetString() ?? string.Empty;
            previousHash = root.GetProperty("previousHash").GetString() ?? string.Empty;
            hash = root.GetProperty("hash").GetString() ?? string.Empty;

            return true;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static async Task<string> ReadLatestHashAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return GenesisHash;
        }

        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        for (var index = lines.Length - 1; index >= 0; index--)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                continue;
            }

            return TryReadRecord(lines[index], out _, out _, out var hash) ? hash : GenesisHash;
        }

        return GenesisHash;
    }

    internal static string ComputeHash(string previousHash, string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + "" + payload)));

    /// <summary>
    /// Сериализует событие в устойчивую строку.
    /// Порядок полей задан кодом: иначе одна и та же запись давала бы разный хеш.
    ///
    /// Записываются **все** поля события. Поле, оставленное без записи, теряется
    /// безвозвратно — журнал единственное место, где оно хранится. Без инициатора
    /// отказ по правам превращается в «кто-то попытался»; без варианта экспорта
    /// запись об экспорте не отвечает на главный вопрос — ушли ли наружу
    /// идентификаторы пациента; без итога уборки остаётся «что-то убрали» там,
    /// где записывалось «данные пациента должны были исчезнуть и не исчезли».
    ///
    /// Пустые поля записываются как null, а не пропускаются: состав записи
    /// не зависит от данных, иначе событие без инициатора не отличить
    /// от события, у которого инициатора забыли записать.
    ///
    /// Поля, добавленные позже, не делают прежние записи недействительными:
    /// проверка пересчитывает хеш от содержимого, лежащего в файле, а не от
    /// заново собранного. Журнал установки переживает обновление приложения.
    /// </summary>
    private static string SerializeEvent(AuditEvent auditEvent)
    {
        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("code", auditEvent.Code.ToString());
            writer.WriteString(
                "occurredAt",
                auditEvent.OccurredAt.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteString("pseudonymousStudyId", auditEvent.PseudonymousStudyId);
            writer.WriteString("modelVersion", auditEvent.ModelVersion);
            writer.WriteString("pseudonymousActorId", auditEvent.PseudonymousActorId);
            writer.WriteString("reportExportVariant", auditEvent.ReportExportVariant?.ToString());

            if (auditEvent.Retention is { } retention)
            {
                writer.WriteStartObject("retention");
                writer.WriteNumber("expired", retention.Expired);
                writer.WriteNumber("orphaned", retention.Orphaned);
                writer.WriteNumber("failed", retention.Failed);
                writer.WriteNumber("timeToLiveHours", retention.TimeToLiveHours);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("retention");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
