using System.Globalization;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dataset;

/// <summary>
/// Хранилище манифестов датасета в файлах.
///
/// Каждый экспорт кладётся отдельным файлом с отметкой времени в имени.
/// Перезаписывать прошлый манифест нельзя: по нему уже могла быть обучена
/// модель, и подмена состава выборки задним числом делает результат
/// невоспроизводимым — молча.
/// </summary>
public sealed class FileDatasetManifestStore : IDatasetManifestStore
{
    private readonly string rootDirectory;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Создаёт хранилище.
    /// </summary>
    /// <param name="rootDirectory">Каталог, в который складываются манифесты.</param>
    /// <param name="timeProvider">Источник времени для имени файла.</param>
    public FileDatasetManifestStore(string rootDirectory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.rootDirectory = rootDirectory;
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Записывает манифест.
    /// </summary>
    /// <param name="studies">Исследования, попадающие в манифест.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Полный путь записанного файла.</returns>
    /// <exception cref="IOException">Если файл с таким именем уже существует.</exception>
    public async Task<string> WriteAsync(
        IReadOnlyList<ImagingStudy> studies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(studies);

        var stamp = this.timeProvider.GetUtcNow()
            .ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture);

        var path = Path.Combine(this.rootDirectory, $"manifest-{stamp}.json");

        Directory.CreateDirectory(this.rootDirectory);

        var content = DatasetManifestWriter.Serialize(studies);

        // CreateNew: столкновение имён означало бы, что один экспорт затирает
        // другой, и по манифесту нельзя было бы восстановить, на чём училась
        // модель.
        await using (var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        {
            await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }
}
