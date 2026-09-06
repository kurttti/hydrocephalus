using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dataset;

/// <summary>
/// Запись манифеста датасета для ML-контура (`ml/README.md`).
///
/// Манифест строится из доменной модели, а не пересобирается потом по файлам
/// рабочей копии. Причина прямая: профиль деидентификации убирает свободный
/// текст, включая SeriesDescription, и по рабочей копии взвешенность
/// и постконтрастность уже не восстановить. Их знает импорт — значит, манифест
/// пишется тогда, когда сведения ещё есть.
///
/// В манифест попадают только псевдонимы: он уходит в исследовательский контур,
/// и исходных UID или имён в нём быть не может (docs/data/README.md).
/// Референсного диагноза здесь тоже нет — он живёт во внешнем защищённом
/// реестре и соединяется с манифестом по идентификатору пациента.
/// </summary>
public static class DatasetManifestWriter
{
    /// <summary>
    /// Версия схемы. Должна совпадать с той, которую понимает Python-пакет:
    /// расхождение версий там отвергается, а не читается как получится.
    /// </summary>
    public const string SchemaVersion = "1.0.0";

    private static readonly JsonWriterOptions Options = new()
    {
        // С отступами: манифест читают люди при разборе состава выборки,
        // а побайтовое сравнение к нему не применяется — в отличие от отчёта.
        Indented = true,
    };

    /// <summary>
    /// Сериализует манифест.
    /// </summary>
    /// <param name="studies">Исследования рабочих копий.</param>
    /// <returns>Байты JSON в UTF-8.</returns>
    public static byte[] Serialize(IEnumerable<ImagingStudy> studies)
    {
        ArgumentNullException.ThrowIfNull(studies);

        using var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream, Options))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SchemaVersion);
            writer.WriteStartArray("series");

            foreach (var study in studies)
            {
                foreach (var series in study.Series)
                {
                    Write(writer, study, series);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Сериализует манифест в строку.
    /// </summary>
    /// <param name="studies">Исследования рабочих копий.</param>
    /// <returns>JSON.</returns>
    public static string SerializeToString(IEnumerable<ImagingStudy> studies) =>
        Encoding.UTF8.GetString(Serialize(studies));

    /// <summary>
    /// Записывает манифест в файл.
    /// </summary>
    /// <param name="studies">Исследования рабочих копий.</param>
    /// <param name="path">Путь файла.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    public static async Task WriteAsync(
        IEnumerable<ImagingStudy> studies,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var content = Serialize(studies);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static void Write(Utf8JsonWriter writer, ImagingStudy study, ImagingSeries series)
    {
        var geometry = series.Geometry;

        writer.WriteStartObject();

        writer.WriteString("series_id", series.PseudonymousSeriesId);
        writer.WriteString("study_id", study.PseudonymousStudyId);
        writer.WriteString("subject_id", study.PseudonymousSubjectId);

        // Названия уровней и взвешенности пишутся так же, как они называются
        // в доменной модели: переводить их здесь значило бы завести второй
        // словарь, который однажды разойдётся с первым.
        writer.WriteString("tier", series.Tier.ToString());
        writer.WriteString("weighting", series.Weighting.ToString());
        writer.WriteBoolean("contrast_enhanced", series.IsContrastEnhanced);
        writer.WriteNumber("slice_count", geometry.Dimensions.Slices);

        writer.WriteStartArray("voxel_spacing_mm");

        // Порядок: столбец, строка, срез — тот же, что у сетки отсчётов.
        // Шаг между срезами, а не толщина: при зазоре это разные величины,
        // и объём, посчитанный по толщине, занижен.
        writer.WriteNumberValue(geometry.PixelSpacing.ColumnMillimetres);
        writer.WriteNumberValue(geometry.PixelSpacing.RowMillimetres);
        writer.WriteNumberValue(geometry.SliceSpacingMillimetres);

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
