using System.Globalization;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Запись экспортированного отчёта в файл.
///
/// Клинический вариант получает блок идентификаторов пациента, обезличенный —
/// нет. Что именно попадает в файл, решено раньше, в сценарии (ADR 0005);
/// здесь это только записывается — и проверяется.
///
/// Проверка перед записью не дублирует решение сценария, а страхует от него:
/// файл уходит наружу, и стоимость ошибки здесь несимметрична. Пропущенный
/// идентификатор в обезличенном файле — утечка, которую уже не отозвать.
/// </summary>
public sealed class JsonReportExportStore : IReportExportStore
{
    /// <summary>Имя свойства с идентификаторами пациента.</summary>
    public const string PatientProperty = "patient";

    private readonly string rootDirectory;

    private static readonly JsonWriterOptions Options = new() { Indented = true };

    /// <summary>Создаёт хранилище.</summary>
    /// <param name="rootDirectory">Каталог, в который складываются экспорты.</param>
    public JsonReportExportStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Записывает экспортированный отчёт.
    /// </summary>
    /// <param name="prepared">Подготовленный экспорт.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Полный путь записанного файла.</returns>
    /// <exception cref="DomainRuleViolationException">
    /// Если обезличенный вариант содержит идентифицирующие значения.
    /// </exception>
    public async Task<string> WriteAsync(ReportExport prepared, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        var content = Serialize(prepared);

        Verify(prepared, content);

        var stamp = prepared.ExportedAt
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);

        var variant = prepared.Variant == ReportExportVariant.Clinical ? "clinical" : "deidentified";

        var path = Path.Combine(
            this.rootDirectory,
            prepared.Report.PseudonymousStudyId,
            $"{stamp}-{variant}.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);

        return path;
    }

    /// <summary>
    /// Сериализует экспорт.
    /// </summary>
    /// <param name="prepared">Подготовленный экспорт.</param>
    /// <returns>Байты JSON в UTF-8.</returns>
    public static byte[] Serialize(ReportExport prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();

            writer.WriteString("variant", prepared.Variant.ToString());
            writer.WriteString("exportedAt", prepared.ExportedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("exportedBy", prepared.ExportedByPseudonymousUserId);

            // Предупреждение печатается в самом файле, а не только в интерфейсе:
            // файл переживает диалог, в котором его показали (ADR 0005).
            writer.WriteString("limitations", Limitations);

            if (prepared.PatientIdentity is { } identity)
            {
                writer.WriteStartObject(PatientProperty);
                writer.WriteString("fullName", identity.FullName);
                writer.WriteString("medicalRecordNumber", identity.MedicalRecordNumber);

                if (identity.BirthDate is { } birthDate)
                {
                    writer.WriteString("birthDate", birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                writer.WriteEndObject();
            }

            writer.WritePropertyName("report");
            writer.WriteRawValue(CanonicalReportJson.Serialize(prepared.Report));

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Формулировка ограничений, обязательная для обоих вариантов (ADR 0005).
    /// </summary>
    public const string Limitations =
        "Вспомогательный инструмент. Не является автономной диагностикой; "
        + "результат интерпретируется врачом вместе с клиническими данными.";

    /// <summary>
    /// Проверяет, что обезличенный вариант не несёт идентификаторов.
    ///
    /// Автоматическая проверка, а не ручной осмотр — так требует план проверки
    /// ADR 0005. Проверяется готовый файл, а не намерение: между решением
    /// и записью есть код, и именно он может ошибиться.
    /// </summary>
    private static void Verify(ReportExport prepared, byte[] content)
    {
        if (prepared.Variant != ReportExportVariant.Deidentified)
        {
            return;
        }

        var text = Encoding.UTF8.GetString(content);

        if (text.Contains($"\"{PatientProperty}\"", StringComparison.Ordinal))
        {
            throw new DomainRuleViolationException(
                "A deidentified export must not carry a patient block.");
        }
    }
}
