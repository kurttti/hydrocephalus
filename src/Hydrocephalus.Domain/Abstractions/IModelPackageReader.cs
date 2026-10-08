namespace Hydrocephalus.Domain.Abstractions;

/// <summary>
/// Причина, по которой пакет модели не принят.
///
/// Код машинно-читаемый: объяснение для человека собирается на слое
/// представления. Частичная загрузка запрещена — любой из этих случаев означает
/// отказ целиком (ADR 0004).
/// </summary>
public enum ModelPackageRejection
{
    /// <summary>Причина не задана.</summary>
    Unspecified = 0,

    /// <summary>Файл не читается как пакет.</summary>
    NotAPackage = 1,

    /// <summary>Манифест отсутствует или не разбирается.</summary>
    ManifestUnreadable = 2,

    /// <summary>Список хешей отсутствует или не разбирается.</summary>
    ChecksumsUnreadable = 3,

    /// <summary>Подписи нет, либо она не сходится с доверенным ключом.</summary>
    SignatureInvalid = 4,

    /// <summary>Хеш файла не совпал с объявленным.</summary>
    ContentAltered = 5,

    /// <summary>Состав пакета не совпадает с объявленным: файл лишний или пропал.</summary>
    CompositionMismatch = 6,

    /// <summary>Пакет требует более новой версии приложения.</summary>
    ApplicationTooOld = 7,

    /// <summary>Версия предобработки или карты меток приложению неизвестна.</summary>
    IncompatibleContract = 8,
}

/// <summary>
/// Объявление пакета модели.
/// </summary>
/// <param name="FormatVersion">Версия самого формата пакета.</param>
/// <param name="ModelVersion">Версия модели, как её показывают врачу.</param>
/// <param name="MinimumApplicationVersion">Наименьшая версия приложения, с которой пакет совместим.</param>
/// <param name="PreprocessingVersion">Версия описания предобработки.</param>
/// <param name="LabelMapVersion">Версия карты меток.</param>
/// <param name="SigningKeyId">Идентификатор ключа, которым подписан список хешей.</param>
public sealed record ModelPackageManifest(
    string FormatVersion,
    string ModelVersion,
    string MinimumApplicationVersion,
    string PreprocessingVersion,
    string LabelMapVersion,
    string SigningKeyId);

/// <summary>Итог проверки пакета.</summary>
/// <param name="Manifest">Объявление пакета; <see langword="null"/> при отказе.</param>
/// <param name="Rejection">Причина отказа; <see langword="null"/>, если пакет принят.</param>
/// <param name="Detail">Уточнение к причине: имя файла или версия. Не содержит PHI.</param>
public sealed record ModelPackageCheck(
    ModelPackageManifest? Manifest,
    ModelPackageRejection? Rejection,
    string Detail = "")
{
    /// <summary>
    /// Веса сегментации — те самые байты, у которых сошёлся хеш.
    ///
    /// Заполняются только <see cref="IModelPackageReader.Open"/> и только у
    /// принятого пакета. Смысл поля в том, чтобы между проверкой и загрузкой не
    /// было второго чтения файла: проверить один набор байт, а скормить сети
    /// другой — ровно та подмена, против которой ADR 0004 и написан. Для экрана
    /// установки веса не нужны, и <see cref="IModelPackageReader.Verify"/> их не
    /// читает: это сотня мегабайт на решение, которому они ни к чему.
    /// </summary>
    public byte[]? Weights { get; init; }
}

/// <summary>
/// Чтение и проверка пакета модели.
///
/// Договор живёт в домене, а разбор архива и проверка подписи — в инфраструктуре:
/// сценарий установки не должен знать ни про ZIP, ни про ECDSA. Ключ, версия
/// приложения и перечень понятных версий настраиваются в реализации, а не
/// передаются из сценария, — иначе вызывающий мог бы подставить свой ключ.
/// </summary>
public interface IModelPackageReader
{
    /// <summary>
    /// Проверяет пакет, не читая весов.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <returns>Объявление пакета либо названная причина отказа.</returns>
    ModelPackageCheck Verify(string packagePath);

    /// <summary>
    /// Проверяет пакет и отдаёт веса, проверенные тем же проходом.
    /// </summary>
    /// <param name="packagePath">Путь к файлу пакета.</param>
    /// <returns>
    /// Объявление вместе с весами в <see cref="ModelPackageCheck.Weights"/> либо
    /// названная причина отказа. Проверка та же, что у
    /// <see cref="Verify"/>, — отдельного, более снисходительного пути к весам
    /// не существует.
    /// </returns>
    ModelPackageCheck Open(string packagePath);
}
