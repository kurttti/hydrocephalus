using System.Text.Json;

namespace Hydrocephalus.Infrastructure.Reporting;

/// <summary>
/// Строка человекочитаемого отчёта.
/// </summary>
/// <param name="Depth">Уровень вложенности.</param>
/// <param name="Text">Готовый текст строки.</param>
/// <param name="IsHeading">Заголовок раздела, а не значение.</param>
/// <param name="GapAfter">Нужен ли отступ после строки.</param>
public readonly record struct ReportLine(int Depth, string Text, bool IsHeading, bool GapAfter);

/// <summary>
/// Канонический JSON, разложенный в строки для человека.
///
/// Существует отдельно от отрисовщика PDF, потому что читателей у этого
/// разложения двое: файл, который уходит вовне, и экран предпросмотра, на
/// котором врач решает, отправлять ли его. Две реализации разошлись бы, и
/// разошлись бы незаметно — предпросмотр показывал бы одно, а в файл попадало
/// другое, то есть ровно то, от чего предпросмотр и защищает.
///
/// Разложение ничего не вычисляет и ничего не добавляет: на вход приходит
/// сериализованный JSON, а не доменный объект, поэтому показать что-то сверх
/// него здесь структурно невозможно (ADR 0005).
/// </summary>
public static class ReportOutline
{
    private static readonly Dictionary<string, string> Captions = new(StringComparer.Ordinal)
    {
        ["variant"] = "Вариант экспорта",
        ["exportedAt"] = "Экспортировано",
        ["exportedBy"] = "Экспортировал",
        ["limitations"] = "Ограничения",
        ["patient"] = "Пациент",
        ["fullName"] = "ФИО",
        ["medicalRecordNumber"] = "Номер карты",
        ["birthDate"] = "Дата рождения",
        ["report"] = "Отчёт",
        ["pseudonymousStudyId"] = "Псевдоним исследования",
        ["createdAt"] = "Сформирован",
        ["quality"] = "Контроль качества",
        ["issues"] = "Замечания",
        ["code"] = "Код",
        ["severity"] = "Степень",
        ["parameters"] = "Параметры",
        ["outcome"] = "Результат",
        ["reason"] = "Причина",
        ["prediction"] = "Прогноз",
        ["pipeline"] = "Версии конвейера",
        ["preprocessingVersion"] = "Предобработка",
        ["featureSchemaVersion"] = "Схема признаков",
        ["labelMapVersion"] = "Label map",
        ["modelVersion"] = "Версия модели",
        ["applicationCommitSha"] = "Сборка приложения",
        ["clinicianAnnotations"] = "Комментарии врача",
        ["biomarkers"] = "Измерения",
        ["method"] = "Метод",
        ["value"] = "Значение",
        ["unit"] = "Единица",
        ["quality_flag"] = "Достоверность",
        ["allowedRange"] = "Допустимый диапазон",
        ["segmentation"] = "Сегментация",
    };

    /// <summary>
    /// Раскладывает канонический JSON в строки.
    /// </summary>
    /// <param name="canonicalJson">Байты JSON, записываемого как канонический слой.</param>
    /// <returns>Строки отчёта сверху вниз.</returns>
    /// <exception cref="JsonException">Если содержимое не является JSON.</exception>
    public static IReadOnlyList<ReportLine> Build(byte[] canonicalJson)
    {
        ArgumentNullException.ThrowIfNull(canonicalJson);

        using var json = JsonDocument.Parse(canonicalJson);

        var lines = new List<ReportLine>();

        foreach (var property in json.RootElement.EnumerateObject())
        {
            Write(lines, property, depth: 0);
        }

        return lines;
    }

    private static void Write(List<ReportLine> lines, JsonProperty property, int depth)
    {
        var caption = Caption(property.Name);

        switch (property.Value.ValueKind)
        {
            case JsonValueKind.Object:
                lines.Add(new ReportLine(depth, caption, IsHeading: true, GapAfter: false));

                foreach (var nested in property.Value.EnumerateObject())
                {
                    Write(lines, nested, depth + 1);
                }

                Gap(lines);
                break;

            case JsonValueKind.Array:
                lines.Add(new ReportLine(depth, caption, IsHeading: true, GapAfter: false));

                var index = 0;

                foreach (var item in property.Value.EnumerateArray())
                {
                    WriteValue(lines, $"{++index}", item, depth + 1);
                }

                if (index == 0)
                {
                    // Пустой раздел называется пустым. Пропустить его значило бы
                    // показать отчёт, в котором его вовсе нет, — а «замечаний нет»
                    // и «о замечаниях ничего не сказано» читаются по-разному.
                    lines.Add(new ReportLine(depth + 1, "— нет", IsHeading: false, GapAfter: false));
                }

                Gap(lines);
                break;

            default:
                lines.Add(new ReportLine(
                    depth,
                    caption + ": " + Scalar(property.Value),
                    IsHeading: false,
                    GapAfter: false));

                break;
        }
    }

    private static void WriteValue(List<ReportLine> lines, string caption, JsonElement value, int depth)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            lines.Add(new ReportLine(depth, caption, IsHeading: true, GapAfter: false));

            foreach (var nested in value.EnumerateObject())
            {
                Write(lines, nested, depth + 1);
            }

            return;
        }

        lines.Add(new ReportLine(
            depth,
            caption + ": " + Scalar(value),
            IsHeading: false,
            GapAfter: false));
    }

    private static void Gap(List<ReportLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        lines[^1] = lines[^1] with { GapAfter = true };
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,

        // Отсутствующее значение показывается прочерком, а не пустотой:
        // пустое место читается как недосмотр вёрстки.
        JsonValueKind.Null => "—",

        _ => value.GetRawText(),
    };

    private static string Caption(string name) =>
        Captions.TryGetValue(name, out var caption) ? caption : name;
}
