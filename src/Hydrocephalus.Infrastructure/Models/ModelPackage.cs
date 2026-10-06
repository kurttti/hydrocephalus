using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hydrocephalus.Infrastructure.Models;

/// <summary>
/// Причина, по которой пакет модели не принят.
///
/// Код машинно-читаемый: объяснение для человека собирается на слое
/// представления. Частичная загрузка запрещена — любой из этих случаев означает
/// отказ целиком (ADR 0004).
/// </summary>
public enum ModelPackageRejection
{
    /// <summary>Причина не задана.</summary>
    Unspecified = 0,

    /// <summary>Файл не читается как пакет.</summary>
    NotAPackage = 1,

    /// <summary>Манифест отсутствует или не разбирается.</summary>
    ManifestUnreadable = 2,

    /// <summary>Список хешей отсутствует или не разбирается.</summary>
    ChecksumsUnreadable = 3,

    /// <summary>Подписи нет, либо она не сходится с доверенным ключом.</summary>
    SignatureInvalid = 4,

    /// <summary>Хеш файла не совпал с объявленным.</summary>
    ContentAltered = 5,

    /// <summary>Состав пакета не совпадает с объявленным: файл лишний или пропал.</summary>
    CompositionMismatch = 6,

    /// <summary>Пакет требует более новой версии приложения.</summary>
    ApplicationTooOld = 7,

    /// <summary>Версия предобработки или карты меток приложению неизвестна.</summary>
    IncompatibleContract = 8,
}

/// <summary>
/// Объявление пакета модели.
/// </summary>
/// <param name="FormatVersion">Версия самого формата пакета.</param>
/// <param name="ModelVersion">Версия модели, как её показывают врачу.</param>
/// <param name="MinimumApplicationVersion">Наименьшая версия приложения, с которой пакет совместим.</param>
/// <param name="PreprocessingVersion">Версия описания предобработки.</param>
/// <param name="LabelMapVersion">Версия карты меток.</param>
/// <param name="SigningKeyId">Идентификатор ключа, которым подписан список хешей.</param>
public sealed record ModelPackageManifest(
    [property: JsonPropertyName("formatVersion")] string FormatVersion,
    [property: JsonPropertyName("modelVersion")] string ModelVersion,
    [property: JsonPropertyName("minimumApplicationVersion")] string MinimumApplicationVersion,
    [property: JsonPropertyName("preprocessingVersion")] string PreprocessingVersion,
    [property: JsonPropertyName("labelMapVersion")] string LabelMapVersion,
    [property: JsonPropertyName("signingKeyId")] string SigningKeyId);

/// <summary>Итог проверки пакета.</summary>
/// <param name="Manifest">Объявление пакета; <see langword="null"/> при отказе.</param>
/// <param name="Rejection">Причина отказа; <see langword="null"/>, если пакет принят.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelPackageCheck(
    ModelPackageManifest? Manifest,
    ModelPackageRejection? Rejection,
    string Detail = "");

/// <summary>
/// Пакет модели: чтение, проверка целостности и подписи.
///
/// Порядок проверок задан ADR 0004 и соблюдается строго **до чтения любых
/// весов**: манифест, подпись списка хешей, хеши и состав, версия приложения,
/// версии предобработки и карты меток. Подмена модели названа в
/// `docs/security/README.md` отдельной угрозой, и проверка здесь — её мера.
///
/// Открытый ключ передаётся вызывающим, а не берётся из пакета: ключ из
/// проверяемого файла проверяет только то, что файл подписан сам собой.
/// </summary>
public static class ModelPackage
{
    /// <summary>Версия формата, которую понимает эта сборка.</summary>
    public const string SupportedFormatVersion = "1";

    /// <summary>Имя манифеста внутри пакета.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>Имя списка хешей.</summary>
    public const string ChecksumsEntry = "checksums.sha256";

