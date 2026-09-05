using Hydrocephalus.Domain.Abstractions;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;
using Hydrocephalus.Inference.QualityControl;

namespace Hydrocephalus.Inference;

/// <summary>
/// Конвейер, выполняющий входной контроль качества и отказывающийся от ответа.
///
/// Это не заглушка и не временная подмена: пока нет проверенного model package,
/// отказ — единственно верное поведение. ADR 0004 разрешает читать веса только
/// из подписанного и проверенного пакета, а сам пакет появляется в M4 вместе
/// с обученной моделью. Вернуть здесь любую вероятность значило бы выдать
/// невалидированное число за результат.
///
/// Контроль качества при этом настоящий: серия проверяется по геометрии
/// и метаданным, и непригодный вход отклоняется до всякого разговора о модели.
/// Поэтому приложение уже сейчас проходит сценарий целиком и заканчивает его
/// честным «анализ невозможен», а не пустым экраном.
/// </summary>
public sealed class QualityControlOnlyEngine : IInferenceEngine
{
    private readonly InputQualityControl qualityControl;
    private readonly PipelineIdentity pipeline;

    /// <summary>
    /// Создаёт конвейер.
    /// </summary>
    /// <param name="qualityControl">Входной контроль качества.</param>
    /// <param name="pipeline">
    /// Версии конвейера. Значения по умолчанию здесь нет намеренно: provenance
    /// отчёта должен приходить от того, кто собирает приложение и знает версии,
    /// а подставленное «unknown» выглядело бы как заполненное поле.
    /// </param>
    public QualityControlOnlyEngine(InputQualityControl qualityControl, PipelineIdentity pipeline)
    {
        ArgumentNullException.ThrowIfNull(qualityControl);
        ArgumentNullException.ThrowIfNull(pipeline);

        this.qualityControl = qualityControl;
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
    /// Отказывается от ответа: проверенного model package нет.
    /// </summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Отказ с кодом <see cref="RefusalCode.ModelPackageUnusable"/>.</returns>
    public Task<AnalysisOutcome> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();

        // Отказ наступает на проверке пакета, а не после мнимой обработки:
        // показывать прогресс сегментации, которой не будет, — обман.
        progress?.Report(new AnalysisProgress(AnalysisStage.ValidatingModelPackage, 1.0));

        AnalysisOutcome outcome = new AnalysisOutcome.Refused
        {
            Reason = new RefusalReason { Code = RefusalCode.ModelPackageUnusable },
        };

        return Task.FromResult(outcome);
    }
}
