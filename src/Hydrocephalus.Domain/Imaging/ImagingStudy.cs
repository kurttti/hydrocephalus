namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Исследование в рабочей копии: набор серий одного пациента, полученных в одном сеансе.
/// Связь с исходными данными существует только во внешнем защищённом реестре.
/// </summary>
public sealed record ImagingStudy
{
    /// <summary>Псевдонимный идентификатор исследования.</summary>
    public required string PseudonymousStudyId { get; init; }

    /// <summary>
    /// Псевдонимный идентификатор пациента. Вычисляется по правилу дедупликации
    /// из docs/data/README.md и нужен для patient-level split.
    /// </summary>
    public required string PseudonymousSubjectId { get; init; }

    /// <summary>Серии исследования.</summary>
    public required IReadOnlyList<ImagingSeries> Series { get; init; }

    /// <summary>
    /// Наилучший доступный уровень входа по всем неконтрастным сериям исследования.
    /// Постконтрастные серии не учитываются: они не подаются в конвейер.
    /// </summary>
    public AcquisitionTier BestAvailableTier =>
        Series.Where(series => !series.IsContrastEnhanced)
            .Select(series => series.Tier)
            .DefaultIfEmpty(AcquisitionTier.Unusable)
            .Max();
}
