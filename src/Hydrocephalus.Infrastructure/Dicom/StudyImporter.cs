using System.Globalization;
using FellowOakDicom;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Импорт исследования: разбор источника, деидентификация и запись рабочей копии
/// (ADR 0003).
///
/// Порядок обязателен: набор тегов очищается в памяти и только затем записывается.
/// Обратный порядок — скопировать файлы, затем очистить — оставлял бы окно, в
/// котором исходные данные лежат внутри рабочего каталога; падение в этом окне
/// оставляло бы их на диске. При выбранном порядке любой сбой оставляет меньше
/// файлов, но все записанные уже деидентифицированы.
///
/// Имена файлов и каталогов рабочей копии строятся только из псевдонимов: имена
/// в исходном сборе содержат фамилии пациентов, то есть PHI вне DICOM-тегов
/// (docs/data/README.md), и переименование входит в профиль наравне с тегами.
/// </summary>
public sealed class StudyImporter : IStudyImporter, IWorkingCopyLifetime
{
    private readonly DicomImportOptions importOptions;
    private readonly WorkingCopyOptions workingCopyOptions;
    private readonly TimeProvider timeProvider;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkingCopySession> sessions =
        new(StringComparer.Ordinal);

    /// <summary>Создаёт импортёр.</summary>
    /// <param name="importOptions">Ограничения приёма и соль псевдонимизации.</param>
    /// <param name="workingCopyOptions">Расположение рабочей копии.</param>
    /// <param name="timeProvider">Источник времени для отметки сеанса.</param>
    public StudyImporter(
        DicomImportOptions importOptions,
        WorkingCopyOptions workingCopyOptions,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(importOptions);
        ArgumentNullException.ThrowIfNull(workingCopyOptions);

        this.importOptions = importOptions;
        this.workingCopyOptions = workingCopyOptions;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Импортирует исследование из каталога и создаёт рабочую копию.
    /// </summary>
    /// <param name="sourceReference">Каталог источника.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Рабочая копия исследования.</returns>
    public async Task<WorkingCopy> ImportAsync(string sourceReference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);

        var scan = await new DicomStudyScanner(this.importOptions)
            .ScanAsync(sourceReference, cancellationToken)
            .ConfigureAwait(false);

        if (scan.Studies.Count == 0)
        {
            throw new DomainRuleViolationException(
                "The import source contains no readable imaging study.");
        }

        if (scan.Studies.Count > 1)
        {
            // Интерфейс импорта возвращает одну рабочую копию, поэтому неоднозначный
            // источник — это отказ, а не молчаливый выбор одного из исследований.
            // Выбор исследования врачом — часть просмотрщика (M2) и потребует
            // расширения контракта.
            throw new DomainRuleViolationException(
                "The import source contains more than one study; a single study must be selected before import.");
        }

        var study = scan.Studies[0];

        // Серия с блокирующим замечанием — прежде всего вписанные в изображение
        // аннотации — в рабочую копию не попадает (ADR 0003): такие серии уходят
        // в ручной контроль. Если пригодных серий не остаётся, исследование
        // доходит до сценария анализа с уровнем Unusable и отклоняется там,
        // а не исчезает молча.
        var blockedSeries = scan.Findings
            .Where(finding => finding.Issue.Severity == QualityIssueSeverity.Blocking)
            .Select(finding => finding.PseudonymousSeriesId)
            .ToHashSet(StringComparer.Ordinal);

        var retainedSeries = study.Series
            .Where(series => !blockedSeries.Contains(series.PseudonymousSeriesId))
            .ToList();

        var retainedIds = retainedSeries
            .Select(series => series.PseudonymousSeriesId)
            .ToHashSet(StringComparer.Ordinal);

        // Идентификатор сеанса случаен, а не выведен из исследования. Причин две.
        // Общий на исследование сеанс делили бы просмотр и анализ, и освобождение
        // в одном уничтожило бы данные другого. И имя каталога, выведенное
        // из псевдонима, само связывает копию с исследованием — на диске это
        // лишняя связь. Повторные копии убирает уборка по сроку.
        var session = await WorkingCopySession.CreateAsync(
                this.workingCopyOptions.RootDirectory,
                Guid.NewGuid().ToString("N"),
                this.timeProvider.GetUtcNow(),
                this.workingCopyOptions.RestrictAccessToCurrentUser,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var written = await this.WriteWorkingCopyAsync(
                    sourceReference,
                    session,
                    study.PseudonymousSubjectId,
                    retainedIds,
                    cancellationToken)
                .ConfigureAwait(false);

            VerifyNothingWasLost(retainedSeries, written);
        }
        catch
        {
            // Незавершённая рабочая копия не остаётся на диске: её содержимое
            // уже деидентифицировано, но частичное исследование выглядело бы
            // пригодным для анализа. Уничтожение начинается с ключа.
            session.Destroy();
            throw;
        }

        // Сеанс переживает импорт: по рабочей копии ещё будут читать объём.
        // Уничтожает его сценарий анализа в finally — на успехе, ошибке
        // и отмене (ADR 0006).
        this.sessions[session.Directory] = session;

        return new WorkingCopy
        {
            Study = study with { Series = retainedSeries },
            VolumeReference = session.Directory,
        };
    }

