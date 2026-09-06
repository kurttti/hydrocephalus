using System.Globalization;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dicom;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Сеанс работы с зашифрованной рабочей копией (ADR 0006).
///
/// У каждого сеанса свой ключ данных, защищённый DPAPI. Ключ на сеанс, а не один
/// на установку, потому что удаление рабочей копии — это уничтожение её ключа:
/// побайтовая перезапись на SSD не гарантируется файловой системой, и
/// конфиденциальность обеспечивается именно недоступностью ключа. С общим ключом
/// удалить одну копию, не трогая остальные, было бы нечем.
///
/// Расширение зашифрованных файлов отличается от исходного намеренно: файл
/// с расширением .dcm, который не открывается как DICOM, выглядит повреждённым,
/// а не защищённым, и провоцирует «починить» его.
/// </summary>
public sealed class WorkingCopySession : IDisposable
{
    /// <summary>Расширение зашифрованных файлов рабочей копии.</summary>
    public const string EncryptedExtension = ".enc";

    /// <summary>Имя файла с защищённым ключом сеанса.</summary>
    public const string KeyFileName = "session.key";

    /// <summary>Имя файла с отметкой создания сеанса.</summary>
    public const string StampFileName = "session.stamp";

    private readonly byte[] key;
    private bool destroyed;

    private WorkingCopySession(string directory, byte[] key, DateTimeOffset createdAt)
    {
        this.Directory = directory;
        this.key = key;
        this.CreatedAt = createdAt;
    }

    /// <summary>Каталог сеанса.</summary>
    public string Directory { get; }

    /// <summary>Момент создания сеанса.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Открывает новый сеанс.
    /// </summary>
    /// <param name="root">Корень рабочих копий.</param>
    /// <param name="sessionId">Идентификатор сеанса; должен быть непрозрачным.</param>
    /// <param name="createdAt">Момент создания.</param>
    /// <param name="restrictAccess">Ограничивать ли доступ текущей учётной записью.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Открытый сеанс.</returns>
    public static async Task<WorkingCopySession> CreateAsync(
        string root,
        string sessionId,
        DateTimeOffset createdAt,
        bool restrictAccess,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var directory = Path.Combine(root, sessionId);

        ProtectedDirectory.Create(root, restrictAccess);
        ProtectedDirectory.Create(directory, restrictAccess);

        var key = await ProtectedSecretStore
            .GetOrCreateAsync(
                Path.Combine(directory, KeyFileName),
                WorkingCopyCipher.KeyLengthBytes,
                cancellationToken)
            .ConfigureAwait(false);

        // Отметка времени лежит рядом и в открытом виде: по ней работает
        // очистка по сроку, а датой создания сеанса ничего не выдаётся.
        var stampPath = Path.Combine(directory, StampFileName);

        if (!File.Exists(stampPath))
        {
            await File.WriteAllTextAsync(
                stampPath,
                createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }

        return new WorkingCopySession(directory, key, createdAt);
    }

    /// <summary>
    /// Записывает файл рабочей копии в зашифрованном виде.
    /// </summary>
    /// <param name="relativePath">Путь внутри сеанса.</param>
    /// <param name="content">Открытые данные.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Задача записи.</returns>
    public async Task WriteAsync(
        string relativePath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        this.EnsureUsable();
        ArgumentNullException.ThrowIfNull(content);

        var path = this.PathOf(relativePath);

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllBytesAsync(
            path,
            WorkingCopyCipher.Encrypt(this.key, content),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Читает файл рабочей копии.
    /// </summary>
    /// <param name="relativePath">Путь внутри сеанса.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Открытые данные.</returns>
    public async Task<byte[]> ReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        this.EnsureUsable();

        var content = await File.ReadAllBytesAsync(this.PathOf(relativePath), cancellationToken)
            .ConfigureAwait(false);

        return WorkingCopyCipher.Decrypt(this.key, content);
    }

    /// <summary>
    /// Перечисляет файлы рабочей копии по путям внутри сеанса.
    /// </summary>
    /// <param name="relativeDirectory">Каталог внутри сеанса.</param>
    /// <returns>Относительные пути без расширения шифрования.</returns>
    public IReadOnlyList<string> Enumerate(string relativeDirectory)
    {
        this.EnsureUsable();

        var directory = Path.Combine(this.Directory, relativeDirectory);

        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        return
        [
            .. System.IO.Directory
                .EnumerateFiles(directory, "*" + EncryptedExtension, SearchOption.TopDirectoryOnly)
                .Select(path => Path.GetRelativePath(this.Directory, path))
                .Select(path => path[..^EncryptedExtension.Length])
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Уничтожает сеанс: сначала ключ, затем данные.
    ///
    /// Порядок обязателен. Если удаление прервётся посередине — падение процесса,
    /// отключение питания, — данные уже нечитаемы. Обратный порядок оставил бы
    /// каталог, где ключ пережил файлы, которые он открывает.
    /// </summary>
    public void Destroy()
    {
        this.destroyed = true;

        ProtectedSecretStore.Destroy(Path.Combine(this.Directory, KeyFileName));

        Array.Clear(this.key);

        try
        {
            if (System.IO.Directory.Exists(this.Directory))
            {
                System.IO.Directory.Delete(this.Directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Каталог остаётся, но без ключа он бесполезен. Повторная попытка
            // выполняется очисткой при следующем старте.
        }
    }

    /// <summary>Уничтожает сеанс.</summary>
    public void Dispose() => this.Destroy();

    private string PathOf(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var full = Path.GetFullPath(Path.Combine(this.Directory, relativePath + EncryptedExtension));
        var root = Path.GetFullPath(this.Directory) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            // Путь, уводящий за пределы сеанса, означает либо ошибку сборки
            // имени, либо подставленное значение. И то и другое пишет файл
            // туда, где его никто не удалит.
            throw new ArgumentException(
                "The path leaves the session directory.",
                nameof(relativePath));
        }

        return full;
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(this.destroyed, this);
    }
}
