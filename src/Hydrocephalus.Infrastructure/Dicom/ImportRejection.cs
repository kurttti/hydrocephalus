namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Причина, по которой файл не принят. Каждое исключение получает машинно-читаемый код
/// и человекочитаемое объяснение (docs/data/README.md); текст формируется на слое
/// представления, здесь хранится только код.
/// </summary>
public enum ImportRejectionCode
{
    /// <summary>Код не задан.</summary>
    Unspecified = 0,

    /// <summary>Файл не является DICOM или заголовок повреждён.</summary>
    NotADicomFile = 1,

    /// <summary>Файл превышает допустимый размер.</summary>
    FileTooLarge = 2,

    /// <summary>Каталог вложен глубже допустимого.</summary>
    DirectoryTooDeep = 3,

    /// <summary>Превышено допустимое число файлов за один импорт.</summary>
    TooManyFiles = 4,

    /// <summary>Разбор файла завершился ошибкой.</summary>
    UnreadableDataset = 5,

    /// <summary>В файле отсутствует SeriesInstanceUID, группировка невозможна.</summary>
    MissingSeriesIdentifier = 6,
}

/// <summary>
/// Отклонённый файл.
///
/// Путь к файлу намеренно не сохраняется: в исходном сборе данных имена файлов и папок
/// содержат фамилии пациентов (docs/data/README.md), поэтому попадание пути в журнал
/// было бы утечкой PHI. Вместо него используется устойчивая непрозрачная ссылка.
/// </summary>
/// <param name="Code">Машинно-читаемый код отказа.</param>
/// <param name="OpaqueFileReference">Псевдонимная ссылка на файл для корреляции.</param>
public readonly record struct ImportRejection(ImportRejectionCode Code, string OpaqueFileReference);
