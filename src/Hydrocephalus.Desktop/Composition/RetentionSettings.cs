using System.Globalization;
using System.IO;
using System.Text.Json;
using Hydrocephalus.Infrastructure.Volumes;

// В WPF-проекте короткое Path разрешается в System.Windows.Shapes.Path,
// поэтому файловый путь называется через псевдоним.
using IoPath = System.IO.Path;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Срок жизни рабочих копий, заданный при установке (ADR 0006).
///
/// Читается из файла рядом с исполняемым файлом — там же, где лежит
/// <see cref="ActorSettings"/>: срок задаёт тот, кто разворачивает приложение,
/// и он не должен меняться от того, под кем оно запущено.
///
/// Испорченное или бессмысленное значение не останавливает запуск и не
/// принимается: берётся значение по умолчанию, а действующий срок попадает
/// в журнал вместе с итогом уборки и показывается врачу. ADR называет риском
/// именно молчаливое удаление незавершённого разбора случая, поэтому
/// «незаметно подставили другое число» — то, чего здесь быть не должно.
/// </summary>
public static class RetentionSettings
{
    /// <summary>Имя файла настроек рядом с исполняемым файлом.</summary>
    public const string FileName = "retention.json";

    /// <summary>Наибольший допустимый срок жизни, часов.</summary>
    /// <remarks>
    /// Верхняя граница нужна, чтобы опечатка в порядке величины не превратила
    /// политику хранения в её отсутствие: неделя — это уже не «до конца разбора
    /// случая», а бессрочное хранение расшифрованных данных на рабочей станции.
    /// </remarks>
    public const double MaxTimeToLiveHours = 168;

    /// <summary>
    /// Читает политику хранения из файла настроек.
    /// </summary>
    /// <param name="directory">Каталог, в котором лежит файл настроек.</param>
    /// <returns>Политика хранения.</returns>
    public static WorkingCopyRetentionPolicy Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var hours = ReadHours(IoPath.Combine(directory, FileName));

        return hours is null
            ? new WorkingCopyRetentionPolicy()
            : new WorkingCopyRetentionPolicy { TimeToLive = TimeSpan.FromHours(hours.Value) };
    }

    /// <summary>
    /// Читает срок жизни из файла; отсутствие файла означает значение по умолчанию.
    /// </summary>
    /// <param name="path">Путь файла настроек.</param>
    /// <returns>Срок в часах либо <see langword="null"/>, если значения нет.</returns>
    public static double? ReadHours(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("timeToLiveHours", out var value)
                || !value.TryGetDouble(out var hours))
            {
                return null;
            }

            // Ноль и отрицательное значение означали бы удаление рабочей копии
            // сразу после создания, то есть неработающее приложение. Это ошибка
            // настройки, а не политика.
            return hours > 0 && hours <= MaxTimeToLiveHours ? hours : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Описывает действующий срок для показа врачу.
    /// </summary>
    /// <param name="policy">Политика хранения.</param>
    /// <returns>Строка для показа пользователю.</returns>
    /// <remarks>
    /// ADR 0006 называет риском молчаливое удаление незавершённого разбора
    /// случая и требует, чтобы поведение было заранее видимым. Срок, о котором
    /// врач узнаёт в момент пропажи данных, этому требованию не отвечает.
    /// </remarks>
    public static string Describe(WorkingCopyRetentionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Рабочая копия удаляется через {policy.TimeToLive.TotalHours:0.#} ч.");
    }
}
