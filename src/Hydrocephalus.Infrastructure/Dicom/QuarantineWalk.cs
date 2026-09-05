namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Обход каталога приёмки с ограничениями из <see cref="DicomImportOptions"/>.
///
/// Обход вынесен отдельно, потому что его выполняют двое: разбор структуры
/// (<see cref="DicomStudyScanner"/>) и создание рабочей копии
/// (<see cref="StudyImporter"/>). Дублировать проверки глубины, размера и числа
/// файлов нельзя: разойдясь, они дали бы импорт, принимающий то, что разбор
/// отклонил.
///
/// Обход написан явной очередью, а не рекурсивным поиском по маске: слишком
/// глубокая структура должна давать отказ с кодом, а не необработанное исключение.
/// </summary>
internal sealed class QuarantineWalk
{
    private readonly DicomImportOptions options;
    private readonly string rootDirectory;

    /// <summary>Создаёт обход.</summary>
    /// <param name="options">Ограничения приёма и соль псевдонимизации.</param>
    /// <param name="rootDirectory">Корневой каталог обхода.</param>
    internal QuarantineWalk(DicomImportOptions options, string rootDirectory)
    {
        this.options = options;
        this.rootDirectory = rootDirectory;
    }

    /// <summary>
    /// Перечисляет файлы, прошедшие ограничения приёма.
    /// </summary>
    /// <param name="rejections">Список, пополняемый отказами.</param>
    /// <returns>Файлы-кандидаты.</returns>
    internal IEnumerable<FileInfo> EnumerateFiles(List<ImportRejection> rejections)
    {
        var inspected = 0;

        foreach (var file in this.EnumerateAllFiles(rejections))
        {
            if (++inspected > this.options.MaxFileCount)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.TooManyFiles,
                    this.OpaqueReference(file.FullName)));
                yield break;
            }

            if (file.Length > this.options.MaxFileSizeBytes)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.FileTooLarge,
                    this.OpaqueReference(file.FullName)));
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    /// Строит непрозрачную ссылку на путь.
    ///
    /// Сам путь не сохраняется: имена файлов и папок в исходном сборе содержат
    /// фамилии пациентов и потому являются PHI. Правило относится к результатам,
    /// журналам и записям аудита — но не к переменным внутри импорта, которому
    /// путь нужен, чтобы открыть файл.
    /// </summary>
    /// <param name="path">Полный путь.</param>
    /// <returns>Устойчивая непрозрачная ссылка.</returns>
    internal string OpaqueReference(string path)
    {
        var relative = Path.GetRelativePath(this.rootDirectory, path);
        return Pseudonyms.Derive(this.options.PseudonymSalt, "path", relative);
    }

    private IEnumerable<FileInfo> EnumerateAllFiles(List<ImportRejection> rejections)
    {
        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        queue.Enqueue((new DirectoryInfo(this.rootDirectory), 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            if (depth > this.options.MaxDirectoryDepth)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.DirectoryTooDeep,
                    this.OpaqueReference(directory.FullName)));
                continue;
            }

            FileInfo[] files;
            DirectoryInfo[] subdirectories;

            try
            {
                files = directory.GetFiles();
                subdirectories = directory.GetDirectories();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.UnreadableDataset,
                    this.OpaqueReference(directory.FullName)));
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                queue.Enqueue((subdirectory, depth + 1));
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
