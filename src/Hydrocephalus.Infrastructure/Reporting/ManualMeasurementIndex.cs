using System.Globalization;
using System.Text.Json;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>Ручное измерение одного исследования.</summary>
/// <param name="EvansIndex">Индекс Эванса, отмеченный врачом.</param>
/// <param name="HeadRotationDegrees">Поворот головы при измерении; null, если не записан.</param>
/// <param name="RecordedAt">Момент версии отчёта, из которой взято измерение.</param>
public readonly record struct ManualMeasurement(
    double EvansIndex,
    double? HeadRotationDegrees,
    DateTimeOffset RecordedAt);

/// <summary>
/// Что уже измерено врачом, по хранилищу отчётов.
///
/// **Ищется перебором всех версий отчёта, а не в последней.** Сохранённый отчёт
/// неизменен, и ручная отметка ложится новой версией; повторное открытие
/// исследования заново запускает анализ и сохраняет отчёт **без** неё, поэтому
/// самая свежая версия измерения может не содержать. Тот же перебор поглощает
/// случайное повторное нажатие: две версии с одинаковым значением дают одно
/// измерение.
///
/// Значение вне диапазона правдоподобия не считается измерением. Записать такое
/// сейчас нельзя, но отчёты прежних версий остаются на диске навсегда.
///
/// Читателей двое: сведение результатов и очередь измерений в просмотрщике. У
/// второго от этого зависит, предложит ли он исследование снова.
/// </summary>
public static class ManualMeasurementIndex
{
    /// <summary>Код метода ручного индекса Эванса в отчёте.</summary>
    public const string ManualEvansCode = "evans-index-manual";

    /// <summary>Код метода автоматического индекса Эванса в отчёте.</summary>
    public const string AutomaticEvansCode = "evans-index";

    /// <summary>Код метода поворота головы в отчёте.</summary>
    public const string HeadRotationCode = "head-rotation-in-plane";

    /// <summary>
    /// Читает хранилище отчётов.
    /// </summary>
    /// <param name="reportRoot">Корень хранилища отчётов.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// Ручные и автоматические измерения по псевдониму исследования. Если
    /// хранилища нет, оба словаря пусты: измерений ещё не было — это утверждение,
    /// а не отсутствие сведений.
    /// </returns>
    public static async Task<MeasurementReadout> ReadAsync(
        string reportRoot,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportRoot);

        var manual = new Dictionary<string, ManualMeasurement>(StringComparer.Ordinal);
        var automatic = new Dictionary<string, double>(StringComparer.Ordinal);
        var unreadable = 0;

        if (!Directory.Exists(reportRoot))
        {
            return new MeasurementReadout(manual, automatic, unreadable);
        }

        foreach (var studyDirectory in Directory.EnumerateDirectories(reportRoot))
        {
            var studyId = Path.GetFileName(studyDirectory);

            foreach (var path in Directory.EnumerateFiles(studyDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                Version version;

                try
                {
                    var content = await File.ReadAllBytesAsync(path, cancellationToken)
                        .ConfigureAwait(false);

                    version = Read(content);
                }
                catch (Exception exception) when (exception is JsonException or IOException)
                {
                    // Нечитаемый отчёт не прерывает чтение остальных, но и не
                    // пропадает молча: их число возвращается наружу.
                    unreadable++;
                    continue;
                }

                if (version.Evans is { } evans
                    && (!manual.TryGetValue(studyId, out var known)
                        || version.CreatedAt > known.RecordedAt))
                {
                    manual[studyId] = new ManualMeasurement(evans, version.Rotation, version.CreatedAt);
                }

                if (version.Automatic is { } value)
                {
                    automatic[studyId] = value;
                }
            }
        }

        return new MeasurementReadout(manual, automatic, unreadable);
    }

    private static Version Read(byte[] content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        var moment = root.TryGetProperty("createdAt", out var createdAt)
            && DateTimeOffset.TryParse(
                createdAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        double? manual = null;
        double? automatic = null;
        double? rotation = null;

        if (root.TryGetProperty("biomarkers", out var biomarkers))
        {
            foreach (var biomarker in biomarkers.EnumerateArray())
            {
                if (biomarker.TryGetProperty("outOfRange", out var flagged) && flagged.GetBoolean())
                {
                    continue;
                }

                var value = biomarker.GetProperty("value").GetDouble();

                switch (biomarker.GetProperty("code").GetString())
                {
                    case ManualEvansCode:
                        manual = value;
                        break;
                    case AutomaticEvansCode:
                        automatic = value;
                        break;
                    case HeadRotationCode:
                        rotation = value;
                        break;
                    default:
                        break;
                }
            }
        }

        return new Version(moment, manual, automatic, rotation);
    }

    private readonly record struct Version(
        DateTimeOffset CreatedAt,
        double? Evans,
        double? Automatic,
        double? Rotation);
}

/// <summary>Что нашлось в хранилище отчётов.</summary>
/// <param name="Manual">Ручные измерения по псевдониму исследования.</param>
/// <param name="Automatic">Автоматические индексы по псевдониму исследования.</param>
/// <param name="UnreadableReports">Сколько файлов отчётов прочитать не удалось.</param>
public sealed record MeasurementReadout(
    IReadOnlyDictionary<string, ManualMeasurement> Manual,
    IReadOnlyDictionary<string, double> Automatic,
    int UnreadableReports);
