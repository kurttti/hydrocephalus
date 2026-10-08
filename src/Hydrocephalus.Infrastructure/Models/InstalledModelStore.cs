using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hydrocephalus.Infrastructure.Models;

/// <summary>
/// Установленные пакеты модели: версии рядом, одна действующая (ADR 0008).
///
/// Решение ADR 0008 требует, чтобы установка нового пакета не удаляла
/// предыдущий, а откат был переключением метки — без переустановки файла и без
/// сети. Причина клиническая: смена версии модели меняет результат для одного и
/// того же пациента, и возврат назад не должен зависеть от того, сохранил ли
/// администратор старый файл.
///
/// Хранилище не проверяет пакеты и не читает весов: это дело
/// <see cref="SignedModelPackageReader"/>. Здесь только раскладка на диске —
/// какие версии есть и какая действует.
///
/// Действующей версии может не быть вовсе, и это рядовое состояние: приложение
/// поставляется без модели (ADR 0008 — установщик её не приносит), и до
/// установки работает пороговым путём.
/// </summary>
public sealed class InstalledModelStore
{
    /// <summary>Имя файла с меткой действующей версии.</summary>
    public const string ActiveMarkerName = "active.json";

    /// <summary>Расширение файла пакета (ADR 0004).</summary>
    public const string PackageExtension = ".hcmp";

    private static readonly JsonSerializerOptions Format = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string root;

    /// <summary>
    /// Открывает хранилище.
    /// </summary>
    /// <param name="root">Каталог, в котором лежат пакеты.</param>
    public InstalledModelStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        this.root = root;
    }

    /// <summary>
    /// Перечисляет установленные версии.
    /// </summary>
    /// <returns>Версии в порядке имён; пусто, если не установлено ничего.</returns>
    public IReadOnlyList<string> InstalledVersions()
    {
        if (!Directory.Exists(this.root))
        {
            return [];
        }

        return [.. Directory
            .EnumerateFiles(this.root, $"*{PackageExtension}")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Действующая версия либо <see langword="null"/>, если её нет.
    ///
    /// Метка, указывающая на пропавший файл, равнозначна отсутствию действующей
    /// версии: иначе приложение сообщало бы версию, которой не может загрузить.
    /// </summary>
    /// <returns>Версия или <see langword="null"/>.</returns>
    public string? ActiveVersion()
    {
        var marker = Path.Combine(this.root, ActiveMarkerName);

        if (!File.Exists(marker))
        {
            return null;
        }

        ActiveModel? active;

        try
        {
            active = JsonSerializer.Deserialize<ActiveModel>(File.ReadAllBytes(marker), Format);
        }
        catch (JsonException)
        {
            return null;
        }

        var version = active?.ModelVersion;

        return version is not null && File.Exists(this.PathOf(version)) ? version : null;
    }

    /// <summary>
    /// Путь к пакету действующей версии либо <see langword="null"/>.
    /// </summary>
    /// <returns>Путь или <see langword="null"/>.</returns>
    public string? ActivePackagePath()
    {
        var version = this.ActiveVersion();

        return version is null ? null : this.PathOf(version);
    }

    /// <summary>
    /// Кладёт пакет в хранилище под его версией, не делая действующим.
    ///
    /// Копирование, а не перенос: файл администратора остаётся у него. Разделение
    /// установки и активации — требование ADR 0008: переключение версии запрещено
    /// во время выполняющегося анализа, а значит это два разных действия.
    /// </summary>
    /// <param name="packagePath">Путь к проверенному файлу пакета.</param>
    /// <param name="modelVersion">Версия из объявления пакета.</param>
    /// <returns>Путь, по которому пакет лёг в хранилище.</returns>
    public string Install(string packagePath, string modelVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);

        var destination = this.PathOf(modelVersion);

        Directory.CreateDirectory(this.root);
        File.Copy(packagePath, destination, overwrite: true);

        return destination;
    }

    /// <summary>
    /// Помечает версию действующей.
    /// </summary>
    /// <param name="modelVersion">Версия, которая должна стать действующей.</param>
    /// <exception cref="FileNotFoundException">Если такая версия не установлена.</exception>
    public void Activate(string modelVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);

        var package = this.PathOf(modelVersion);

        if (!File.Exists(package))
        {
            throw new FileNotFoundException(
                "The model version to activate is not installed.", package);
        }

        Directory.CreateDirectory(this.root);
        File.WriteAllBytes(
            Path.Combine(this.root, ActiveMarkerName),
            JsonSerializer.SerializeToUtf8Bytes(new ActiveModel(modelVersion), Format));
    }

    /// <summary>
    /// Путь пакета заданной версии внутри хранилища.
    ///
    /// Версия берётся из объявления пакета и в имя файла попадает как есть, но
    /// каталог к ней не прибавляется: версия вида `../..` иначе вывела бы запись
    /// за пределы хранилища.
    /// </summary>
    private string PathOf(string modelVersion) => Path.Combine(
        this.root,
        Path.GetFileName(modelVersion) + PackageExtension);

    private sealed record ActiveModel(
        [property: JsonPropertyName("modelVersion")] string ModelVersion);
}
