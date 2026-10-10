using System.Globalization;
using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference.Measurements;
using Hydrocephalus.Inference.QualityControl;
using Hydrocephalus.Inference.Segmentation;

namespace Hydrocephalus.Inference;

/// <summary>
/// Конвейер, который измеряет то, что поддаётся измерению без модели,
/// и отказывается от классификации.
///
/// Разделение здесь принципиальное. **Классификация** — вероятность диагноза —
/// требует проверенного model package: ADR 0004 разрешает читать веса только
/// из подписанного пакета, а пакет появляется в M4. Вернуть вероятность без
/// него значило бы выдать невалидированное число за результат, поэтому
/// классификация отказывает всегда.
///
/// **Измерение** — другое утверждение. Объём желудочков получается
/// детерминированным методом из маски, и его можно посчитать уже сейчас.
/// Отказ классификации не отменяет измерений: отчёт с числом и честной
/// пометкой о его происхождении полезнее пустого отчёта.
///
/// Чего конвейер намеренно не делает:
///
/// - **не считает индекс Эванса мимо маски.** Индекс выводится из той же
///   маски, что и объём (<see cref="Measurements.AutomaticEvansIndex"/>),
///   и только когда сегментация не отказала: точки, поставленные по
///   фрагменту желудочка, дали бы число правдоподобного вида из неверной
///   анатомии. Если передние рога обоих желудочков в маске не найдены,
///   индекса нет;
/// - **не сохраняет маску.** <see cref="Domain.Segmentation.SegmentationResult"/>
///   требует ссылки на маски в рабочей копии, а записывать их туда пока некуда:
///   для этого нужны шифрованное хранение масок и срок их жизни (ADR 0006).
///   Поэтому раздел сегментации в отчёте остаётся пустым, хотя сегментация
///   выполнялась;
/// - **не измеряет там, где объём не читается.** Сжатый синтаксис передачи,
///   неподдерживаемый формат пикселей, объём без распознаваемой головы —
///   обычные состояния реальной выгрузки, а не дефекты. Измерений в таком
///   случае нет, а отчёт с результатом контроля качества всё равно выходит;
/// - **не выдаёт нулевой объём.** Пустая маска означает, что метод ничего
///   не нашёл, а не что желудочков нет; измерения в таком случае нет;
/// - **не измеряет на базовом уровне входа.** Объём считается по шагу сетки,
///   и на серии с зазором между срезами он описывает не полученную ткань,
///   а объём, которым она представлена. Объёмные признаки определены для
///   расширенного уровня, и это ограничение проверяется здесь, а не
///   предполагается.
/// </summary>
public sealed class BaselineMeasurementEngine : IInferenceEngine
{
    private readonly InputQualityControl qualityControl;
    private readonly IVolumeSource volumes;
    private readonly PipelineIdentity pipeline;
    private readonly IVentricleSegmentation segmentation;

    /// <summary>
    /// Создаёт конвейер.
    /// </summary>
    /// <param name="qualityControl">Входной контроль качества.</param>
    /// <param name="volumes">Доступ к воксельным данным рабочей копии.</param>
    /// <param name="pipeline">
    /// Версии конвейера. Значения по умолчанию здесь нет намеренно: provenance
    /// отчёта должен приходить от того, кто собирает приложение и знает версии,
    /// а подставленное «unknown» выглядело бы как заполненное поле.
    /// </param>
    /// <param name="segmentation">
    /// Способ получить маску желудочков. По умолчанию пороговый — тот, с которым
    /// конвейер работал до появления модели (ADR 0009). Выбор принадлежит
    /// составу приложения, а не конвейеру: конвейер не должен знать, какая
    /// модель установлена и установлена ли вообще.
    /// </param>
    public BaselineMeasurementEngine(
        InputQualityControl qualityControl,
        IVolumeSource volumes,
        PipelineIdentity pipeline,
        IVentricleSegmentation? segmentation = null)
    {
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(pipeline);

        this.qualityControl = qualityControl;
        this.volumes = volumes;
        this.pipeline = pipeline;
        this.segmentation = segmentation ?? new ThresholdVentricleSegmentation();
    }

