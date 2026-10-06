using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hydrocephalus.Infrastructure.Models;
using Hydrocephalus.ModelPackageBuild;

// Сборка пакета модели (.hcmp) по ADR 0004.
//
// Веса в репозиторий не входят и CI их не качает: пакет собирается на машине,
// где они лежат, и подписывается ключом выпуска. Без ключа пакет собирается
// неподписанным — приложение такой отвергнет, и это намеренно: неподписанный
// пакет годится только для проверки сборки.
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine(
        "Использование: Hydrocephalus.ModelPackageBuild --onnx <модель.onnx> --out <пакет.hcmp>"
        + " --model-version <версия> [--key <ключ.pem>] [--min-app <версия>]");

    return args.Length == 0 ? 2 : 0;
}

static string? Option(string[] arguments, string name)
{
    var index = Array.IndexOf(arguments, name);

    return index >= 0 && index < arguments.Length - 1 ? arguments[index + 1] : null;
}

var onnxPath = Option(args, "--onnx");
var outputPath = Option(args, "--out");
var modelVersion = Option(args, "--model-version");
var keyPath = Option(args, "--key");
var minimumApplication = Option(args, "--min-app") ?? "1.0.0";

if (onnxPath is null || outputPath is null || modelVersion is null)
{
    Console.Error.WriteLine("Нужны --onnx, --out и --model-version.");

    return 2;
}

if (!File.Exists(onnxPath))
{
    Console.Error.WriteLine($"Нет файла модели: {onnxPath}");

    return 2;
}

var manifest = JsonSerializer.SerializeToUtf8Bytes(
    new ModelPackageManifest(
        FormatVersion: ModelPackage.SupportedFormatVersion,
        ModelVersion: modelVersion,
        MinimumApplicationVersion: minimumApplication,
        PreprocessingVersion: PackageContent.PreprocessingVersion,
        LabelMapVersion: PackageContent.LabelMapVersion,
        SigningKeyId: keyPath is null ? "unsigned" : Path.GetFileNameWithoutExtension(keyPath)),
    new JsonSerializerOptions { WriteIndented = true });

var files = new List<(string Name, byte[] Content)>
{
    (ModelPackage.ManifestEntry, manifest),
    (ModelPackage.SegmentationEntry, File.ReadAllBytes(onnxPath)),
    ("preprocessing.json", Encoding.UTF8.GetBytes(PackageContent.Preprocessing)),
    ("labels.json", Encoding.UTF8.GetBytes(PackageContent.Labels)),
    ("model-card.md", Encoding.UTF8.GetBytes(PackageContent.ModelCard(modelVersion))),
};

var checksums = ModelPackage.Checksums(files);

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

using (var stream = File.Create(outputPath))
using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (var (name, content) in files)
    {
        using var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open();

        entry.Write(content);
    }

    using (var entry = archive.CreateEntry(ModelPackage.ChecksumsEntry).Open())
    {
        entry.Write(checksums);
    }

    if (keyPath is not null)
    {
        using var key = ECDsa.Create();

        key.ImportFromPem(File.ReadAllText(keyPath));

        using var entry = archive.CreateEntry(ModelPackage.SignatureEntry).Open();

        entry.Write(key.SignData(checksums, HashAlgorithmName.SHA256));
    }
}

var size = new FileInfo(outputPath).Length / (1024.0 * 1024.0);

Console.WriteLine(
    string.Create(
        CultureInfo.InvariantCulture,
        $"Собран {outputPath}: {files.Count + 1} файлов, {size:0.0} МБ."));

if (keyPath is null)
{
    Console.WriteLine("Пакет не подписан: приложение его отвергнет. Для выпуска нужен --key.");
}

return 0;
