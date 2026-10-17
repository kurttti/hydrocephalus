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
    /// Полных лет на момент исследования либо <see langword="null"/>, если
    /// источник возраста не называл. Значение 90 означает «столько или больше»:
    /// точный год в пожилом конце распределения сам работает идентификатором.
    ///
    /// Возраст — главный конфаундер размера желудочков, поэтому он нужен как
    /// ковариата сравнения групп. В файлы рабочей копии и набора он не
    /// попадает: профиль обезличивания удаляет (0010,1010) по-прежнему.
    /// </summary>
    public int? PatientAgeYears { get; init; }

    /// <summary>Пол: «M», «F», «O» либо <see langword="null"/>.</summary>
    public string? PatientSex { get; init; }

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
