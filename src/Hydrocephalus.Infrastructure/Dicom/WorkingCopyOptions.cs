namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Расположение рабочей копии.
///
/// ADR 0006 требует от этого каталога трёх вещей: ограниченного ACL, шифрования
/// содержимого и гарантированного удаления по TTL. Здесь реализовано только
/// ограничение доступа; шифрование и политика удаления — отдельная работа
/// по ADR 0006, и до неё каталог защищён правами доступа, но не шифром.
/// </summary>
public sealed record WorkingCopyOptions
{
    /// <summary>Корневой каталог рабочих копий.</summary>
    public required string RootDirectory { get; init; }

    /// <summary>
    /// Ограничивать ли доступ к создаваемым каталогам текущей учётной записью.
    /// Выключается только в тестах на файловых системах без ACL.
    /// </summary>
    public bool RestrictAccessToCurrentUser { get; init; } = true;
}