    /// <summary>
    /// Возвращает сеанс рабочей копии по ссылке на неё.
    /// </summary>
    /// <param name="volumeReference">Ссылка на рабочую копию.</param>
    /// <returns>Сеанс.</returns>
    /// <exception cref="InvalidOperationException">Если сеанс уже уничтожен.</exception>
    public WorkingCopySession SessionFor(string volumeReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeReference);

        return this.sessions.TryGetValue(volumeReference, out var session)
            ? session

            // Обращение к уничтоженному сеансу — это чтение данных, которых
            // уже нет. Отдать здесь пустоту значило бы показать пустой объём
            // вместо ошибки.
            : throw new InvalidOperationException(
                "The working copy session has already been destroyed.");
    }

    /// <summary>
    /// Уничтожает рабочую копию: сначала ключ, затем данные.
    /// </summary>
    /// <param name="volumeReference">Ссылка на рабочую копию.</param>
    /// <returns>Задача уничтожения.</returns>
    public Task ReleaseAsync(string volumeReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeReference);

        if (this.sessions.TryRemove(volumeReference, out var session))
        {
            session.Destroy();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Убирает из метаинформации файла поля, называющие отправителя.
    /// Очистка одного лишь набора тегов их не касается: узел-отправитель
    /// обычно именуется по отделению или аппарату.
    /// </summary>
    private static void StripFileMetaIdentity(DicomFileMetaInformation metaInfo)
    {
        metaInfo.Remove(
            new DicomTag(0x0002, 0x0016), // SourceApplicationEntityTitle
            new DicomTag(0x0002, 0x0017), // SendingApplicationEntityTitle
            new DicomTag(0x0002, 0x0018), // ReceivingApplicationEntityTitle
            new DicomTag(0x0002, 0x0100), // PrivateInformationCreatorUID
            new DicomTag(0x0002, 0x0102)); // PrivateInformation
    }

    private void CreateProtected(string directory) =>
        ProtectedDirectory.Create(directory, this.workingCopyOptions.RestrictAccessToCurrentUser);

    private async Task<Dictionary<string, int>> WriteWorkingCopyAsync(
        string sourceReference,
        WorkingCopySession session,
        string pseudonymousSubjectId,
        HashSet<string> retainedSeriesIds,
        CancellationToken cancellationToken)
    {
        var deidentifier = new DicomDeidentifier(this.importOptions);
        var walk = new QuarantineWalk(this.importOptions, sourceReference);
        var written = new Dictionary<string, int>(StringComparer.Ordinal);

        // Отказы второго прохода отбрасываются: те же файлы уже отклонены разбором
        // и попали в его результат. Общий обход задаёт обоим проходам одни и те же
        // лимиты — но не одинаковый набор файлов, если каталог меняется между
        // проходами. Расхождение ловит проверка числа записанных срезов.
        var ignoredRejections = new List<ImportRejection>();

        foreach (var file in walk.EnumerateFiles(ignoredRejections))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DicomFile source;

            try
            {
                source = await DicomFile.OpenAsync(file.FullName).ConfigureAwait(false);
            }
            catch (Exception exception)
                when (exception is DicomFileException or IOException or InvalidOperationException)
            {
                continue;
            }

            var seriesUid = source.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);

            if (string.IsNullOrWhiteSpace(seriesUid))
            {
                continue;
            }

            var seriesId = Pseudonyms.Derive(this.importOptions.PseudonymSalt, "series", seriesUid);

            if (!retainedSeriesIds.Contains(seriesId))
            {
                continue;
            }

            var instance = deidentifier.Deidentify(source.Dataset, pseudonymousSubjectId);
            var relativePath = Path.Combine(seriesId, instance.PseudonymousInstanceId + ".dcm");

            var target = new DicomFile(instance.Dataset);
            StripFileMetaIdentity(target.FileMetaInfo);

            Verify(instance, target.FileMetaInfo, relativePath);

            // Файл шифруется в памяти и только потом ложится на диск:
            // записать открытым и зашифровать следом означало бы оставить окно,
            // в котором деидентифицированные снимки лежат в открытом виде.
            using var buffer = new MemoryStream();

            await target.SaveAsync(buffer).ConfigureAwait(false);

            await session.WriteAsync(relativePath, buffer.ToArray(), cancellationToken)
                .ConfigureAwait(false);

            written[seriesId] = written.GetValueOrDefault(seriesId) + 1;
        }

        return written;
    }

    /// <summary>
    /// Сверяет число записанных срезов с числом, найденным разбором.
    ///
    /// Разбор и запись — два обхода одного каталога. Если между ними каталог
    /// изменился, рабочая копия окажется неполной, а доменная модель по-прежнему
    /// будет заявлять исходное число срезов: геометрия объёма разойдётся с тем,
    /// что лежит на диске. Молчаливо неполный объём хуже отказа, поэтому
    /// расхождение прекращает импорт.
    /// </summary>
    private static void VerifyNothingWasLost(
        IReadOnlyList<ImagingSeries> retainedSeries,
        IReadOnlyDictionary<string, int> written)
    {
        foreach (var series in retainedSeries)
        {
            var expected = series.Geometry.Dimensions.Slices;
            var actual = written.GetValueOrDefault(series.PseudonymousSeriesId);

            if (expected != actual)
            {
                throw new DomainRuleViolationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The working copy is incomplete: {0} of {1} instances were written for one series.",
                    actual,
                    expected));
            }
        }
    }

    /// <summary>
    /// Проверяет результат деидентификации до записи файла.
    ///
    /// Проверка выполняется в рабочем режиме, а не только в тестах: список тегов
    /// профиля описывает намерение, а гарантию даёт лишь отсутствие исходных
    /// значений в результате. Нарушение прекращает импорт — записать файл
    /// и «отметить в журнале» здесь означало бы записать PHI.
    /// </summary>
    private static void Verify(
        DeidentifiedInstance instance,
        DicomFileMetaInformation metaInfo,
        string relativePath)
    {
        var violations = DeidentificationAudit.Inspect(
            instance.Dataset,
            metaInfo,
            instance.SourceSecrets,
            relativePath);

        if (violations.Count == 0)
        {
            return;
        }

        throw new DomainRuleViolationException(string.Format(
            CultureInfo.InvariantCulture,
            "Deidentification audit rejected the working copy: {0} violation(s); first {1} at {2}.",
            violations.Count,
            violations[0].Code,
            violations[0].Location));
    }
}
