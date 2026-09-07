using System.Reflection;
using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Reporting;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Composition;

/// <summary>
/// Сборка приложения из реализаций портов.
///
/// Связывание выполняется конструкторами вручную, без контейнера: объектов пять,
/// и контейнер здесь добавил бы зависимость и косвенность, ничего не упростив.
/// Он появится, когда появятся ViewModel со своим жизненным циклом.
///
/// Это единственное место, где слои встречаются (docs/architecture/README.md):
/// UI получает готовый сценарий и не знает ни о fo-dicom, ни о том, где лежат файлы.
/// </summary>
public sealed class CompositionRoot : IDisposable
{
    private readonly HashChainAuditLog auditLog;

    private readonly StudyImporter importer;

    private CompositionRoot(
        Hydrocephalus.Application.AnalyzeStudyUseCase analyzeStudy,
        Hydrocephalus.Application.ExportDatasetManifestUseCase exportDatasetManifest,
        StudyImporter importer,
        HashChainAuditLog auditLog,
        PipelineIdentity pipeline,
        Actor actor)
    {
        this.AnalyzeStudy = analyzeStudy;
        this.ExportDatasetManifest = exportDatasetManifest;
        this.importer = importer;
        this.auditLog = auditLog;
        this.Pipeline = pipeline;
        this.Actor = actor;
    }

    /// <summary>Сценарий анализа исследования.</summary>
    public Hydrocephalus.Application.AnalyzeStudyUseCase AnalyzeStudy { get; }

    /// <summary>
    /// Экспорт манифеста датасета в исследовательский контур.
    ///
    /// Собран, но ни один экран его не вызывает, и это намеренно. Экспорт
    /// выборки наружу — действие исследователя, а не врача, и открывать его
    /// в клиническом интерфейсе можно только вместе с разграничением ролей,
    /// которого пока нет (ADR 0005: разграничение выполняется на уровне
    /// сценария и фиксируется в аудите).
    /// </summary>
    public Hydrocephalus.Application.ExportDatasetManifestUseCase ExportDatasetManifest { get; }

    /// <summary>Версии конвейера, попадающие в отчёт.</summary>
    public PipelineIdentity Pipeline { get; }

    /// <summary>
    /// Тот, от чьего имени выполняются операции.
    ///
    /// Собирается один раз при запуске и дальше не меняется: роль задаётся
    /// установкой, а не выбирается в интерфейсе (см. <see cref="ActorSettings"/>).
    /// </summary>
    public Actor Actor { get; }

    /// <summary>
    /// Собирает приложение.
    /// </summary>
    /// <param name="paths">Расположение локальных каталогов.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Собранное приложение.</returns>
    public static async Task<CompositionRoot> CreateAsync(
        ApplicationPaths paths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // Соль читается или создаётся до всего остального: без неё импорт
        // не может построить ни одного псевдонима, и запускаться незачем.
        var salt = await PseudonymSaltStore
            .GetOrCreateAsync(paths.PseudonymSaltPath, cancellationToken)
            .ConfigureAwait(false);

        var pipeline = DescribePipeline();

        var auditLog = new HashChainAuditLog(paths.AuditLogPath);

        var importer = new StudyImporter(
            new DicomImportOptions { PseudonymSalt = salt },
            new WorkingCopyOptions { RootDirectory = paths.WorkingCopyRoot });

        // Уборка до начала любой новой работы: осиротевшие рабочие копии
        // от прерванных сеансов сами не исчезнут — сеанс, который должен был
        // их удалить, уже не выполняется (ADR 0006).
        WorkingCopyRetention.Sweep(paths.WorkingCopyRoot, TimeProvider.System.GetUtcNow());

        var useCase = new Hydrocephalus.Application.AnalyzeStudyUseCase(
            importer,
            importer,
            new QualityControlOnlyEngine(new InputQualityControl(), pipeline),
            new JsonReportStore(paths.ReportRoot),
            auditLog,
            TimeProvider.System);

        var exportDatasetManifest = new Hydrocephalus.Application.ExportDatasetManifestUseCase(
            new FileDatasetManifestStore(paths.DatasetManifestRoot, TimeProvider.System),
            auditLog,
            TimeProvider.System);

        // Файл настроек лежит рядом с исполняемым файлом, а не в профиле
        // пользователя: роль задаёт тот, кто разворачивает приложение, и она
        // не должна меняться от того, под кем оно запущено.
        var actor = ActorSettings.Read(AppContext.BaseDirectory);

        return new CompositionRoot(useCase, exportDatasetManifest, importer, auditLog, pipeline, actor);
    }

    /// <summary>
    /// Открывает исследование для просмотра: импорт с деидентификацией, загрузка
    /// объёма выбранной серии и baseline-сегментация.
    ///
    /// Сборка живёт здесь, а не в окне: окно не читает DICOM и не знает, из каких
    /// слоёв берётся картинка (docs/architecture/README.md). Отдельного сценария
    /// в Application для этого пока нет — он появится вместе с портом загрузки
    /// объёма, когда просмотр перестанет быть единственным его потребителем.
    /// </summary>
    /// <param name="sourceDirectory">Каталог с исходными файлами.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Состояние экрана просмотра.</returns>
    public async Task<StudyView> OpenForViewingAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var workingCopy = await this.importer.ImportAsync(sourceDirectory, cancellationToken)
            .ConfigureAwait(false);

        // Показывается та же серия, которую взял бы анализ: смотреть одно,
        // а измерять другое нельзя.
        var series = workingCopy.Study.Series
            .Where(item => !item.IsContrastEnhanced && item.Tier != AcquisitionTier.Unusable)
            .OrderByDescending(item => item.Tier)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The study has no series suitable for viewing.");

        var volume = await DicomVolumeReader
            .LoadAsync(
                this.importer.SessionFor(workingCopy.VolumeReference),
                series.PseudonymousSeriesId,
                cancellationToken)
            .ConfigureAwait(false);

        // Маска строится только когда взвешенность известна: без неё
        // baseline-сегментация отказывается работать, и это не повод
        // не показать изображение.
        VoxelMask? mask = null;

        if (series.Weighting != SeriesWeighting.Unknown)
        {
            mask = BaselineVentricleSegmentation
                .Segment(volume, series.Weighting, cancellationToken: cancellationToken)
                .Mask;
        }

        return new StudyView(volume, mask);
    }

    /// <summary>Освобождает ресурсы собранных реализаций.</summary>
    public void Dispose() => this.auditLog.Dispose();

    /// <summary>
    /// Описывает версии конвейера.
    ///
    /// Предобработка, схема признаков и label map отмечены как нереализованные,
    /// а не проставлены версией: этих этапов в конвейере ещё нет, и «1.0.0»
    /// в таком поле — заявка на воспроизводимость, которой не существует.
    ///
    /// Commit SHA берётся из атрибута сборки. Он называет коммит, из которого
    /// собрано приложение, но не состояние рабочего дерева: сборка с
    /// незакоммиченными правками сошлётся на предыдущий коммит. Для сборок
    /// из CI это точное значение, и именно они попадают к врачу.
    /// </summary>
    private static PipelineIdentity DescribePipeline() => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,
        LabelMapVersion = PipelineIdentity.NotImplementedVersion,
        ApplicationCommitSha = BuildProvenance.CommitShaOf(Assembly.GetExecutingAssembly()),
    };
}
