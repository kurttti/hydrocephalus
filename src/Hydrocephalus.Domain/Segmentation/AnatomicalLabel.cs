namespace Hydrocephalus.Domain.Segmentation;

/// <summary>
/// Анатомическая метка сегментации. Код стабилен и версионируется вместе с label map:
/// смена состава меток обязана менять версию, иначе измерения окажутся несопоставимыми.
/// </summary>
/// <param name="Code">Стабильный код метки, например "lateral_ventricles".</param>
public readonly record struct AnatomicalLabel(string Code);
