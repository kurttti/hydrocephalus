using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Этап конвейера анализа. Код, а не текст: перевод выполняется на слое представления.
/// </summary>
public enum AnalysisStage
{
    /// <summary>Этап не задан.</summary>
    Unspecified = 0,

    /// <summary>Проверка model package и совместимости.</summary>
    ValidatingModelPackage = 1,

    /// <summary>Входной контроль качества и проверка на выход за пределы распределения.</summary>
    QualityControl = 2,

    /// <summary>Ориентация, ресэмплинг и нормализация.</summary>
    Preprocessing = 3,

    /// <summary>Сегментация анатомических структур.</summary>
    Segmentation = 4,

    /// <summary>Постобработка масок и геометрический контроль.</summary>
    MaskPostProcessing = 5,

    /// <summary>Вычисление количественных признаков.</summary>
    FeatureExtraction = 6,

    /// <summary>Классификация и калибровка.</summary>
    Classification = 7,
}

/// <summary>
/// Сообщение о прогрессе. Простая сериализуемая запись без ссылок на объекты процесса:
/// контракт обязан пережить вынос инференса в отдельный процесс (ADR 0002).
/// </summary>
/// <param name="Stage">Текущий этап.</param>
/// <param name="CompletedFraction">Доля выполненного от 0 до 1.</param>
public readonly record struct AnalysisProgress(AnalysisStage Stage, double CompletedFraction);

/// <summary>
/// Запрос на анализ. Ссылается на серию рабочей копии идентификатором,
/// а не передаёт пиксельные данные: буферы не пересекают границу контракта.
/// </summary>
public sealed record AnalysisRequest
{
    /// <summary>Исследование в рабочей копии.</summary>
    public required ImagingStudy Study { get; init; }

    /// <summary>Псевдонимный идентификатор серии, выбранной для анализа.</summary>
    public required string PseudonymousSeriesId { get; init; }

    /// <summary>Ссылка на рабочую копию воксельных данных в защищённом каталоге.</summary>
    public required string VolumeReference { get; init; }
}

/// <summary>
/// Локальный конвейер анализа изображения. Реализация живёт в слое Inference и скрывает
/// ONNX Runtime; ни один другой слой не знает о движке инференса.
/// </summary>
public interface IInferenceEngine
{
    /// <summary>
    /// Сообщает версии конвейера, которыми получен результат. Нужны отчёту как provenance:
    /// без них результат невоспроизводим (docs/ml/README.md).
    /// </summary>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Версии предобработки, схемы признаков, label map и сборки приложения.</returns>
    Task<PipelineIdentity> DescribePipelineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Выполняет входной контроль качества.
    /// </summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Результат контроля качества.</returns>
    Task<QualityAssessment> RunQualityControlAsync(
        AnalysisRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Выполняет анализ и возвращает прогноз либо отказ.
    /// Операция длительная, обязана быть отменяемой и сообщать прогресс.
    /// </summary>
    /// <param name="request">Запрос на анализ.</param>
    /// <param name="progress">Приёмник сообщений о прогрессе; может отсутствовать.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Итог анализа.</returns>
    Task<AnalysisOutcome> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken);
}
