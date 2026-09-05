using FellowOakDicom;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Что именно не прошло проверку деидентификации.
/// </summary>
internal enum DeidentificationViolationCode
{
    /// <summary>Код не задан.</summary>
    Unspecified = 0,

    /// <summary>Тег, удаляемый профилем, остался в результате.</summary>
    ResidualIdentifyingTag = 1,

    /// <summary>Приватный, оверлейный или кривой тег остался в результате.</summary>
    ResidualPrivateOrOverlayTag = 2,

    /// <summary>Значение из исходного файла встретилось в результате.</summary>
    ResidualSourceValue = 3,

    /// <summary>Значение из исходного файла встретилось в пути рабочей копии.</summary>
    ResidualSourceValueInPath = 4,

    /// <summary>Тег, обязательный по профилю, отсутствует.</summary>
    MissingRetainedTag = 5,
}

/// <summary>
/// Отдельное нарушение. Хранится только тег или указание на путь — само значение
/// не сохраняется, иначе отчёт проверки сам стал бы носителем PHI.
/// </summary>
/// <param name="Code">Код нарушения.</param>
/// <param name="Location">Тег в виде (gggg,eeee) либо условное имя места.</param>
internal readonly record struct DeidentificationViolation(
    DeidentificationViolationCode Code,
    string Location);

/// <summary>
/// Проверка результата деидентификации — та самая утилита из плана проверки ADR 0003.
///
/// Проверка идёт не только по списку тегов. Список отвечает на вопрос «убрали ли мы
/// то, что собирались», но проходит и тогда, когда то же значение уцелело в другом
/// теге или во вложенной последовательности. Поэтому основная проверка — поиск
/// исходных значений в результате целиком, включая имя файла и путь.
///
/// Запускается не только в тестах: импорт вызывает её до записи файла и отказывается
/// писать при любом нарушении.
/// </summary>
internal static class DeidentificationAudit
{
    /// <summary>Условное имя места для нарушений, найденных в пути рабочей копии.</summary>
    internal const string PathLocation = "path";

    /// <summary>
    /// Проверяет деидентифицированный экземпляр.
    /// </summary>
    /// <param name="dataset">Обработанный набор тегов.</param>
    /// <param name="fileMetaInfo">Метаинформация файла либо <see langword="null"/>.</param>
    /// <param name="sourceSecrets">Исходные значения, которых не должно быть в результате.</param>
    /// <param name="relativePath">Путь файла внутри рабочей копии.</param>
    /// <returns>Список нарушений; пустой список означает успех.</returns>
    internal static IReadOnlyList<DeidentificationViolation> Inspect(
        DicomDataset dataset,
        DicomDataset? fileMetaInfo,
        IReadOnlySet<string> sourceSecrets,
        string relativePath)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(sourceSecrets);

        var violations = new List<DeidentificationViolation>();

        Inspect(dataset, sourceSecrets, violations);

        if (fileMetaInfo is not null)
        {
            Inspect(fileMetaInfo, sourceSecrets, violations);
        }

        foreach (var secret in sourceSecrets)
        {
            if (relativePath.Contains(secret, StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new DeidentificationViolation(
                    DeidentificationViolationCode.ResidualSourceValueInPath,
                    PathLocation));
                break;
            }
        }

        return violations;
    }

    private static void Inspect(
        DicomDataset dataset,
        IReadOnlySet<string> secrets,
        List<DeidentificationViolation> violations)
    {
        foreach (var item in dataset)
        {
            if (DeidentificationProfile.IsInRemovedGroup(item.Tag))
            {
                violations.Add(new DeidentificationViolation(
                    DeidentificationViolationCode.ResidualPrivateOrOverlayTag,
                    Describe(item.Tag)));
                continue;
            }

            if (DeidentificationProfile.IsRemoved(item.Tag))
            {
                violations.Add(new DeidentificationViolation(
                    DeidentificationViolationCode.ResidualIdentifyingTag,
                    Describe(item.Tag)));
                continue;
            }

            if (item is DicomSequence sequence)
            {
                foreach (var child in sequence.Items)
                {
                    Inspect(child, secrets, violations);
                }

                continue;
            }

            // Двоичные значения не проверяются построчно: вписанный в пиксели текст
            // ловится не поиском подстроки, а отдельной проверкой BurnedInAnnotation
            // и ручным контролем.
            if (!item.ValueRepresentation.IsString)
            {
                continue;
            }

            if (!dataset.TryGetValues<string>(item.Tag, out var values) || values is null)
            {
                continue;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (secrets.Any(secret => value.Contains(secret, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add(new DeidentificationViolation(
                        DeidentificationViolationCode.ResidualSourceValue,
                        Describe(item.Tag)));
                    break;
                }
            }
        }
    }

    private static string Describe(DicomTag tag) =>
        $"({tag.Group:x4},{tag.Element:x4})";
}
