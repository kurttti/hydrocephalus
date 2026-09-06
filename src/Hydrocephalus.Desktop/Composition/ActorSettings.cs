using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Domain.Access;

// В WPF-проекте короткое Path разрешается в System.Windows.Shapes.Path,
// поэтому файловый путь называется через псевдоним.
using IoPath = System.IO.Path;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Роль пользователя приложения, заданная при установке.
///
/// Роль читается из файла настроек, который пишет тот, кто разворачивает
/// приложение, и **не выбирается пользователем в интерфейсе**. Выбор роли самим
/// пользователем был бы хуже, чем отсутствие ролей: он позволяет объявить себя
/// кем угодно, а в журнал аудита попадёт заявление, неотличимое по виду от факта.
/// Видимость контроля вместо контроля опаснее, чем его признанное отсутствие.
///
/// Настоящей аутентификации здесь нет. Идентификатор пользователя выводится
/// из учётной записи Windows, то есть отвечает на вопрос «под кем запущено
/// приложение», а не «кто за клавиатурой». До клинического применения это
/// должно быть заменено проверкой членства в группе домена или иным механизмом
/// организации; ограничение названо здесь, чтобы его нельзя было не заметить.
/// </summary>
public static class ActorSettings
{
    /// <summary>Имя файла настроек рядом с исполняемым файлом.</summary>
    public const string FileName = "actor.json";

    /// <summary>
    /// Читает роль из файла настроек.
    /// </summary>
    /// <param name="directory">Каталог, в котором лежит файл настроек.</param>
    /// <returns>Инициатор операций приложения.</returns>
    public static Actor Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var role = ReadRole(IoPath.Combine(directory, FileName));

        return Actor.Create(PseudonymousUserId(), role);
    }

    /// <summary>
    /// Читает роль из файла; отсутствие файла означает отсутствие прав.
    /// </summary>
    /// <param name="path">Путь файла настроек.</param>
    /// <returns>Роль либо <see cref="ClinicalRole.Unspecified"/>.</returns>
    public static ClinicalRole ReadRole(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            // Ненастроенная установка не получает прав. Обратное умолчание —
            // «нет файла, значит врач» — раздавало бы доступ там, где никто
            // ничего не решал.
            return ClinicalRole.Unspecified;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("role", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return ClinicalRole.Unspecified;
            }

            // Разбор строгий по регистру: «clinician» и «Clinician» — разные
            // строки, и молчаливое приравнивание однажды примет опечатку
            // за роль.
            return Enum.TryParse<ClinicalRole>(value.GetString(), ignoreCase: false, out var role)
                ? role
                : ClinicalRole.Unspecified;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // Испорченный файл настроек — это неизвестная роль, а не любая
            // из известных.
            return ClinicalRole.Unspecified;
        }
    }

    /// <summary>
    /// Строит псевдонимный идентификатор пользователя из учётной записи Windows.
    ///
    /// Хеш, а не само имя: журнал не должен нести персональных данных ни
    /// о пациенте, ни о сотруднике (docs/security/README.md), а имя учётной
    /// записи в клинике обычно образовано от фамилии.
    /// </summary>
    /// <returns>Псевдонимный идентификатор.</returns>
    public static string PseudonymousUserId()
    {
        var account = Environment.UserDomainName + "\\" + Environment.UserName;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(account));

        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }
}