    /// <summary>Имя отделённой подписи списка хешей.</summary>
    public const string SignatureEntry = "checksums.sha256.sig";

    /// <summary>Имя файла весов сегментации.</summary>
    public const string SegmentationEntry = "segmentation.onnx";

    /// <summary>
    /// Файлы, которые пакету разрешено содержать.
    ///
    /// Перечень закрытый: ADR 0004 требует отвергать незнакомые имена, а не
    /// пропускать их. Подписанный пакет с лишним файлом — всё ещё подписанный,
    /// и подпись сама по себе не говорит, что этот файл кто-то проверял.
    ///
    /// Классификатор и калибровка не названы: их в пакете пока нет, и
    /// добавление — отдельное решение, а не молчаливое расширение списка.
    /// </summary>
    private static readonly string[] AllowedEntries =
    [
        ManifestEntry,
        ChecksumsEntry,
        SignatureEntry,
        SegmentationEntry,
        "preprocessing.json",
        "labels.json",
        "model-card.md",
    ];

    /// <summary>
    /// Проверяет пакет и возвращает его объявление.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <param name="trustedPublicKey">Доверенный открытый ключ ECDSA P-256.</param>
    /// <param name="applicationVersion">Версия приложения.</param>
    /// <param name="knownPreprocessing">Версии предобработки, которые умеет приложение.</param>
    /// <param name="knownLabelMaps">Версии карт меток, которые умеет приложение.</param>
    /// <returns>Объявление пакета либо названная причина отказа.</returns>
    public static ModelPackageCheck Verify(
        string packagePath,
        ECDsa trustedPublicKey,
        Version applicationVersion,
        IReadOnlyCollection<string> knownPreprocessing,
        IReadOnlyCollection<string> knownLabelMaps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(trustedPublicKey);
        ArgumentNullException.ThrowIfNull(applicationVersion);
        ArgumentNullException.ThrowIfNull(knownPreprocessing);
        ArgumentNullException.ThrowIfNull(knownLabelMaps);

        ZipArchive archive;

        try
        {
            archive = ZipFile.OpenRead(packagePath);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            return new ModelPackageCheck(null, ModelPackageRejection.NotAPackage);
        }

        using (archive)
        {
            return Inspect(archive, trustedPublicKey, applicationVersion, knownPreprocessing, knownLabelMaps);
        }
    }

    /// <summary>
    /// Извлекает файл из пакета. Зовётся только после успешной проверки.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <param name="entryName">Имя файла внутри пакета.</param>
    /// <param name="destinationPath">Куда положить.</param>
    public static void Extract(string packagePath, string entryName, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var archive = ZipFile.OpenRead(packagePath);
        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"The package holds no entry named {entryName}.");

