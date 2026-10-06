using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Infrastructure.Models;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Проверка пакета модели до чтения весов.
///
/// Подмена модели названа в `docs/security/README.md` отдельной угрозой, и ADR
/// 0004 перечисляет случаи, каждый из которых обязан дать свой код отказа:
/// изменённый байт, пропавший файл, лишний файл, неверная подпись, подпись чужим
/// ключом, несовместимая версия. Здесь они и проверяются.
///
/// Ключ подписи порождается в тесте: приватный ключ выпуска лежит вне
/// репозитория, и в сборочных артефактах его быть не должно.
/// </summary>
public sealed class ModelPackageTests : IDisposable
{
    private static readonly Version Application = new(1, 4, 0);
    private static readonly string[] KnownPreprocessing = ["conform-1"];
    private static readonly string[] KnownLabelMaps = ["ventricles-1"];

    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("hydrocephalus-package-");
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void A_sound_package_is_accepted()
    {
        var path = Build();

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Null(check.Rejection);
        Assert.Equal("vinn-axial-2.0.0", check.Manifest?.ModelVersion);
    }

    [Fact]
    public void An_altered_byte_in_the_weights_is_refused()
    {
        var path = Build(corrupt: true);

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.ContentAltered, check.Rejection);
        Assert.Equal(ModelPackage.SegmentationEntry, check.Detail);
    }

    [Fact]
    public void A_missing_file_is_refused()
    {
        var path = Build(omit: "labels.json");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.CompositionMismatch, check.Rejection);
        Assert.Equal("labels.json", check.Detail);
    }

    [Fact]
    public void An_extra_file_is_refused()
    {
        // Лишний файл в подписанном пакете — такая же подмена, как изменённый:
        // иначе в него можно подложить что угодно, не трогая подпись.
        var path = Build(extra: "readme.txt");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.CompositionMismatch, check.Rejection);
        Assert.Equal("readme.txt", check.Detail);
    }

    [Fact]
    public void A_file_with_an_unknown_name_is_refused_even_when_declared()
    {
        // Перечень имён закрытый: подписанный пакет с посторонним файлом всё
        // ещё подписан, но подпись не говорит, что этот файл кто-то смотрел.
        var path = Build(extraDeclared: "notes.md");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.CompositionMismatch, check.Rejection);
        Assert.Equal("notes.md", check.Detail);
    }

    [Fact]
    public void A_package_signed_by_another_key_is_refused()
    {
        var path = Build();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var check = ModelPackage.Verify(path, stranger, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.SignatureInvalid, check.Rejection);
    }

    [Fact]
    public void A_package_without_a_signature_is_refused()
    {
        var path = Build(unsigned: true);

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.SignatureInvalid, check.Rejection);
    }

    [Fact]
    public void A_package_that_wants_a_newer_application_is_refused()
    {
        var path = Build(minimumApplication: "2.0.0");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.ApplicationTooOld, check.Rejection);
        Assert.Equal("2.0.0", check.Detail);
    }

    [Fact]
    public void An_unknown_preprocessing_version_is_refused()
    {
        var path = Build(preprocessing: "conform-99");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.IncompatibleContract, check.Rejection);
        Assert.Equal("conform-99", check.Detail);
    }

    [Fact]
    public void Something_that_is_not_a_package_is_refused()
    {
        var path = Path.Combine(this.root.FullName, "not-a-package.hcmp");

        File.WriteAllText(path, "это не архив");

        var check = ModelPackage.Verify(path, this.key, Application, KnownPreprocessing, KnownLabelMaps);

        Assert.Equal(ModelPackageRejection.NotAPackage, check.Rejection);
    }

    public void Dispose()
    {
        this.key.Dispose();
        this.root.Delete(recursive: true);
    }

    private string Build(
        bool corrupt = false,
        bool unsigned = false,
        string? omit = null,
        string? extra = null,
        string minimumApplication = "1.0.0",
        string preprocessing = "conform-1",
        string? extraDeclared = null)
    {
        var manifest = ModelPackage.WriteManifest(new ModelPackageManifest(
            FormatVersion: ModelPackage.SupportedFormatVersion,
            ModelVersion: "vinn-axial-2.0.0",
            MinimumApplicationVersion: minimumApplication,
            PreprocessingVersion: preprocessing,
            LabelMapVersion: "ventricles-1",
            SigningKeyId: "test-key"));

        var files = new List<(string Name, byte[] Content)>
        {
            (ModelPackage.ManifestEntry, manifest),
            (ModelPackage.SegmentationEntry, [1, 2, 3, 4]),
            ("preprocessing.json", Encoding.UTF8.GetBytes("{}")),
            ("labels.json", Encoding.UTF8.GetBytes("{}")),
            ("model-card.md", Encoding.UTF8.GetBytes("# карточка")),
        };

        if (extraDeclared is not null)
        {
            files.Add((extraDeclared, Encoding.UTF8.GetBytes("посторонний")));
        }

        // Хеши считаются по полному составу, а в архив пропущенный файл не
        // кладётся: именно так выглядит пропажа файла из подписанного пакета.
        var checksums = ModelPackage.Checksums(files);
        var path = Path.Combine(this.root.FullName, "model.hcmp");

        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in files.Where(file => file.Name != omit))
            {
                // Порча вносится после подсчёта хешей: так ведёт себя подмена.
                var written = corrupt && name == ModelPackage.SegmentationEntry
                    ? new byte[] { 1, 2, 3, 9 }
                    : content;

                Write(archive, name, written);
            }

            if (extra is not null)
            {
                Write(archive, extra, Encoding.UTF8.GetBytes("лишнее"));
            }

            Write(archive, ModelPackage.ChecksumsEntry, checksums);

            if (!unsigned)
            {
                Write(
                    archive,
                    ModelPackage.SignatureEntry,
                    this.key.SignData(checksums, HashAlgorithmName.SHA256));
            }
        }

        return path;
    }

    private static void Write(ZipArchive archive, string name, byte[] content)
    {
        using var entry = archive.CreateEntry(name).Open();

        entry.Write(content);
    }
}
