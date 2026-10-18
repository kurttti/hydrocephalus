using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Provenance;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Segmentation;

namespace Hydrocephalus.Domain.Reporting;

/// <summary>
/// Канонический результат анализа одного исследования. Соответствует JSON-слою из ADR 0005:
/// человекочитаемое представление формируется из него и не может содержать ничего сверх него.
/// </summary>
public sealed record AnalysisReport
{
    /// <summary>Псевдонимный идентификатор исследования.</summary>
    public required string PseudonymousStudyId { get; init; }

    /// <summary>Момент формирования отчёта.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Серия, по которой составлен отчёт; <see langword="null"/>, если пригодной
    /// серии в исследовании не нашлось и анализ отказал до её выбора.
    ///
    /// Требование ADR 0005 («идентификаторы серий, псевдонимные»), не
    /// выполнявшееся до версии схемы 1.2.0. Почему одного исследования не
    /// хватает — в <see cref="AnalysedSeries"/>.
    /// </summary>
    public AnalysedSeries? Series { get; init; }

    /// <summary>Результат входного контроля качества.</summary>
    public required QualityAssessment Quality { get; init; }

    /// <summary>Итог анализа: прогноз либо отказ.</summary>
    public required AnalysisOutcome Outcome { get; init; }

    /// <summary>Версии конвейера, воспроизводящие результат.</summary>
    public required PipelineIdentity Pipeline { get; init; }

    /// <summary>Вычисленные признаки. При отказе список может быть пустым.</summary>
    public IReadOnlyList<Biomarker> Biomarkers { get; init; } = [];

    /// <summary>Результат сегментации, если она выполнялась.</summary>
    public SegmentationResult? Segmentation { get; init; }

    /// <summary>
    /// Чем размечали и почему измерения нет. ADR 0005 требует, чтобы отчёт
    /// нёс факт и причину отказа; до появления этого поля отчёт
    /// неизмеренного исследования не говорил ничего.
    /// </summary>
    public MeasurementAttempt? Measurement { get; init; }

    /// <summary>
    /// Комментарии врача. Отдельная коллекция, а не поле внутри прогноза:
    /// ручное исправление не является выводом модели.
    /// </summary>
    public IReadOnlyList<ClinicianAnnotation> ClinicianAnnotations { get; init; } = [];

    /// <summary>
    /// Точки, которыми врач отметил ручные измерения этой версии отчёта.
    ///
    /// Пустой список — «разметки нет», и это утверждение: до схемы 1.3.0 точки
    /// не записывались вовсе, поэтому у прежних отчётов значение без разметки —
    /// норма, а не потеря. Зачем они в отчёте — в <see cref="MarkedMeasurement"/>.
    /// </summary>
    public IReadOnlyList<MarkedMeasurement> Markings { get; init; } = [];
}
