namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Хранилище установленных пакетов модели: версии рядом, одна действующая.
///
/// Договор живёт в домене, потому что сценарий установки обязан его знать, а
/// слой сценариев не видит инфраструктуры (`docs/architecture/README.md`).
/// Раскладка на диске — её дело: здесь нет ни каталогов, ни расширений.
///
/// Установка и активация разделены — это требование ADR 0008. Пакет, лежащий в
/// хранилище, ещё ничего не измеряет; измеряет только действующий. Разделение
/// нужно и для отката: он не переустанавливает файл, а переключает метку, и
/// потому не зависит от того, сохранил ли администратор исходный файл.
///
/// Действующей версии может не быть вовсе, и это рядовое состояние: приложение
/// поставляется без модели, установщик её не приносит (ADR 0008).
/// </summary>
public interface IInstalledModelStore
{
    /// <summary>
    /// Перечисляет установленные версии.
    /// </summary>
    /// <returns>Версии; пусто, если не установлено ничего.</returns>
    IReadOnlyList<string> InstalledVersions();

    /// <summary>
    /// Действующая версия либо <see langword="null"/>, если её нет.
    /// </summary>
    /// <returns>Версия или <see langword="null"/>.</returns>
    string? ActiveVersion();

    /// <summary>
    /// Путь к пакету действующей версии либо <see langword="null"/>.
    /// </summary>
    /// <returns>Путь или <see langword="null"/>.</returns>
    string? ActivePackagePath();

    /// <summary>
    /// Путь к пакету установленной версии.
    ///
    /// Нужен, чтобы перед переключением метки проверить именно тот файл,
    /// которым будут измерять: пакет, испортившийся в хранилище после
    /// установки, иначе обнаружился бы только отказом при следующем запуске.
    /// </summary>
    /// <param name="modelVersion">Версия.</param>
    /// <returns>Путь; файла по нему может не быть.</returns>
    string PackagePathOf(string modelVersion);

    /// <summary>
    /// Кладёт пакет в хранилище под его версией, не делая действующим.
    /// </summary>
    /// <param name="packagePath">Путь к проверенному файлу пакета.</param>
    /// <param name="modelVersion">Версия из объявления пакета.</param>
    /// <returns>Путь, по которому пакет лёг в хранилище.</returns>
    /// <exception cref="IOException">Если такая версия уже установлена.</exception>
    string Install(string packagePath, string modelVersion);

    /// <summary>
    /// Помечает версию действующей.
    /// </summary>
    /// <param name="modelVersion">Версия, которая должна стать действующей.</param>
    /// <exception cref="FileNotFoundException">Если такая версия не установлена.</exception>
    void Activate(string modelVersion);
}
