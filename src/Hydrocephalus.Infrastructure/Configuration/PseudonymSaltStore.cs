using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Hydrocephalus.Infrastructure.Configuration;

/// <summary>
/// Хранилище соли псевдонимизации.
///
/// Соль создаётся на установку и никогда не поставляется вместе с приложением:
/// общая для всех установок соль сделала бы псевдонимы предсказуемыми, то есть
/// восстановимыми по словарю исходных идентификаторов. По той же причине здесь
/// нет значения по умолчанию — отсутствующая соль это ошибка, а не повод
/// подставить константу.
///
/// На диске соль лежит защищённой Windows DPAPI в области текущего пользователя
/// (ADR 0006): открытый файл рядом с рабочей копией обесценил бы замену UID.
/// </summary>
public static class PseudonymSaltStore
{
    /// <summary>Длина соли в байтах.</summary>
    public const int SaltLengthBytes = 32;

    /// <summary>
    /// Возвращает соль, создавая её при первом обращении.
    /// </summary>
    /// <param name="path">Путь файла с защищённой солью.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Соль в виде строки base64.</returns>
    /// <exception cref="PlatformNotSupportedException">Если система не Windows.</exception>
    /// <exception cref="CryptographicException">Если файл повреждён или создан другим пользователем.</exception>
    public static async Task<string> GetOrCreateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsWindows())
        {
            // Целевая платформа приложения — Windows (ADR 0001). Хранить соль
            // в открытом виде «пока что» на других системах нельзя.
            throw new PlatformNotSupportedException(
                "The pseudonymisation salt is protected with Windows DPAPI and requires Windows.");
        }

        return await GetOrCreateOnWindowsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string> GetOrCreateOnWindowsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var protectedSalt = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

            // Ошибка расшифровки означает, что файл повреждён или принадлежит
            // другому профилю. Молча создать новую соль здесь нельзя: прежние
            // псевдонимы перестанут совпадать, и один пациент разойдётся на двух.
            return Convert.ToBase64String(
                ProtectedData.Unprotect(protectedSalt, optionalEntropy: null, DataProtectionScope.CurrentUser));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);

        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sealedSalt = ProtectedData.Protect(salt, optionalEntropy: null, DataProtectionScope.CurrentUser);

        // CreateNew: если файл появился между проверкой и записью, перезаписать
        // его значило бы потерять уже использованную соль.
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(sealedSalt, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToBase64String(salt);
    }
}
