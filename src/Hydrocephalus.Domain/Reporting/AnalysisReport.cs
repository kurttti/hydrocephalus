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
    /// Комментарии врача. Отдельная коллекция, а не поле внутри прогноза:
    /// ручное исправление не является выводом модели.
    /// </summary>
    public IReadOnlyList<ClinicianAnnotation> ClinicianAnnotations { get; init; } = [];
}
