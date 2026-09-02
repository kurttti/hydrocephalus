namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Ограничения приёма произвольного DICOM. Злонамеренный DICOM назван отдельной угрозой
/// в docs/security/README.md, мера — лимиты размера и глубины плюс безопасный разбор.
/// Значения по умолчанию рассчитаны на клиническую МРТ, а не на произвольные данные.
/// </summary>
public sealed record DicomImportOptions
{
    /// <summary>
    /// Соль для псевдонимизации идентификаторов. Обязательна и хранится только
    /// во внешнем защищённом реестре (ADR 0003): её утечка вместе с данными
    /// обесценивает замену UID.
    /// </summary>
    public required string PseudonymSalt { get; init; }

    /// <summary>Максимальный размер одного файла, байт.</summary>
    public long MaxFileSizeBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Максимальная глубина вложенности каталогов относительно корня обхода.</summary>
    public int MaxDirectoryDepth { get; init; } = 12;

    /// <summary>Максимальное число файлов, просматриваемых за один импорт.</summary>
    public int MaxFileCount { get; init; } = 200_000;
}
