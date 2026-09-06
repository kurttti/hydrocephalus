using System.Security.Cryptography;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Шифрование содержимого рабочей копии (ADR 0006).
///
/// AES-256-GCM, а не CBC или CTR: режим с аутентификацией отличает повреждённый
/// или подменённый файл от исправного. Без этого подмена воксельных данных
/// в рабочем каталоге прошла бы незамеченной — расшифровалось бы во что-то,
/// и анализ посчитал бы это изображением.
///
/// Одноразовый вектор случаен и свой у каждого файла. Повторный вектор при том же
/// ключе разрушает GCM полностью: он раскрывает разность открытых текстов
/// и позволяет подделать тег. Счётчик тут не годится — рабочие копии создаются
/// параллельно, и общее состояние пришлось бы синхронизировать.
/// </summary>
public static class WorkingCopyCipher
{
    /// <summary>Длина ключа в байтах.</summary>
    public const int KeyLengthBytes = 32;

    /// <summary>Длина одноразового вектора в байтах.</summary>
    public const int NonceLengthBytes = 12;

    /// <summary>Длина тега аутентификации в байтах.</summary>
    public const int TagLengthBytes = 16;

    /// <summary>Наименьший размер зашифрованного файла: вектор и тег.</summary>
    public const int OverheadBytes = NonceLengthBytes + TagLengthBytes;

    /// <summary>
    /// Шифрует содержимое.
    /// </summary>
    /// <param name="key">Ключ длиной <see cref="KeyLengthBytes"/> байт.</param>
    /// <param name="plaintext">Открытые данные.</param>
    /// <returns>Вектор, тег и шифротекст одним блоком.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plaintext)
    {
        EnsureKey(key);

        var result = new byte[OverheadBytes + plaintext.Length];

        var nonce = result.AsSpan(0, NonceLengthBytes);
        var tag = result.AsSpan(NonceLengthBytes, TagLengthBytes);
        var ciphertext = result.AsSpan(OverheadBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagLengthBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return result;
    }

    /// <summary>
    /// Расшифровывает содержимое.
    /// </summary>
    /// <param name="key">Ключ длиной <see cref="KeyLengthBytes"/> байт.</param>
    /// <param name="content">Вектор, тег и шифротекст одним блоком.</param>
    /// <returns>Открытые данные.</returns>
    /// <exception cref="InvalidDataException">Если блок короче служебных полей.</exception>
    /// <exception cref="AuthenticationTagMismatchException">
    /// Если тег не сходится: файл повреждён, подменён или зашифрован другим ключом.
    /// </exception>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> content)
    {
        EnsureKey(key);

        if (content.Length < OverheadBytes)
        {
            throw new InvalidDataException(
                "The encrypted block is shorter than its nonce and tag.");
        }

        var nonce = content[..NonceLengthBytes];
        var tag = content.Slice(NonceLengthBytes, TagLengthBytes);
        var ciphertext = content[OverheadBytes..];

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagLengthBytes);

        // Несошедшийся тег поднимает AuthenticationTagMismatchException,
        // и это правильное поведение: отдать «расшифрованные как получилось»
        // байты значило бы подать на анализ то, чего в исходных данных не было.
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    private static void EnsureKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLengthBytes)
        {
            throw new ArgumentException(
                $"The key must be {KeyLengthBytes} bytes long.",
                nameof(key));
        }
    }
}