    /// <summary>Сообщает версии конвейера.</summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Версии конвейера.</returns>
    public Task<PipelineIdentity> DescribePipelineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(this.pipeline);
    }

    /// <summary>Выполняет входной контроль качества.</summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат контроля качества.</returns>
    public async Task<QualityAssessment> RunQualityControlAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var assessment = this.qualityControl.Evaluate(request);
        var series = SeriesOf(request);

        // Голова ли на снимке — проверяется здесь, а не на приёмке: приёмка
        // читает только теги, а теги об этом лгут. Найденная абдоминальная МРТ
        // объявляет себя `TEMP^HEAD` при пустой области съёмки.
        //
        // И здесь, а не в измерении: контроль качества идёт первым и
        // останавливает разбор, то есть снимок живота не проходит сегментацию
        // ради отказа «желудочки неправдоподобно малы» — правды о числе и
        // неправды о сути.
        if (series is null || !assessment.IsAcceptable)
        {
            return assessment;
        }

        var volume = await this.volumes
            .LoadAsync(request.VolumeReference, series.PseudonymousSeriesId, cancellationToken)
            .ConfigureAwait(false);

        var (extent, looksLikeHead) = HeadPresence.Measure(volume, cancellationToken);

        if (looksLikeHead)
        {
            return assessment;
        }

        return new QualityAssessment
        {
            Issues =
            [
                .. assessment.Issues,
                new QualityIssue
                {
                    Code = QualityIssueCode.NotAHeadStudy,
                    Severity = QualityIssueSeverity.Blocking,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["extentMillimetres"] = extent.ToString(
                            "0", CultureInfo.InvariantCulture),
                        ["limitMillimetres"] = HeadPresence.MaxHeadExtentMillimetres.ToString(
                            "0", CultureInfo.InvariantCulture),
                    },
                },
            ],
        };
    }

    /// <summary>Серия запроса либо <see langword="null"/>, если её нет.</summary>
    private static ImagingSeries? SeriesOf(AnalysisRequest request) =>
        request.Study.Series.FirstOrDefault(item => string.Equals(
            item.PseudonymousSeriesId,
            request.PseudonymousSeriesId,
            StringComparison.Ordinal));

    /// <summary>
    /// Измеряет объёмы желудочковой системы и отказывается от классификации.
    /// </summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Измерения и отказ классификации.</returns>
    public async Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        var (biomarkers, attempt) = await this.MeasureAsync(request, progress, cancellationToken)
            .ConfigureAwait(false);

        // Отказ классификации наступает на проверке пакета: показывать прогресс
        // модели, которой нет, — обман.
        progress?.Report(new AnalysisProgress(AnalysisStage.ValidatingModelPackage, 1.0));

        return new AnalysisResult
        {
            Outcome = new AnalysisOutcome.Refused
            {
                Reason = new RefusalReason { Code = RefusalCode.ModelPackageUnusable },
            },
            Biomarkers = biomarkers,
            Measurement = attempt,
        };
    }

    /// <summary>Измерения вместе с рассказом о том, чем размечали и что вышло.</summary>
    private static (IReadOnlyList<Biomarker> Biomarkers, MeasurementAttempt Attempt) Refused(
        MeasurementRefusal refusal) =>
        ([], new MeasurementAttempt { Refusal = refusal });

    private async Task<(IReadOnlyList<Biomarker> Biomarkers, MeasurementAttempt Attempt)> MeasureAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var series = SeriesOf(request);

        if (series is null)
        {
            // Рассогласование запроса и рабочей копии. Молчаливо вернуть
            // «измерений нет» здесь означало бы допустить измерение
            // неизвестно чего.
            throw new DomainRuleViolationException(
                "The analysis request names a series that the study does not contain.");
        }

        // Взвешенность обязана быть известна: по ней сегментация выбирает
        // направление порога, а угадать его нельзя — ошибка выделит ткань
        // вместо ликвора и даст объём того же порядка с обратным смыслом.
        if (series.Weighting == SeriesWeighting.Unknown)
        {
            return Refused(MeasurementRefusal.WeightingNotRecognised);
        }

        // Уровень входа решает, что именно можно измерить, а не можно ли вообще.
        //
        // Объём желудочковой системы требует объёмной серии по определению:
        // на шаге среза 7 мм между срезами нет ткани, и сумма вокселей — не
        // объём. Индекс Эванса не требует: он отмеряется на одном аксиальном
        // срезе, и толщина среза ему безразлична.
        //
        // Прежде ворота стояли на уровне входа целиком, и из-за этого метод
        // не касался 48 исследований выборки из 58 — все рутинные двумерные
        // серии (docs/data/README.md). Контрольной группе он не измерял ничего.
        var volumetric = series.Tier == AcquisitionTier.Extended;

        progress?.Report(new AnalysisProgress(AnalysisStage.Segmentation, 0.0));

        try
        {
            var volume = await this.volumes
                .LoadAsync(request.VolumeReference, series.PseudonymousSeriesId, cancellationToken)
                .ConfigureAwait(false);

            // Голова ли это — до сегментации, а не после. Снимок живота иначе
            // проходит весь разбор, чтобы получить отказ «желудочки
            // неправдоподобно малы»: правду о числе и неправду о сути.
            var (extent, looksLikeHead) = HeadPresence.Measure(volume, cancellationToken);

            if (!looksLikeHead)
            {
                return Refused(MeasurementRefusal.NotAHead);
            }

            var segmentation = this.segmentation.Segment(
                volume,
                series.Weighting,
                series.PseudonymousSeriesId,
                cancellationToken);

            progress?.Report(new AnalysisProgress(AnalysisStage.Segmentation, 1.0));
            progress?.Report(new AnalysisProgress(AnalysisStage.FeatureExtraction, 0.0));

            // Достоверность берётся у сегментации, а не задаётся здесь. Значение
            // по умолчанию у RegionVolumes — «надёжно», и забыть этот аргумент
            // значило бы выдать правдоподобный объём, полученный методом,
            // за который сам метод не ручается.
            var biomarkers = volumetric
                ? RegionVolumes.Measure(
                    segmentation.Mask,
                    series.Tier,
                    segmentation.Quality)
                : [];

            var evans = AutomaticEvansIndex.Measure(volume, segmentation, series.Weighting, cancellationToken);

            progress?.Report(new AnalysisProgress(AnalysisStage.FeatureExtraction, 1.0));

            // Пустая маска — отказ метода, а не нулевой объём. Желудочковой
            // системы объёмом 0 мл у живого человека не бывает: если сегментация
            // не выделила ни одного вокселя, порог встал не там или структура
            // не найдена. Пакетный замер выборки показал это на всех восьми
            // реальных сериях T1 — отчёт получал «0 мл, недостоверно», и число
            // выглядело как измерение. Пометка «недостоверно» этого не исправляет:
            // её читают после числа, а копируют вместе с числом не всегда.
            IReadOnlyList<Biomarker> measured =
            [
                .. biomarkers.Where(biomarker => biomarker.Value > 0),
                .. evans.Biomarker is { } index ? [index] : Array.Empty<Biomarker>(),
            ];

            return (measured, new MeasurementAttempt
            {
                // Способ называет себя сам: маршрутизация по взвешенности
                // выбирает ветвь во время работы, и записать здесь таблицу
                // целиком значило бы не записать ничего.
                LabelMapVersion = segmentation.Mask.Map.Version,
                MaskQuality = segmentation.Quality,
                SegmentationIssues = segmentation.Issues,
                Refusal = measured.Count > 0 ? null : Translate(evans.Refusal),
            });
        }
        catch (Exception exception)
            when (exception is InvalidDataException
                or NotSupportedException
                or DomainRuleViolationException)
        {
            // Измерение — надстройка над сценарием, а не его условие. Серия
            // в сжатом синтаксисе, неподдерживаемый формат пикселей, объём,
            // на котором сегментация не находит головы, — всё это встречается
            // в реальной выгрузке. Раньше такое исследование давало отчёт
            // с результатом контроля качества, и обязано давать его и теперь:
            // уронить сценарий ради необязательной части значило бы отнять
            // у врача то, что работало.
            //
            // Отменa и сбои, означающие дефект (ввод-вывод, нехватка памяти),
            // сюда не попадают и проходят наверх.
            return Refused(MeasurementRefusal.SeriesUnreadable);
        }
    }

    /// <summary>
    /// Переводит отказ индекса в доменный код.
    ///
    /// Перечисление индекса живёт в слое Inference вместе с методом, а отчёту
    /// нужен код, не зависящий от слоя. Перевод явный и без значения
    /// по умолчанию: новый отказ индекса обязан сломать сборку здесь, а не
    /// молча стать «причина не названа».
    /// </summary>
    private static MeasurementRefusal Translate(AutomaticEvansRefusal? refusal) => refusal switch
    {
        AutomaticEvansRefusal.SegmentationRefused => MeasurementRefusal.SegmentationRefused,
        AutomaticEvansRefusal.WeightingNotSupported => MeasurementRefusal.WeightingNotSupported,
        AutomaticEvansRefusal.AxesNotAligned => MeasurementRefusal.AxesNotAligned,
        AutomaticEvansRefusal.FrontalHornsNotFound => MeasurementRefusal.FrontalHornsNotFound,
        AutomaticEvansRefusal.InnerSkullNotFound => MeasurementRefusal.InnerSkullNotFound,
        null => MeasurementRefusal.Unspecified,
        _ => MeasurementRefusal.Unspecified,
    };
}
