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
        var salt = await ProtectedSecretStore
            .GetOrCreateAsync(path, SaltLengthBytes, cancellationToken)
            .ConfigureAwait(false);

        return Convert.ToBase64String(salt);
    }
}
