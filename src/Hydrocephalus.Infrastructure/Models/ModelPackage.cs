using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hydrocephalus.Domain.Abstractions;

namespace Hydrocephalus.Infrastructure.Models;

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
    /// <summary>
    /// Манифест, как он лежит в файле.
    ///
    /// Отдельный от доменного тип: имена полей в JSON — забота формата, а не
    /// договора. Домену незачем знать, как пакет записан на диск.
    /// </summary>
    private sealed record ManifestFile(
        [property: JsonPropertyName("formatVersion")] string FormatVersion,
        [property: JsonPropertyName("modelVersion")] string ModelVersion,
        [property: JsonPropertyName("minimumApplicationVersion")] string MinimumApplicationVersion,
        [property: JsonPropertyName("preprocessingVersion")] string PreprocessingVersion,
        [property: JsonPropertyName("labelMapVersion")] string LabelMapVersion,
        [property: JsonPropertyName("signingKeyId")] string SigningKeyId);

    /// <summary>Настройки записи манифеста: читаемый отступ, один экземпляр.</summary>
    private static readonly JsonSerializerOptions ManifestFormat = new() { WriteIndented = true };

    /// <summary>Версия формата, которую понимает эта сборка.</summary>
    public const string SupportedFormatVersion = "1";

    /// <summary>Имя манифеста внутри пакета.</summary>
    public const string ManifestEntry = "manifest.json";

    /// <summary>Имя списка хешей.</summary>
    public const string ChecksumsEntry = "checksums.sha256";

    /// <summary>Имя отделённой подписи списка хешей.</summary>
    public const string SignatureEntry = "checksums.sha256.sig";

    /// <summary>Имя карточки модели.</summary>
    public const string ModelCardEntry = "model-card.md";

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
        ModelCardEntry,
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
    /// <param name="withWeights">
    /// Отдать ли веса сегментации вместе с вердиктом. Экрану установки они не
    /// нужны — это сотня мегабайт на решение, которому они ни к чему, — а
    /// загрузке нужны именно те байты, у которых здесь сошёлся хеш.
    /// </param>
    public static ModelPackageCheck Verify(
        string packagePath,
        ECDsa trustedPublicKey,
        Version applicationVersion,
        IReadOnlyCollection<string> knownPreprocessing,
        IReadOnlyCollection<string> knownLabelMaps,
        bool withWeights = false)
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
            return Inspect(
                archive,
                trustedPublicKey,
                applicationVersion,
                knownPreprocessing,
                knownLabelMaps,
                withWeights);
        }
    }

    private static ModelPackageCheck Inspect(
        ZipArchive archive,
        ECDsa trustedPublicKey,
        Version applicationVersion,
        IReadOnlyCollection<string> knownPreprocessing,
        IReadOnlyCollection<string> knownLabelMaps,
        bool withWeights)
    {
        byte[]? weights = null;
        var modelCard = string.Empty;

        // Состояние подписи несётся явно, а не выводится потом из кода отказа:
        // «список хешей не разбирается» встречается и до проверки подписи, и
        // после неё, и по коду эти два случая не отличить. Экран установки
        // показывает это состояние человеку, и догадка там была бы заявлением,
        // которого проверка не делала.
        var signatureState = ModelPackageSignature.NotChecked;

        ModelPackageCheck Verdict(
            ModelPackageManifest? manifest,
            ModelPackageRejection? rejection,
            string detail = "") =>
            new(manifest, rejection, detail)
            {
                Signature = signatureState,
                ModelCard = modelCard,
                Weights = rejection is null ? weights : null,
            };

        if (Read(archive, ManifestEntry) is not { } manifestBytes)
        {
            return Verdict(null, ModelPackageRejection.ManifestUnreadable);
        }

        ModelPackageManifest? manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<ManifestFile>(manifestBytes) is { } file
                ? new ModelPackageManifest(
                    file.FormatVersion,
                    file.ModelVersion,
                    file.MinimumApplicationVersion,
                    file.PreprocessingVersion,
                    file.LabelMapVersion,
                    file.SigningKeyId)
                : null;
        }
        catch (JsonException)
        {
            return Verdict(null, ModelPackageRejection.ManifestUnreadable);
        }

        if (manifest is null
            || !string.Equals(manifest.FormatVersion, SupportedFormatVersion, StringComparison.Ordinal))
        {
            return Verdict(
                null, ModelPackageRejection.ManifestUnreadable, manifest?.FormatVersion ?? string.Empty);
        }

        if (Read(archive, ChecksumsEntry) is not { } checksumBytes)
        {
            return Verdict(manifest, ModelPackageRejection.ChecksumsUnreadable);
        }

        // Подпись проверяется раньше самих хешей: список хешей, которому нельзя
        // доверять, нечем отличить от подделанного вместе с содержимым.
        if (Read(archive, SignatureEntry) is not { } signature
            || !trustedPublicKey.VerifyData(checksumBytes, signature, HashAlgorithmName.SHA256))
        {
            signatureState = ModelPackageSignature.Invalid;

            return Verdict(manifest, ModelPackageRejection.SignatureInvalid);
        }

        signatureState = ModelPackageSignature.Valid;

        if (ParseChecksums(checksumBytes) is not { } declared)
        {
            return Verdict(manifest, ModelPackageRejection.ChecksumsUnreadable);
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
                return Verdict(manifest, ModelPackageRejection.CompositionMismatch, name);
            }
        }

        foreach (var (name, expected) in declared)
        {
            if (!present.Contains(name))
            {
                return Verdict(manifest, ModelPackageRejection.CompositionMismatch, name);
            }

            if (Read(archive, name) is not { } content
                || !string.Equals(Hash(content), expected, StringComparison.OrdinalIgnoreCase))
            {
                return Verdict(manifest, ModelPackageRejection.ContentAltered, name);
            }

            // Веса запоминаются здесь, а не читаются заново после проверки:
            // второе чтение файла оставило бы между проверкой и загрузкой щель,
            // в которую и пролезает подмена модели.
            if (withWeights && string.Equals(name, SegmentationEntry, StringComparison.Ordinal))
            {
                weights = content;
            }

            // Карточка — по той же причине и тем же проходом: показанная
            // администратору из второго чтения архива, она описывала бы не
            // обязательно тот пакет, который проверен. Читается всегда:
            // это текст, а не сотня мегабайт, и экрану установки он нужен.
            if (string.Equals(name, ModelCardEntry, StringComparison.Ordinal))
            {
                modelCard = Encoding.UTF8.GetString(content).TrimEnd();
            }
        }

        if (!Version.TryParse(manifest.MinimumApplicationVersion, out var minimum))
        {
            return Verdict(
                manifest, ModelPackageRejection.ManifestUnreadable, manifest.MinimumApplicationVersion);
        }

        if (applicationVersion < minimum)
        {
            return Verdict(
                manifest, ModelPackageRejection.ApplicationTooOld, minimum.ToString());
        }

        if (!knownPreprocessing.Contains(manifest.PreprocessingVersion, StringComparer.Ordinal))
        {
            return Verdict(
                manifest, ModelPackageRejection.IncompatibleContract, manifest.PreprocessingVersion);
        }

        return knownLabelMaps.Contains(manifest.LabelMapVersion, StringComparer.Ordinal)
            ? Verdict(manifest, null)
            : Verdict(
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
    /// Записывает манифест в том виде, в каком его читает <see cref="Verify"/>.
    ///
    /// Запись и чтение держатся рядом намеренно: имена полей в файле — забота
    /// формата, и разойдясь, они дали бы пакет, который не читается собственным
    /// же приложением.
    /// </summary>
    /// <param name="manifest">Объявление пакета.</param>
    /// <returns>Содержимое `manifest.json`.</returns>
    public static byte[] WriteManifest(ModelPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return JsonSerializer.SerializeToUtf8Bytes(
            new ManifestFile(
                manifest.FormatVersion,
                manifest.ModelVersion,
                manifest.MinimumApplicationVersion,
                manifest.PreprocessingVersion,
                manifest.LabelMapVersion,
                manifest.SigningKeyId),
            ManifestFormat);
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