        entry.ExtractToFile(destinationPath, overwrite: true);
    }

    private static ModelPackageCheck Inspect(
        ZipArchive archive,
        ECDsa trustedPublicKey,
        Version applicationVersion,
        IReadOnlyCollection<string> knownPreprocessing,
        IReadOnlyCollection<string> knownLabelMaps)
    {
        if (Read(archive, ManifestEntry) is not { } manifestBytes)
        {
            return new ModelPackageCheck(null, ModelPackageRejection.ManifestUnreadable);
        }

        ModelPackageManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<ModelPackageManifest>(manifestBytes);
        }
        catch (JsonException)
        {
            return new ModelPackageCheck(null, ModelPackageRejection.ManifestUnreadable);
        }

        if (manifest is null
            || !string.Equals(manifest.FormatVersion, SupportedFormatVersion, StringComparison.Ordinal))
        {
            return new ModelPackageCheck(
                null, ModelPackageRejection.ManifestUnreadable, manifest?.FormatVersion ?? string.Empty);
        }

        if (Read(archive, ChecksumsEntry) is not { } checksumBytes)
        {
            return new ModelPackageCheck(manifest, ModelPackageRejection.ChecksumsUnreadable);
        }

        // Подпись проверяется раньше самих хешей: список хешей, которому нельзя
        // доверять, нечем отличить от подделанного вместе с содержимым.
        if (Read(archive, SignatureEntry) is not { } signature
            || !trustedPublicKey.VerifyData(checksumBytes, signature, HashAlgorithmName.SHA256))
        {
            return new ModelPackageCheck(manifest, ModelPackageRejection.SignatureInvalid);
        }

        if (ParseChecksums(checksumBytes) is not { } declared)
        {
            return new ModelPackageCheck(manifest, ModelPackageRejection.ChecksumsUnreadable);
        }

        var present = archive.Entries
            .Select(entry => entry.FullName)
            .Where(name => !string.Equals(name, ChecksumsEntry, StringComparison.Ordinal)
                && !string.Equals(name, SignatureEntry, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        // Состав сверяется в обе стороны: лишний файл в пакете — такой же повод
        // для отказа, как пропавший. Иначе в подписанный пакет можно подложить.
        foreach (var name in present)
        {
            if (!declared.ContainsKey(name)
                || !Array.Exists(AllowedEntries, allowed => string.Equals(allowed, name, StringComparison.Ordinal)))
            {
                return new ModelPackageCheck(manifest, ModelPackageRejection.CompositionMismatch, name);
            }
        }

        foreach (var (name, expected) in declared)
        {
            if (!present.Contains(name))
            {
                return new ModelPackageCheck(manifest, ModelPackageRejection.CompositionMismatch, name);
            }

            if (Read(archive, name) is not { } content
                || !string.Equals(Hash(content), expected, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelPackageCheck(manifest, ModelPackageRejection.ContentAltered, name);
            }
        }

        if (!Version.TryParse(manifest.MinimumApplicationVersion, out var minimum))
        {
            return new ModelPackageCheck(
                manifest, ModelPackageRejection.ManifestUnreadable, manifest.MinimumApplicationVersion);
        }

        if (applicationVersion < minimum)
        {
            return new ModelPackageCheck(
                manifest, ModelPackageRejection.ApplicationTooOld, minimum.ToString());
        }

        if (!knownPreprocessing.Contains(manifest.PreprocessingVersion, StringComparer.Ordinal))
        {
            return new ModelPackageCheck(
                manifest, ModelPackageRejection.IncompatibleContract, manifest.PreprocessingVersion);
        }

        return knownLabelMaps.Contains(manifest.LabelMapVersion, StringComparer.Ordinal)
            ? new ModelPackageCheck(manifest, null)
            : new ModelPackageCheck(
                manifest, ModelPackageRejection.IncompatibleContract, manifest.LabelMapVersion);
    }

    private static byte[]? Read(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName);

        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static string Hash(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>
    /// Разбирает список хешей в привычном виде «хеш  имя».
    /// </summary>
    private static Dictionary<string, string>? ParseChecksums(byte[] content)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var line in Encoding.UTF8.GetString(content).Split('\n'))
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            var separator = trimmed.IndexOf("  ", StringComparison.Ordinal);

            if (separator <= 0)
            {
                return null;
            }

            var hash = trimmed[..separator];
            var name = trimmed[(separator + 2)..].Trim();

            if (hash.Length != 64 || name.Length == 0 || !declared.TryAdd(name, hash))
            {
                return null;
            }
        }

        return declared.Count > 0 ? declared : null;
    }

    /// <summary>
    /// Собирает строки списка хешей для набора файлов.
    /// </summary>
    /// <param name="files">Имя внутри пакета и содержимое.</param>
    /// <returns>Содержимое `checksums.sha256`.</returns>
    public static byte[] Checksums(IEnumerable<(string Name, byte[] Content)> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var builder = new StringBuilder();

        foreach (var (name, content) in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            builder.Append(CultureInfo.InvariantCulture, $"{Hash(content)}  {name}\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
