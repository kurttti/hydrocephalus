namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Путь, приведённый к виду, пригодному для показа на экране.
///
/// Все каталоги приложения лежат в профиле текущего пользователя (ADR 0006),
/// а путь профиля содержит имя учётной записи. В клинике оно обычно образовано
/// от фамилии сотрудника, и показанный целиком путь сводит на нет псевдонимы
/// в соседних строках: на экране администрирования «инициатор actor-42» стоит
/// рядом с расположением файлов, и полный путь стал бы ключом к псевдониму.
///
/// Заменяется на переменную окружения, а не обрезается: <c>%LOCALAPPDATA%</c>
/// остаётся рабочим путём — его можно вставить в проводник, — и при этом
/// ничего не называет.
/// </summary>
public static class PathReadout
{
    /// <summary>
    /// Заменяет путь профиля пользователя на переменную окружения.
    /// </summary>
    /// <param name="path">Путь файла или каталога.</param>
    /// <returns>Путь для показа.</returns>
    public static string Describe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Порядок важен: локальный каталог данных лежит внутри профиля,
        // и проверка профиля первой заменила бы более точное совпадение
        // на менее точное.
        return Replace(
            Replace(path, Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
            Environment.SpecialFolder.UserProfile,
            "%USERPROFILE%");
    }

    private static string Replace(string path, Environment.SpecialFolder folder, string name)
    {
        var prefix = Environment.GetFolderPath(folder);

        // Пустой путь особой папки заменил бы собой начало любой строки.
        return !string.IsNullOrEmpty(prefix)
            && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name + path[prefix.Length..]
            : path;
    }
}
