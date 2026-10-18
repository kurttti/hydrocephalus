using System.Globalization;
using System.Text.Json;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Разметка из хранилища вместе с серией, на которой она поставлена.
/// </summary>
/// <param name="Marking">Точки и плоскость.</param>
/// <param name="PseudonymousSeriesId">
/// Серия версии отчёта; <see langword="null"/> в отчётах схем до 1.2.0.
///
/// Нужна показу: точки — отсчёты сетки **той** серии, и на другой они легли бы
/// не на анатомию. Сетки двух серий бывают одинаковыми по размеру, так что
/// проверить это кроме как по идентификатору нечем.
/// </param>
/// <param name="RecordedAt">Момент версии отчёта.</param>
public readonly record struct StoredMarking(
    MarkedMeasurement Marking,
    string? PseudonymousSeriesId,
    DateTimeOffset RecordedAt);

/// <summary>
/// Разметка, записанная в отчёт: чем врач отметил измерение.
///
/// Ищется перебором всех версий отчёта по тем же причинам, по которым так же
/// ищется само измерение (<see cref="ManualMeasurementIndex"/>): отчёт
/// неизменен, ручная отметка ложится новой версией, а повторное открытие
/// исследования сохраняет отчёт без неё. Берётся самая свежая версия, в которой
/// разметка **есть**.
///
/// Читается по одному исследованию, а не обходом хранилища: разметка нужна в
/// момент открытия исследования, и обходить ради неё все отчёты незачем.
/// </summary>
public static class ReportMarkings
{
    /// <summary>
    /// Читает последнюю записанную разметку заданного метода.
    /// </summary>
    /// <param name="reportRoot">Корень хранилища отчётов.</param>
    /// <param name="pseudonymousStudyId">Псевдоним исследования.</param>
    /// <param name="methodCode">Код метода, чью разметку искать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>
    /// Разметка либо <see langword="null"/>, если её нет. <see langword="null"/>
    /// значит «не записана»: у отчётов схем до 1.3.0 точек нет вовсе, и значение
    /// без разметки для них — норма.
    /// </returns>
    public static async Task<StoredMarking?> ReadLatestAsync(
        string reportRoot,
        string pseudonymousStudyId,
        string methodCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonymousStudyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodCode);

        var directory = Path.Combine(reportRoot, pseudonymousStudyId);

        if (!Directory.Exists(directory))
        {
            return null;
        }

        StoredMarking? latest = null;
        var moment = DateTimeOffset.MinValue;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] content;

            try
            {
                content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // Нечитаемая версия не прерывает чтение остальных: разметка
                // восстанавливается для удобства, и отказать из-за одного
                // повреждённого файла значило бы потерять все прочие.
                continue;
            }

            try
            {
                var version = Read(content, methodCode);

                if (version is { } found && found.RecordedAt > moment)
                {
                    moment = found.RecordedAt;
                    latest = found;
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return latest;
    }

    private static StoredMarking? Read(byte[] content, string methodCode)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;

        if (!root.TryGetProperty("markings", out var markings)
            || markings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var moment = root.TryGetProperty("createdAt", out var createdAt)
            && DateTimeOffset.TryParse(
                createdAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        foreach (var marking in markings.EnumerateArray())
        {
            if (!marking.TryGetProperty("method", out var method)
                || !string.Equals(method.GetString(), methodCode, StringComparison.Ordinal))
            {
                continue;
            }

            if (!marking.TryGetProperty("points", out var points)
                || points.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var placed = new List<MarkedPoint>();

            foreach (var point in points.EnumerateArray())
            {
                if (point.TryGetProperty("column", out var column)
                    && point.TryGetProperty("row", out var row)
                    && point.TryGetProperty("slice", out var slice))
                {
                    placed.Add(new MarkedPoint(
                        column.GetDouble(),
                        row.GetDouble(),
                        slice.GetDouble()));
                }
            }

            if (placed.Count == 0)
            {
                continue;
            }

            var across = marking.TryGetProperty("markedAcross", out var axis)
                && Enum.TryParse<VolumeAxis>(axis.GetString(), out var parsedAxis)
                ? parsedAxis
                : VolumeAxis.AcrossSlices;

            var plane = marking.TryGetProperty("planeIndex", out var index)
                && index.TryGetInt32(out var number)
                ? number
                : (int)Math.Round(placed[0].Slice);

            var series = root.TryGetProperty("series", out var described)
                && described.ValueKind == JsonValueKind.Object
                && described.TryGetProperty("pseudonymousSeriesId", out var id)
                ? id.GetString()
                : null;

            return new StoredMarking(
                new MarkedMeasurement(methodCode, across, Math.Max(plane, 0), placed),
                series,
                moment);
        }

        return null;
    }
}
