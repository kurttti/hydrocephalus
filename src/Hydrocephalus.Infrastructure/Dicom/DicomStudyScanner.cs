using FellowOakDicom;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Обходит произвольную структуру каталогов и собирает исследования из DICOM-файлов.
///
/// Реальный экспорт неоднороден (docs/data/README.md): встречается вложенная структура
/// вида PA*/ST*/SE*, единичная обёрточная папка на пациента и плоский список файлов без
/// структуры вообще. Поэтому группировка идёт исключительно по SeriesInstanceUID
/// и StudyInstanceUID, а имена папок не используются как источник смысла.
/// </summary>
public sealed class DicomStudyScanner
{
    private readonly DicomImportOptions options;

    /// <summary>Создаёт сканер.</summary>
    /// <param name="options">Ограничения приёма и соль псевдонимизации.</param>
    public DicomStudyScanner(DicomImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>
    /// Обходит каталог и собирает исследования.
    /// </summary>
    /// <param name="rootDirectory">Корневой каталог обхода.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Разобранные исследования, отклонённые файлы и замечания.</returns>
    public async Task<DicomScanResult> ScanAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var rejections = new List<ImportRejection>();
        var findings = new List<SeriesFinding>();
        var seriesBuilders = new Dictionary<string, SeriesBuilder>(StringComparer.Ordinal);

        var inspected = 0;

        foreach (var file in this.EnumerateCandidateFiles(rootDirectory, rejections))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++inspected > this.options.MaxFileCount)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.TooManyFiles,
                    this.OpaqueReference(rootDirectory, file.FullName)));
                break;
            }

            if (file.Length > this.options.MaxFileSizeBytes)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.FileTooLarge,
                    this.OpaqueReference(rootDirectory, file.FullName)));
                continue;
            }

            DicomDataset dataset;

            try
            {
                var dicomFile = await DicomFile.OpenAsync(file.FullName).ConfigureAwait(false);
                dataset = dicomFile.Dataset;
            }
            catch (DicomFileException)
            {
                // Посторонние файлы в каталоге со снимками — норма для реального экспорта:
                // в одном из источников рядом лежали клинические документы.
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.NotADicomFile,
                    this.OpaqueReference(rootDirectory, file.FullName)));
                continue;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.UnreadableDataset,
                    this.OpaqueReference(rootDirectory, file.FullName)));
                continue;
            }

            var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);

            if (string.IsNullOrWhiteSpace(seriesUid))
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.MissingSeriesIdentifier,
                    this.OpaqueReference(rootDirectory, file.FullName)));
                continue;
            }

            if (!seriesBuilders.TryGetValue(seriesUid, out var builder))
            {
                builder = new SeriesBuilder(dataset, this.options);
                seriesBuilders[seriesUid] = builder;
            }

            builder.AddInstance(dataset);
        }

        var studies = BuildStudies(seriesBuilders.Values, findings);

        return new DicomScanResult
        {
            Studies = studies,
            Rejections = rejections,
            Findings = findings,
        };
    }

    private static List<ImagingStudy> BuildStudies(
        IEnumerable<SeriesBuilder> builders,
        List<SeriesFinding> findings)
    {
        var byStudy = new Dictionary<(string StudyUid, string SubjectId), List<ImagingSeries>>();

        foreach (var builder in builders)
        {
            var series = builder.Build(findings);
            var key = (builder.StudyInstanceUid, builder.PseudonymousSubjectId);

            if (!byStudy.TryGetValue(key, out var list))
            {
                list = [];
                byStudy[key] = list;
            }

            list.Add(series);
        }

        return byStudy
            .Select(entry => new ImagingStudy
            {
                PseudonymousStudyId = entry.Key.StudyUid,
                PseudonymousSubjectId = entry.Key.SubjectId,
                Series = entry.Value,
            })
            .ToList();
    }

    /// <summary>
    /// Перечисляет файлы-кандидаты, соблюдая лимит глубины вложенности.
    /// Обход написан вручную, а не через рекурсивный поиск по маске: слишком глубокая
    /// структура должна давать отказ с кодом, а не необработанное исключение.
    /// </summary>
    private IEnumerable<FileInfo> EnumerateCandidateFiles(
        string rootDirectory,
        List<ImportRejection> rejections)
    {
        var queue = new Queue<(DirectoryInfo Directory, int Depth)>();
        queue.Enqueue((new DirectoryInfo(rootDirectory), 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            if (depth > this.options.MaxDirectoryDepth)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.DirectoryTooDeep,
                    this.OpaqueReference(rootDirectory, directory.FullName)));
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
                    this.OpaqueReference(rootDirectory, directory.FullName)));
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

    /// <summary>
    /// Строит непрозрачную ссылку на путь. Сам путь не сохраняется: имена файлов и папок
    /// в исходном сборе содержат фамилии пациентов и потому являются PHI.
    /// </summary>
    private string OpaqueReference(string rootDirectory, string path)
    {
        var relative = Path.GetRelativePath(rootDirectory, path);
        return Pseudonyms.Derive(this.options.PseudonymSalt, "path", relative);
    }

    /// <summary>Накопитель срезов одной серии.</summary>
    private sealed class SeriesBuilder
    {
        private readonly DicomImportOptions options;
        private readonly DicomDataset first;
        private int instanceCount;

        internal SeriesBuilder(DicomDataset first, DicomImportOptions options)
        {
            this.first = first;
            this.options = options;

            this.StudyInstanceUid = Pseudonyms.Derive(
                options.PseudonymSalt,
                "study",
                first.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));

            this.PseudonymousSubjectId = Pseudonyms.DeriveSubjectId(
                options.PseudonymSalt,
                first.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
                first.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
                first.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
                first.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty));
        }

        internal string StudyInstanceUid { get; }

        internal string PseudonymousSubjectId { get; }

        internal void AddInstance(DicomDataset dataset)
        {
            _ = dataset;
            this.instanceCount++;
        }

        internal ImagingSeries Build(List<SeriesFinding> findings)
        {
            var seriesId = Pseudonyms.Derive(
                this.options.PseudonymSalt,
                "series",
                this.first.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty));

            var description = this.first.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty);

            foreach (var issue in DicomMetadataChecks.Inspect(this.first))
            {
                findings.Add(new SeriesFinding(seriesId, issue));
            }

            return new ImagingSeries
            {
                PseudonymousSeriesId = seriesId,
                Geometry = DicomGeometryReader.Read(this.first, this.instanceCount),
                Weighting = SeriesClassification.DetectWeighting(description),
                IsContrastEnhanced = SeriesClassification.LooksContrastEnhanced(description),
            };
        }
    }
}
