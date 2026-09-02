namespace Hydrocephalus.Domain.Segmentation;

/// <summary>
/// Результат сегментации: описание масок, а не сами воксели.
/// Пиксельные данные живут в рабочей копии и адресуются ссылкой,
/// чтобы контракт не менялся при выносе инференса в отдельный процесс (ADR 0002).
/// </summary>
public sealed record SegmentationResult
{
    /// <summary>Версия label map, которой соответствуют метки.</summary>
    public required string LabelMapVersion { get; init; }

    /// <summary>Метки, фактически присутствующие в результате.</summary>
    public required IReadOnlyList<AnatomicalLabel> Labels { get; init; }

    /// <summary>
    /// Ссылка на маски в рабочей копии. Домен не знает, файл это или запись в хранилище.
    /// </summary>
    public required string MaskReference { get; init; }

    /// <summary>
    /// Результат геометрического QC масок: сегментация может быть технически выполнена,
    /// но непригодна для измерений.
    /// </summary>
    public required bool PassedGeometricQualityControl { get; init; }
}
