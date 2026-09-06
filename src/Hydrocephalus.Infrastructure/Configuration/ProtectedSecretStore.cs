using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Hydrocephalus.Infrastructure.Configuration;

/// <summary>
/// Хранение случайного секрета на диске под защитой Windows DPAPI.
///
/// Общая основа для соли псевдонимизации и ключа шифрования рабочей копии:
/// правила у них одни, и повторять работу с DPAPI дважды значило бы получить
/// две реализации, которые однажды разойдутся в мелочи вроде области защиты.
///
/// Секрет привязан к профилю текущего пользователя (ADR 0006). Это ограничение
/// названо в самом ADR: при работе нескольких врачей под общей учётной записью
/// Windows разграничения между ними не будет.
/// </summary>
public static class ProtectedSecretStore
{
    /// <summary>
    /// Возвращает секрет, создавая его при первом обращении.
    /// </summary>
    /// <param name="path">Путь файла с защищённым секретом.</param>
    /// <param name="lengthBytes">Длина секрета в байтах.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Секрет в открытом виде.</returns>
    /// <exception cref="PlatformNotSupportedException">Если система не Windows.</exception>
    /// <exception cref="CryptographicException">
    /// Если файл повреждён или создан другим пользователем.
    /// </exception>
    public static async Task<byte[]> GetOrCreateAsync(
        string path,
        int lengthBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(lengthBytes, 16);

        if (!OperatingSystem.IsWindows())
        {
            // Целевая платформа приложения — Windows (ADR 0001). Хранить секрет
            // в открытом виде «пока что» на других системах нельзя.
            throw new PlatformNotSupportedException(
                "Secrets are protected with Windows DPAPI and require Windows.");
        }

        return await GetOrCreateOnWindowsAsync(path, lengthBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Удаляет секрет.
    ///
    /// Для рабочей копии это и есть удаление данных: побайтовая перезапись
    /// на SSD не гарантируется файловой системой, поэтому конфиденциальность
    /// обеспечивается недоступностью ключа, а не затиранием (ADR 0006).
    /// </summary>
    /// <param name="path">Путь файла с защищённым секретом.</param>
    /// <returns><see langword="true"/>, если файл существовал и удалён.</returns>
    public static bool Destroy(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte[]> GetOrCreateOnWindowsAsync(
        string path,
        int lengthBytes,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var sealedSecret = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);

            // Ошибка расшифровки означает, что файл повреждён или принадлежит
            // другому профилю. Молча создать новый секрет нельзя: для соли это
            // расщепит одного пациента на двух, для ключа — сделает уже
            // записанную рабочую копию нечитаемой без всякого предупреждения.
            return ProtectedData.Unprotect(
                sealedSecret,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
        }

        var secret = RandomNumberGenerator.GetBytes(lengthBytes);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var protectedSecret = ProtectedData.Protect(
            secret,
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);

        // CreateNew: если файл появился между проверкой и записью, перезаписать
        // его значило бы потерять уже использованный секрет.
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(protectedSecret, cancellationToken).ConfigureAwait(false);
        }

        return secret;
    }
}
