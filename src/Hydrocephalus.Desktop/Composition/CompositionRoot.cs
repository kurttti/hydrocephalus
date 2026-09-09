using System.Reflection;
using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Reporting;
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
    /// <summary>
    /// Подключён ли внешний реестр идентификаторов пациента.
    ///
    /// В этой установке — нет, и клинический вариант экспорта поэтому
    /// недоступен (см. <see cref="UnconfiguredPatientIdentityRegistry"/>).
    /// Значение названо здесь, а не выведено экраном из неудачной попытки:
    /// кнопка, которая всегда падает, хуже выключенной кнопки с причиной.
    /// </summary>
    public const bool PatientRegistryConfigured = false;

    private readonly HashChainAuditLog auditLog;

    private readonly StudyImporter importer;

    private readonly Hydrocephalus.Application.ExportReportUseCase exportReport;

    // Открытое исследование держится здесь, а не в окне: рабочая копия — это
    // расшифрованные данные пациента на диске, и решать, когда они исчезнут,
    // должен тот же слой, который их создал.
    private WorkingCopy? opened;

    private AnalysisReport? report;

    private CompositionRoot(
        Hydrocephalus.Application.AnalyzeStudyUseCase analyzeStudy,
        Hydrocephalus.Application.ExportDatasetManifestUseCase exportDatasetManifest,
        Hydrocephalus.Application.ExportReportUseCase exportReport,
        StudyImporter importer,
        HashChainAuditLog auditLog,
        PipelineIdentity pipeline,
        Actor actor)
    {
        this.AnalyzeStudy = analyzeStudy;
        this.ExportDatasetManifest = exportDatasetManifest;
        this.exportReport = exportReport;
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
    /// Доступен с экрана только исследователю: экспорт выборки наружу — не
    /// лечебная работа. Разграничение выполняется сценарием и фиксируется
    /// в аудите (ADR 0005); экран лишь не показывает того, чего роль не может.
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
            new BaselineMeasurementEngine(
                new InputQualityControl(),
                new WorkingCopyVolumeSource(importer),
                pipeline),
            new JsonReportStore(paths.ReportRoot),
            auditLog,
            TimeProvider.System);

        var exportDatasetManifest = new Hydrocephalus.Application.ExportDatasetManifestUseCase(
            new FileDatasetManifestStore(paths.DatasetManifestRoot, TimeProvider.System),
            auditLog,
            TimeProvider.System);

        var exportReport = new Hydrocephalus.Application.ExportReportUseCase(
            // Оба слоя отчёта: JSON остаётся источником истины, PDF порождается
            // из записанного файла (ADR 0005).
            new PdfReportExportStore(new JsonReportExportStore(paths.ReportExportRoot)),
            new UnconfiguredPatientIdentityRegistry(),
            auditLog,
            TimeProvider.System);

        // Файл настроек лежит рядом с исполняемым файлом, а не в профиле
        // пользователя: роль задаёт тот, кто разворачивает приложение, и она
        // не должна меняться от того, под кем оно запущено.
        var actor = ActorSettings.Read(AppContext.BaseDirectory);

        return new CompositionRoot(
            useCase,
            exportDatasetManifest,
            exportReport,
            importer,
            auditLog,
            pipeline,
            actor);
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
    /// <returns>Состояние экрана просмотра и отчёт по тому же исследованию.</returns>
    public async Task<OpenedStudy> OpenForViewingAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        // Предыдущее исследование освобождается до импорта следующего, а не
        // после: две расшифрованные рабочие копии одновременно на диске — это
        // ровно вдвое больше данных пациента, чем нужно для работы.
        await this.CloseAsync().ConfigureAwait(false);

        var workingCopy = await this.importer.ImportAsync(sourceDirectory, cancellationToken)
            .ConfigureAwait(false);

        this.opened = workingCopy;

        // Показывается та же серия, которую берёт анализ, — и берётся она тем же
        // кодом, а не таким же. Повторённое здесь правило отбора рано или поздно
        // разошлось бы со сценарием, и экран подписал бы одну серию отчётом
        // по другой.
        var series = Hydrocephalus.Application.AnalyzeStudyUseCase
            .SelectAnalysableSeries(workingCopy.Study)
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

        // Анализ идёт по той же рабочей копии, что и просмотр. Отдельный вызов
        // ExecuteAsync импортировал бы исследование второй раз: на диске
        // оказалось бы две копии одних и тех же данных, и экспортируемый отчёт
        // описывал бы не то, что показано на экране.
        this.report = await this.AnalyzeStudy
            .AnalyseWorkingCopyAsync(workingCopy, this.Actor, progress: null, cancellationToken)
            .ConfigureAwait(false);

        return new OpenedStudy
        {
            View = new StudyView(volume, mask),
            Report = this.report,
            Analysed = new Results.AnalysedStudy
            {
                Study = workingCopy.Study,
                Analysed = series,
                Excluded = workingCopy.ExcludedSeries,
            },
        };
    }

    /// <summary>
    /// Экспортирует отчёт по открытому исследованию.
    ///
    /// Права проверяет сценарий, а не экран: выключенная кнопка — удобство,
    /// а не защита, и полагаться на неё как на разграничение нельзя.
    /// </summary>
    /// <param name="variant">Вариант экспорта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Каталог, в который записан файл.</returns>
    /// <exception cref="InvalidOperationException">Если исследование не открыто.</exception>
    public async Task<string> ExportReportAsync(
        ReportExportVariant variant,
        CancellationToken cancellationToken)
    {
        var current = this.report
            ?? throw new InvalidOperationException("No study is open, so there is no report to export.");

        var reference = await this.exportReport.ExecuteAsync(
            new Hydrocephalus.Application.ReportExportRequest
            {
                Report = current,
                Variant = variant,
                RequestedBy = this.Actor,

                // Подтверждение не запрашивается, потому что запрашивать нечего:
                // комментарии врача в приложении пока негде ввести, и список
                // всегда пуст. Когда они появятся, обезличенный экспорт откажет
                // до тех пор, пока подтверждение не будет получено явно, —
                // отказ в безопасную сторону, а не молчаливое согласие за врача.
                AcknowledgeAnnotationsMayContainPhi = false,
            },
            cancellationToken).ConfigureAwait(false);

        return DirectoryOf(reference);
    }

    /// <summary>
    /// Экспортирует манифест датасета по открытому исследованию.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Каталог, в который записан файл.</returns>
    /// <exception cref="InvalidOperationException">Если исследование не открыто.</exception>
    public async Task<string> ExportDatasetManifestAsync(CancellationToken cancellationToken)
    {
        var current = this.opened
            ?? throw new InvalidOperationException("No study is open, so there is nothing to export.");

        var reference = await this.ExportDatasetManifest.ExecuteAsync(
            new Hydrocephalus.Application.DatasetExportRequest
            {
                Studies = [current.Study],
                RequestedBy = this.Actor,

                // Подтверждением служит само нажатие кнопки экспорта: она
                // отдельная и ничего другого не делает.
                Confirmed = true,
            },
            cancellationToken).ConfigureAwait(false);

        return DirectoryOf(reference);
    }

    /// <summary>Освобождает ресурсы собранных реализаций.</summary>
    public void Dispose()
    {
        // Рабочая копия уничтожается синхронно при закрытии приложения:
        // «уберём в фоне» на выходе означает не уберём. Уничтожение начинается
        // с ключа, поэтому прерывание всё равно делает данные нечитаемыми.
        this.CloseAsync().GetAwaiter().GetResult();

        this.auditLog.Dispose();
    }

    // Из ссылки показывается только каталог: имя файла содержит псевдоним
    // исследования, а строка состояния видна на экране в кабинете.
    private static string DirectoryOf(string reference) =>
        System.IO.Path.GetDirectoryName(reference) ?? reference;

    private async Task CloseAsync()
    {
        if (this.opened is null)
        {
            return;
        }

        var previous = this.opened;

        this.opened = null;
        this.report = null;

        await this.importer.ReleaseAsync(previous.VolumeReference).ConfigureAwait(false);
    }

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
