using System.Reflection;
using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Access;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Configuration;
using Hydrocephalus.Infrastructure.Dataset;
using Hydrocephalus.Infrastructure.Dicom;
using Hydrocephalus.Infrastructure.Models;
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

    private readonly Hydrocephalus.Application.RecordManualMeasurementUseCase recordMeasurement;

    private readonly Hydrocephalus.Application.ReadAuditJournalUseCase readAuditJournal;

    // Сценарии установки собираются только при наличии доверенного ключа:
    // без него проверять пакет нечем, а установка без проверки запрещена
    // ADR 0004. Поэтому здесь не заглушка, которая всегда отказывает, а прямое
    // «нечем»: экран не предлагает действия, которого нет.
    private readonly Hydrocephalus.Application.LoadModelPackageUseCase? loadModelPackage;

    private readonly Hydrocephalus.Application.ActivateModelVersionUseCase? activateModelVersion;

    private readonly InstalledModelStore modelStore;

    // Читатель пакетов живёт весь запуск и один: тот же объект проверяет пакет
    // при установке и при выборе способа разметки на старте. Двумя объектами
    // они разошлись бы незаметно — экран принял бы пакет, который следующий
    // запуск заблокирует, — потому что знание о версии приложения и о понятных
    // версиям схемах задаётся при создании читателя.
    private readonly SignedModelPackageReader? modelPackageReader;

    private readonly ApplicationPaths paths;

    // Открытое исследование держится здесь, а не в окне: рабочая копия — это
    // расшифрованные данные пациента на диске, и решать, когда они исчезнут,
    // должен тот же слой, который их создал.
    private WorkingCopy? opened;

    // Разбор открытой папки держится, пока она открыта: переключение на другое
    // исследование той же папки не должно читать источник заново. Пути файлов
    // из него наружу не выходят.
    private DicomScanResult? scanned;

    // Тот же способ разметки, что у конвейера, и тот же экземпляр: иначе экран
    // и отчёт показывали бы разные маски одной серии.
    private readonly IVentricleSegmentation segmentation;

    private AnalysisReport? report;

    private CompositionRoot(
        Hydrocephalus.Application.AnalyzeStudyUseCase analyzeStudy,
        IVentricleSegmentation segmentation,
        Hydrocephalus.Application.ExportDatasetManifestUseCase exportDatasetManifest,
        Hydrocephalus.Application.ExportReportUseCase exportReport,
        Hydrocephalus.Application.RecordManualMeasurementUseCase recordMeasurement,
        Hydrocephalus.Application.ReadAuditJournalUseCase readAuditJournal,
        Hydrocephalus.Application.LoadModelPackageUseCase? loadModelPackage,
        Hydrocephalus.Application.ActivateModelVersionUseCase? activateModelVersion,
        InstalledModelStore modelStore,
        SignedModelPackageReader? modelPackageReader,
        StudyImporter importer,
        HashChainAuditLog auditLog,
        PipelineIdentity pipeline,
        Actor actor,
        WorkingCopyRetentionPolicy retention,
        ApplicationPaths paths)
    {
        this.AnalyzeStudy = analyzeStudy;
        this.segmentation = segmentation;
        this.ExportDatasetManifest = exportDatasetManifest;
        this.exportReport = exportReport;
        this.recordMeasurement = recordMeasurement;
        this.readAuditJournal = readAuditJournal;
        this.loadModelPackage = loadModelPackage;
        this.activateModelVersion = activateModelVersion;
        this.modelStore = modelStore;
        this.modelPackageReader = modelPackageReader;
        this.importer = importer;
        this.auditLog = auditLog;
        this.Pipeline = pipeline;
        this.Actor = actor;
        this.Retention = retention;
        this.paths = paths;
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
    /// Действующая политика хранения рабочих копий.
    ///
    /// Показывается врачу: ADR 0006 называет риском молчаливое удаление
    /// незавершённого разбора случая и требует, чтобы срок был виден заранее.
    /// </summary>
    public WorkingCopyRetentionPolicy Retention { get; }

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

        // Журнал открывается раньше выбора способа разметки: отвергнутый
        // пакет блокирует анализ, и это событие обязано попасть в журнал,
        // а не остаться догадкой по отсутствию чисел в отчёте.
        var auditLog = new HashChainAuditLog(paths.AuditLogPath);

        var modelStore = new InstalledModelStore(paths.ModelRoot);
        var modelPackageReader = CreateModelPackageReader(paths);

        // Способ разметки желудочков выбирается один раз, при сборке: ни
        // конвейер, ни просмотрщик не должны знать, установлена ли модель.
        // Обёртка с памятью — поверх выбранного: одну серию размечают дважды,
        // для экрана и для отчёта, и моделью это минута счёта на каждый раз.
        //
        // Отсюда же следует, что смена действующей версии вступает в силу
        // только со следующего запуска. Это и есть то, как выполняется запрет
        // ADR 0008 на переключение версии во время анализа: подменить способ
        // у уже собранного конвейера нечем, и отчёт не может сослаться на
        // версию, которой он не получен.
        var segmentation = new CachingVentricleSegmentation(
            VentricleSegmentationChoice.For(
                modelStore.ActivePackagePath(), modelPackageReader, out var activeCheck));

        if (activeCheck?.Rejection is not null)
        {
            await auditLog.RecordAsync(
                new AuditEvent
                {
                    Code = AuditEventCode.ModelPackageRefused,
                    OccurredAt = TimeProvider.System.GetUtcNow(),
                    ModelVersion = activeCheck.Manifest?.ModelVersion ?? modelStore.ActiveVersion(),
                    ModelPackage = Hydrocephalus.Application.LoadModelPackageUseCase
                        .Describe(activeCheck),
                },
                cancellationToken).ConfigureAwait(false);
        }

        var pipeline = DescribePipeline(segmentation.Provenance);

        var importer = new StudyImporter(
            new DicomImportOptions { PseudonymSalt = salt },
            new WorkingCopyOptions { RootDirectory = paths.WorkingCopyRoot });

        // Срок хранения задаёт тот, кто разворачивает приложение, — рядом
        // с ролью и тем же способом. Значение по умолчанию остаётся 24 часа.
        var retention = RetentionSettings.Read(AppContext.BaseDirectory);

        // Уборка до начала любой новой работы: осиротевшие рабочие копии
        // от прерванных сеансов сами не исчезнут — сеанс, который должен был
        // их удалить, уже не выполняется (ADR 0006). Итог попадает в журнал:
        // без записи политика хранения недоказуема.
        await new Hydrocephalus.Application.SweepWorkingCopiesUseCase(
                new WorkingCopyRetentionService(paths.WorkingCopyRoot, retention),
                auditLog,
                TimeProvider.System)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        var useCase = new Hydrocephalus.Application.AnalyzeStudyUseCase(
            importer,
            importer,
            new BaselineMeasurementEngine(
                new InputQualityControl(),
                new WorkingCopyVolumeSource(importer),
                pipeline,
                segmentation),
            new JsonReportStore(paths.ReportRoot),
            auditLog,
            TimeProvider.System);

        var exportDatasetManifest = new Hydrocephalus.Application.ExportDatasetManifestUseCase(
            new FileDatasetManifestStore(paths.DatasetManifestRoot, TimeProvider.System),
            auditLog,
            TimeProvider.System);

        // Хранилище то же, что у анализа: ручное измерение ложится новой версией
        // рядом с прошлыми отчётами того же исследования, а не в отдельное место.
        var recordMeasurement = new Hydrocephalus.Application.RecordManualMeasurementUseCase(
            new JsonReportStore(paths.ReportRoot),
            auditLog,
            TimeProvider.System);

        var exportReport = new Hydrocephalus.Application.ExportReportUseCase(
            // Оба слоя отчёта: JSON остаётся источником истины, PDF порождается
            // из записанного файла (ADR 0005).
            new PdfReportExportStore(new JsonReportExportStore(paths.ReportExportRoot)),
            new UnconfiguredPatientIdentityRegistry(),
            auditLog,
            TimeProvider.System);

        // Читатель журнала отдельно от писателя: писать в журнал должны все
        // сценарии, а читать его — право обслуживания установки.
        var readAuditJournal = new Hydrocephalus.Application.ReadAuditJournalUseCase(
            new AuditJournalReader(paths.AuditLogPath),
            auditLog,
            TimeProvider.System);

        // Осмотр, установка и переключение берут того же читателя пакета, что
        // и выбор способа разметки: экран не должен принимать пакет, который
        // следующий запуск заблокирует.
        var loadModelPackage = modelPackageReader is null
            ? null
            : new Hydrocephalus.Application.LoadModelPackageUseCase(
                modelPackageReader, modelStore, auditLog, TimeProvider.System);

        var activateModelVersion = modelPackageReader is null
            ? null
            : new Hydrocephalus.Application.ActivateModelVersionUseCase(
                modelPackageReader, modelStore, auditLog, TimeProvider.System);

        // Файл настроек лежит рядом с исполняемым файлом, а не в профиле
        // пользователя: роль задаёт тот, кто разворачивает приложение, и она
        // не должна меняться от того, под кем оно запущено.
        var actor = ActorSettings.Read(AppContext.BaseDirectory);

        return new CompositionRoot(
            useCase,
            segmentation,
            exportDatasetManifest,
            exportReport,
            recordMeasurement,
            readAuditJournal,
            loadModelPackage,
            activateModelVersion,
            modelStore,
            modelPackageReader,
            importer,
            auditLog,
            pipeline,
            actor,
            retention,
            paths);
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

        this.scanned = null;

        var scan = await this.importer.ScanAsync(sourceDirectory, cancellationToken)
            .ConfigureAwait(false);

        // Папка с несколькими исследованиями открывается, а не отвергается:
        // берётся исследование с лучшей для анализа серией, остальные экран
        // предлагает выбрать. Правило то же, что у выбора серии.
        var study = Hydrocephalus.Application.AnalyzeStudyUseCase.SelectAnalysableStudy(scan.Studies)
            ?? throw new DomainRuleViolationException(
                Hydrocephalus.Infrastructure.Dicom.StudyImporter.NoReadableStudyMessage(scan));

        this.scanned = scan;

        return await this.OpenScannedStudyAsync(study.PseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Исследования последней разобранной папки; пусто, если разбора не было
    /// или он не удался.
    ///
    /// Нужны экрану и тогда, когда открыть выбранное исследование не удалось:
    /// отказ одного исследования — например, проверкой деидентификации — не
    /// повод не предложить остальные.
    /// </summary>
    public IReadOnlyList<ImagingStudy> ScannedStudies => this.scanned?.Studies ?? [];

    /// <summary>
    /// Переключает просмотр на другое исследование уже открытой папки.
    ///
    /// Прежняя рабочая копия уничтожается до создания новой — по той же
    /// причине, что и при открытии папки: две расшифрованные копии на диске
    /// не нужны.
    /// </summary>
    /// <param name="pseudonymousStudyId">Псевдонимный идентификатор исследования.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Состояние экрана просмотра и отчёт по выбранному исследованию.</returns>
    /// <exception cref="InvalidOperationException">Если папка не открыта.</exception>
    public async Task<OpenedStudy> ShowStudyAsync(
        string pseudonymousStudyId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonymousStudyId);

        if (this.scanned is null)
        {
            throw new InvalidOperationException("No folder is open, so there is no study to switch to.");
        }

        await this.CloseAsync().ConfigureAwait(false);

        return await this.OpenScannedStudyAsync(pseudonymousStudyId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OpenedStudy> OpenScannedStudyAsync(
        string pseudonymousStudyId,
        CancellationToken cancellationToken)
    {
        var scan = this.scanned
            ?? throw new InvalidOperationException("No folder is open.");

        var workingCopy = await this.importer
            .ImportStudyAsync(scan, pseudonymousStudyId, cancellationToken)
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

        return await this.ShowAsync(workingCopy, series, justImported: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Переключает просмотр и анализ на другую серию открытого исследования.
    ///
    /// Переанализ обязателен, а не желателен: оставить прежний отчёт рядом
    /// с новой картинкой значило бы подписать одну серию числами, полученными
    /// по другой. Исследование при этом не импортируется заново — рабочая копия
    /// уже есть, и вторая копия тех же данных пациента на диске не нужна.
    /// </summary>
    /// <param name="pseudonymousSeriesId">Псевдонимный идентификатор выбранной серии.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Состояние экрана просмотра и отчёт по выбранной серии.</returns>
    /// <exception cref="InvalidOperationException">Если исследование не открыто.</exception>
    public async Task<OpenedStudy> ShowSeriesAsync(
        string pseudonymousSeriesId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pseudonymousSeriesId);

        var workingCopy = this.opened
            ?? throw new InvalidOperationException("No study is open, so there is no series to show.");

        var series = workingCopy.Study.Series.FirstOrDefault(item => string.Equals(
            item.PseudonymousSeriesId,
            pseudonymousSeriesId,
            StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                "The requested series is not part of the open study.");

        return await this.ShowAsync(workingCopy, series, justImported: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OpenedStudy> ShowAsync(
        WorkingCopy workingCopy,
        ImagingSeries series,
        bool justImported,
        CancellationToken cancellationToken)
    {
        // Отчёт по прежней серии перестаёт быть текущим до того, как начнётся
        // работа над новой. Иначе неудачный переход оставил бы его доступным
        // к экспорту, а описывал бы он не ту серию, которая перед врачом.
        this.report = null;

        var volume = await DicomVolumeReader
            .LoadAsync(
                this.importer.SessionFor(workingCopy.VolumeReference),
                series.PseudonymousSeriesId,
                cancellationToken)
            .ConfigureAwait(false);

        // Маска строится только когда взвешенность известна: без неё
        // baseline-сегментация отказывается работать, и это не повод
        // не показать изображение.
        VentricleSegmentationResult? segmentation = null;

        AutomaticEvansResult? evans = null;
        var review = (VoxelMask?)null;

        if (series.Weighting != SeriesWeighting.Unknown)
        {
            segmentation = this.segmentation.Segment(
                volume,
                series.Weighting,
                series.PseudonymousSeriesId,
                cancellationToken);
            review = segmentation.Value.Review;

            // Индекс считается и конвейером для отчёта; здесь он нужен ради
            // отрезков, которые врач должен видеть на снимке. Метод
            // детерминирован, и оба вызова дают одно и то же.
            //
            // Уровень входа здесь не проверяется: индекс отмеряется на одном
            // аксиальном срезе и толщине среза безразличен, а чего ему не
            // хватает — он скажет сам, названной причиной. Проверка стояла и
            // здесь, и в конвейере; сняв её только в конвейере, я получил бы
            // измерение в отчёте и пустой экран у врача.
            {
                evans = AutomaticEvansIndex.Measure(volume, segmentation.Value, series.Weighting, cancellationToken);

                if (evans.Segments is { } segments && review is not null)
                {
                    review = EvansOverlay.Draw(review, segments);
                }
            }
        }

        // Анализ идёт по той же рабочей копии, что и просмотр. Отдельный вызов
        // ExecuteAsync импортировал бы исследование второй раз: на диске
        // оказалось бы две копии одних и тех же данных, и экспортируемый отчёт
        // описывал бы не то, что показано на экране.
        //
        // Событие импорта пишется только при первом показе: переключение серии
        // импортом не является, и вторая запись сделала бы журнал неправдой.
        this.report = justImported
            ? await this.AnalyzeStudy
                .AnalyseWorkingCopyAsync(workingCopy, this.Actor, progress: null, cancellationToken)
                .ConfigureAwait(false)
            : await this.AnalyzeStudy
                .AnalyseSeriesAsync(
                    workingCopy,
                    series.PseudonymousSeriesId,
                    this.Actor,
                    progress: null,
                    cancellationToken)
                .ConfigureAwait(false);

        var view = new StudyView(volume, segmentation?.Mask, review);

        if (evans?.Segments is { } measured)
        {
            // Аксиальный вид открывается на срезе, где измерен индекс.
            view.Planes.First(plane => plane.Axis == measured.AxialAcross).Index = measured.PlaneIndex;
        }

        return new OpenedStudy
        {
            View = view,
            Segmentation = segmentation,
            Evans = evans,
            Studies = this.scanned?.Studies ?? [workingCopy.Study],
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
            ?? throw new InvalidOperationException(
                "There is no current report to export: no study is open, or the last analysis did not finish.");

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
    /// Открывает рабочий список измерений по замороженному набору.
    ///
    /// Читается заново при каждом вызове: измеренность выводится из хранилища
    /// отчётов, а оно меняется и от работы с оригиналами, мимо набора.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Список либо <see langword="null"/>, если набор не собран.</returns>
    public Task<Infrastructure.Dataset.MeasurementWorklist?> OpenWorklistAsync(
        CancellationToken cancellationToken) =>
        Infrastructure.Dataset.MeasurementWorklist.OpenAsync(
            this.paths.DatasetRoot,
            this.paths.ReportRoot,
            group: null,
            cancellationToken);

    /// <summary>
    /// Записывает в отчёт измерение, выполненное врачом вручную.
    ///
    /// Записывается в отчёт открытой серии: <c>this.report</c> относится к той
    /// серии, которую показывает экран, потому что смена серии заново запускает
    /// анализ и заменяет обе величины сразу. Иначе отметка, сделанная на одной
    /// серии, попала бы в отчёт другой и выглядела бы правильной.
    /// </summary>
    /// <param name="measurements">Признаки одного измерения.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Новая версия отчёта.</returns>
    /// <exception cref="InvalidOperationException">Если исследование не открыто.</exception>
    public async Task<AnalysisReport> RecordMeasurementAsync(
        IReadOnlyList<Domain.Measurements.Biomarker> measurements,
        CancellationToken cancellationToken)
    {
        var current = this.report
            ?? throw new InvalidOperationException(
                "There is no current report to record a measurement into: no study is open, "
                + "or the last analysis did not finish.");

        this.report = await this.recordMeasurement
            .ExecuteAsync(current, measurements, this.Actor, cancellationToken)
            .ConfigureAwait(false);

        return this.report;
    }

    /// <summary>
    /// Готовит отчёт к показу, ничего не записывая.
    ///
    /// Экспорт — действие с последствиями вовне, и увидеть, что уходит, нужно
    /// до того, как оно ушло. Содержимое собирается тем же сценарием, что и при
    /// экспорте, и раскладывается той же раскладкой, что уходит в PDF: иначе
    /// экран показывал бы одно, а в файл попадало другое.
    /// </summary>
    /// <param name="variant">Вариант экспорта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Строки отчёта в том виде, в каком они попадут в файл.</returns>
    /// <exception cref="InvalidOperationException">Если исследование не открыто.</exception>
    public async Task<IReadOnlyList<ReportLine>> PreviewReportAsync(
        ReportExportVariant variant,
        CancellationToken cancellationToken)
    {
        var current = this.report
            ?? throw new InvalidOperationException(
                "There is no current report to preview: no study is open, or the last analysis did not finish.");

        var export = await this.exportReport.PreviewAsync(
            new Hydrocephalus.Application.ReportExportRequest
            {
                Report = current,
                Variant = variant,
                RequestedBy = this.Actor,
                AcknowledgeAnnotationsMayContainPhi = false,
            },
            cancellationToken).ConfigureAwait(false);

        return ReportOutline.Build(JsonReportExportStore.Serialize(export));
    }

    /// <summary>
    /// Собирает состояние установки для экрана администрирования.
    ///
    /// Право проверяет сценарий, а не экран: выключенная кнопка — удобство,
    /// а не разграничение. Журнал показывает работу всей установки, и доступ
    /// к нему не вытекает из права разбирать один случай.
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Журнал аудита вместе с расположением файлов и состоянием установки.</returns>
    /// <exception cref="AccessDeniedException">
    /// Если у роли нет права на чтение журнала.
    /// </exception>
    public async Task<Administration.AdministrationSnapshot> OpenAdministrationAsync(
        CancellationToken cancellationToken)
    {
        var journal = await this.readAuditJournal
            .ExecuteAsync(this.Actor, cancellationToken)
            .ConfigureAwait(false);

        return new Administration.AdministrationSnapshot
        {
            Journal = journal,
            AuditLogPath = this.paths.AuditLogPath,
            WorkingCopyRoot = this.paths.WorkingCopyRoot,
            ReportRoot = this.paths.ReportRoot,
            ReportExportRoot = this.paths.ReportExportRoot,
            DatasetManifestRoot = this.paths.DatasetManifestRoot,
            Retention = this.Retention,
            PatientRegistryConfigured = PatientRegistryConfigured,
            Pipeline = this.Pipeline,
            Models = this.DescribeModels(),
        };
    }

    /// <summary>
    /// Состояние установленных пакетов модели.
    ///
    /// Отдельно от <see cref="OpenAdministrationAsync"/>, потому что экран
    /// модели перечитывает его после каждого действия, а журнал аудита читать
    /// для этого незачем: он растёт всю жизнь установки.
    ///
    /// Право проверяется здесь, а не экраном: выключенная кнопка — удобство,
    /// а не разграничение (ADR 0005).
    /// </summary>
    /// <returns>Что установлено и что действует.</returns>
    /// <exception cref="AccessDeniedException">
    /// Если у роли нет права на установку модели.
    /// </exception>
    public Administration.ModelInstallationState ModelState()
    {
        this.Actor.Require(Capability.InstallModelPackage);

        return this.DescribeModels();
    }

    /// <summary>
    /// Осматривает пакет, ничего не устанавливая.
    ///
    /// Нужен до подтверждения: ADR 0008 требует показать версию, ключ, статус
    /// подписи и карточку модели — и только потом устанавливать.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета, выбранному администратором.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Что показать администратору.</returns>
    /// <exception cref="InvalidOperationException">Если доверенного ключа нет.</exception>
    public Task<Hydrocephalus.Application.ModelPackageInspection> InspectModelPackageAsync(
        string packagePath,
        CancellationToken cancellationToken) =>
        ModelAdministrationOf(this.loadModelPackage)
            .InspectAsync(packagePath, this.Actor, cancellationToken);

    /// <summary>
    /// Устанавливает пакет: проверяет заново и кладёт в хранилище.
    ///
    /// Действующим пакет не становится — это отдельное действие
    /// (<see cref="ActivateModelVersionAsync"/>), как требует ADR 0008.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Исход установки.</returns>
    /// <exception cref="InvalidOperationException">
    /// Если доверенного ключа нет либо открыто исследование.
    /// </exception>
    public Task<Hydrocephalus.Application.ModelPackageInstallation> InstallModelPackageAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        this.RequireNoOpenStudy();

        return ModelAdministrationOf(this.loadModelPackage)
            .ExecuteAsync(packagePath, this.Actor, cancellationToken);
    }

    /// <summary>
    /// Делает установленную версию действующей; она же операция отката.
    ///
    /// Вступает в силу со следующего запуска: способ разметки собирается один
    /// раз при сборке приложения. Так и выполняется запрет ADR 0008 на
    /// переключение версии во время анализа — подменить способ у уже
    /// собранного конвейера нечем.
    /// </summary>
    /// <param name="modelVersion">Версия, которая должна стать действующей.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Исход смены.</returns>
    /// <exception cref="InvalidOperationException">
    /// Если доверенного ключа нет либо открыто исследование.
    /// </exception>
    public Task<Hydrocephalus.Application.ModelVersionActivation> ActivateModelVersionAsync(
        string modelVersion,
        CancellationToken cancellationToken)
    {
        this.RequireNoOpenStudy();

        return ModelAdministrationOf(this.activateModelVersion)
            .ExecuteAsync(modelVersion, this.Actor, cancellationToken);
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
        this.modelPackageReader?.Dispose();
    }

    // Из ссылки показывается только каталог: имя файла содержит псевдоним
    // исследования, а строка состояния видна на экране в кабинете.
    private static string DirectoryOf(string reference) =>
        System.IO.Path.GetDirectoryName(reference) ?? reference;

    private static T ModelAdministrationOf<T>(T? useCase)
        where T : class =>
        useCase ?? throw new InvalidOperationException(
            "There is no trusted key, so model packages cannot be checked.");

    // Пока исследование открыто, установка и переключение версии недоступны:
    // ADR 0008 запрещает менять версию во время выполняющегося анализа. Экран
    // такие кнопки и не показывает; проверка здесь — на случай, когда показ
    // и нажатие разошлись по времени с открытием исследования.
    private void RequireNoOpenStudy()
    {
        if (this.opened is not null)
        {
            throw new InvalidOperationException(
                "A study is open, so the model version must not be switched now.");
        }
    }

    /// <summary>
    /// Описывает установленные пакеты для экрана администрирования.
    ///
    /// Действующий пакет проверяется заново, а не берётся с запуска: экран
    /// отвечает на вопрос «что сейчас», и пакет, испортившийся после старта,
    /// должен быть виден здесь, а не обнаружиться у врача. Веса при этом не
    /// читаются — экрану нужно решение о пакете, а не сотня мегабайт.
    /// </summary>
    private Administration.ModelInstallationState DescribeModels()
    {
        var active = this.modelStore.ActiveVersion();
        var path = active is null ? null : this.modelStore.PackagePathOf(active);

        var check = path is not null && this.modelPackageReader is not null
            ? this.modelPackageReader.Verify(path)
            : null;

        return new Administration.ModelInstallationState
        {
            InstalledVersions = this.modelStore.InstalledVersions(),
            ActiveVersion = active,
            MeasuringNow = this.Pipeline.LabelMapVersion,
            ActiveManifest = check?.Manifest,
            ActiveSignature = check?.Signature ?? ModelPackageSignature.NotChecked,
            ActiveRejection = check?.Rejection,
            ActiveDetail = check?.Detail ?? string.Empty,
            TrustKeyConfigured = this.modelPackageReader is not null,
            ModelRoot = this.paths.ModelRoot,
            StudyOpen = this.opened is not null,
        };
    }

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
    /// <summary>
    /// Создаёт читателя пакетов модели либо сообщает, что проверять нечем.
    ///
    /// Один объект на весь запуск: им проверяется и действующий пакет на
    /// старте, и устанавливаемый на экране. Второй объект с другой версией
    /// приложения или другим перечнем понятных схем разошёлся бы с первым
    /// молча — экран принял бы пакет, который следующий запуск заблокирует.
    ///
    /// Ключ читается из файла в профиле пользователя, и для выпуска этого
    /// недостаточно — см. <see cref="ApplicationPaths.ModelTrustKeyPath"/>.
    /// </summary>
    private static SignedModelPackageReader? CreateModelPackageReader(ApplicationPaths paths) =>
        System.IO.File.Exists(paths.ModelTrustKeyPath)
            ? new SignedModelPackageReader(
                System.IO.File.ReadAllText(paths.ModelTrustKeyPath),
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0),
                [VolumeConforming.PreprocessingVersion],
                [OnnxVentricleSegmentation.LabelMapVersion])
            : null;

    private static PipelineIdentity DescribePipeline(string segmentationProvenance) => new()
    {
        PreprocessingVersion = PipelineIdentity.NotImplementedVersion,
        FeatureSchemaVersion = PipelineIdentity.NotImplementedVersion,

        // Версия разметки приходит от того, кто её делает, и называет и способ,
        // и версию модели. До появления модели здесь стояло «не реализовано»,
        // хотя пороговый метод свою версию имел: смена способа не была видна в
        // отчёте, а ADR 0008 требует, чтобы отчёт называл версию, которой он
        // получен, и чтобы прежние отчёты не пересчитывались задним числом.
        LabelMapVersion = segmentationProvenance,
        ApplicationCommitSha = BuildProvenance.CommitShaOf(Assembly.GetExecutingAssembly()),
    };
}
