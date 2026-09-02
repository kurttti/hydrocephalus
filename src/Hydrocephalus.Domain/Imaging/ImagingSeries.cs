namespace Hydrocephalus.Domain.Imaging;

/// <summary>
/// Тип взвешенности серии, насколько он определён по метаданным.
/// </summary>
public enum SeriesWeighting
{
    /// <summary>Не определено.</summary>
    Unknown = 0,

    /// <summary>T1-взвешенная серия.</summary>
    T1 = 1,

    /// <summary>T2-взвешенная серия.</summary>
    T2 = 2,

    /// <summary>FLAIR.</summary>
    Flair = 3,
}

/// <summary>
/// Серия исследования в рабочей копии. Хранит только псевдонимные идентификаторы:
/// доменная модель не видит ни исходных UID, ни имён файлов.
/// </summary>
public sealed record ImagingSeries
{
    /// <summary>Псевдонимный идентификатор серии.</summary>
    public required string PseudonymousSeriesId { get; init; }

    /// <summary>Геометрия серии.</summary>
    public required SeriesGeometry Geometry { get; init; }

    /// <summary>Тип взвешенности.</summary>
    public required SeriesWeighting Weighting { get; init; }

    /// <summary>
    /// Признак постконтрастной серии. Такие серии не подаются в MRI-only конвейер
    /// ни на одном уровне входа (docs/clinical/README.md).
    /// </summary>
    public required bool IsContrastEnhanced { get; init; }

    /// <summary>Уровень входа, определяемый геометрией серии.</summary>
    public AcquisitionTier Tier => Geometry.Tier;
}
