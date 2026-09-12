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

        // SOPInstanceUID уникален глобально, поэтому множество одно на весь обход.
        // Одна и та же серия, лежащая в выгрузке дважды, иначе дала бы каждому
        // срезу пару с нулевым расстоянием и прочиталась бы как два набора срезов.
        var acceptedInstances = new HashSet<string>(StringComparer.Ordinal);

        var walk = new ImportSourceWalk(this.options, rootDirectory);

        foreach (var file in walk.EnumerateFiles(rejections))
        {
            cancellationToken.ThrowIfCancellationRequested();

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
                    walk.OpaqueReference(file.FullName)));
                continue;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.UnreadableDataset,
                    walk.OpaqueReference(file.FullName)));
                continue;
            }

            var seriesUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);

            if (string.IsNullOrWhiteSpace(seriesUid))
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.MissingSeriesIdentifier,
                    walk.OpaqueReference(file.FullName)));
                continue;
            }

            var sopInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);

            if (!string.IsNullOrWhiteSpace(sopInstanceUid) && !acceptedInstances.Add(sopInstanceUid))
            {
                rejections.Add(new ImportRejection(
                    ImportRejectionCode.DuplicateInstance,
                    walk.OpaqueReference(file.FullName)));
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

    /// <summary>Накопитель срезов одной серии.</summary>
    private sealed class SeriesBuilder
    {
        private readonly DicomImportOptions options;
        private readonly DicomDataset first;

        // Положения нужны, чтобы вычислить шаг между срезами: SliceThickness
        // отвечает на другой вопрос и при зазоре расходится с ним. Вместе с ними
        // собираются оси, по которым серия может распадаться на несколько наборов
        // срезов, — без них «совпадающие положения» нечем объяснить.
        private readonly List<SliceSample> samples = [];

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

        internal void AddInstance(DicomDataset dataset) =>
            this.samples.Add(DicomGeometryReader.ReadSample(dataset));

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

            var geometry = this.BuildGeometry(seriesId, findings);

            return new ImagingSeries
            {
                PseudonymousSeriesId = seriesId,
                Geometry = geometry,
                Weighting = SeriesClassification.DetectWeighting(description),
                IsContrastEnhanced = SeriesClassification.LooksContrastEnhanced(description),
            };
        }

        private SeriesGeometry BuildGeometry(string seriesId, List<SeriesFinding> findings)
        {
            // Первое чтение даёт толщину и направляющие косинусы; шаг между срезами
            // из тегов одного среза не выводится и подставляется следом.
            var geometry = DicomGeometryReader.Read(
                this.first,
                this.samples.Count,
                sliceSpacingMillimetres: 0);

            var positioning = SlicePositions.Analyse(
                this.samples,
                geometry.RowDirection,
                geometry.ColumnDirection,
                geometry.SliceThicknessMillimetres);

            foreach (var issue in SliceGeometryChecks.Inspect(positioning))
            {
                findings.Add(new SeriesFinding(seriesId, issue));
            }

            return geometry with { SliceSpacingMillimetres = positioning.SpacingMillimetres };
        }
    }
}
