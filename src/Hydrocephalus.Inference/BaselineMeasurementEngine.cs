using Hydrocephalus.Domain;
using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
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
/// - **не считает индекс Эванса.** Формула реализована и проверена, но ей
///   нужны четыре точки на срезе, а выводить их из маски конвейер не умеет:
///   выбор аксиального среза для измерения — клиническое соглашение, а не
///   геометрия. Число, похожее на индекс Эванса и полученное иначе, хуже
///   отсутствующего числа;
/// - **не сохраняет маску.** <see cref="Domain.Segmentation.SegmentationResult"/>
///   требует ссылки на маски в рабочей копии, а записывать их туда пока некуда:
///   для этого нужны шифрованное хранение масок и срок их жизни (ADR 0006).
///   Поэтому раздел сегментации в отчёте остаётся пустым, хотя сегментация
///   выполнялась;
/// - **не измеряет там, где объём не читается.** Сжатый синтаксис передачи,
///   неподдерживаемый формат пикселей, объём без распознаваемой головы —
///   обычные состояния реальной выгрузки, а не дефекты. Измерений в таком
///   случае нет, а отчёт с результатом контроля качества всё равно выходит;
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
    public BaselineMeasurementEngine(
        InputQualityControl qualityControl,
        IVolumeSource volumes,
        PipelineIdentity pipeline)
    {
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(pipeline);

        this.qualityControl = qualityControl;
        this.volumes = volumes;
        this.pipeline = pipeline;
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
    public Task<QualityAssessment> RunQualityControlAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(this.qualityControl.Evaluate(request));
    }

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

        var biomarkers = await this.MeasureAsync(request, progress, cancellationToken)
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
        };
    }

    private async Task<IReadOnlyList<Biomarker>> MeasureAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var series = request.Study.Series.FirstOrDefault(item => string.Equals(
            item.PseudonymousSeriesId,
            request.PseudonymousSeriesId,
            StringComparison.Ordinal));

        if (series is null)
        {
            // Рассогласование запроса и рабочей копии. Молчаливо вернуть
            // «измерений нет» здесь означало бы допустить измерение
            // неизвестно чего.
            throw new DomainRuleViolationException(
                "The analysis request names a series that the study does not contain.");
        }

        // Два условия, при которых измерять нельзя, и оба проверяются, а не
        // предполагаются. Пустой список отличается от нулевого объёма: нуль —
        // это результат, а пустой список — его отсутствие.
        if (series.Tier != AcquisitionTier.Extended || series.Weighting == SeriesWeighting.Unknown)
        {
            return [];
        }

        progress?.Report(new AnalysisProgress(AnalysisStage.Segmentation, 0.0));

        try
        {
            var volume = await this.volumes
                .LoadAsync(request.VolumeReference, series.PseudonymousSeriesId, cancellationToken)
                .ConfigureAwait(false);

            var segmentation = BaselineVentricleSegmentation.Segment(
                volume,
                series.Weighting,
                cancellationToken: cancellationToken);

            progress?.Report(new AnalysisProgress(AnalysisStage.Segmentation, 1.0));
            progress?.Report(new AnalysisProgress(AnalysisStage.FeatureExtraction, 0.0));

            // Достоверность берётся у сегментации, а не задаётся здесь. Значение
            // по умолчанию у RegionVolumes — «надёжно», и забыть этот аргумент
            // значило бы выдать правдоподобный объём, полученный методом,
            // за который сам метод не ручается.
            var biomarkers = RegionVolumes.Measure(
                segmentation.Mask,
                series.Tier,
                segmentation.Quality);

            progress?.Report(new AnalysisProgress(AnalysisStage.FeatureExtraction, 1.0));

            return biomarkers;
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
            return [];
        }
    }
}
