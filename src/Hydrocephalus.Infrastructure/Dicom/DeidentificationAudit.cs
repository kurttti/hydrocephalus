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
    /// Длина, начиная с которой число ищется подстрокой: восемь цифр — дата DA.
    /// </summary>
    internal const int LongNumberLength = 8;

    // Разделители числовых лексем: всё, кроме цифр и точки. Знак минус тоже
    // разделитель, иначе «-12345» не совпало бы с «12345».
    private static readonly char[] NotNumeric = [.. Enumerable
        .Range(0, 128)
        .Select(code => (char)code)
        .Where(character => !char.IsAsciiDigit(character) && character != '.')];

    /// <summary>
    /// Проверяет деидентифицированный экземпляр.
    /// </summary>
    /// <param name="dataset">Обработанный набор тегов.</param>
    /// <param name="fileMetaInfo">Метаинформация файла либо <see langword="null"/>.</param>
    /// <param name="sourceSecrets">Исходные значения, которых не должно быть в результате.</param>
    /// <param name="relativePath">Путь файла внутри рабочей копии.</param>
    /// <param name="descriptiveOnlySecrets">
    /// Исходные значения только из описательных полей; их дословное совпадение
    /// с техническим полем нарушением не считается. <see langword="null"/> —
    /// не прощать ничего.
    /// </param>
    /// <returns>Список нарушений; пустой список означает успех.</returns>
    internal static IReadOnlyList<DeidentificationViolation> Inspect(
        DicomDataset dataset,
        DicomDataset? fileMetaInfo,
        IReadOnlySet<string> sourceSecrets,
        string relativePath,
        IReadOnlySet<string>? descriptiveOnlySecrets = null)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(sourceSecrets);

        var violations = new List<DeidentificationViolation>();
        var excusable = descriptiveOnlySecrets ?? new HashSet<string>(StringComparer.Ordinal);

        Inspect(dataset, sourceSecrets, excusable, violations);

        if (fileMetaInfo is not null)
        {
            Inspect(fileMetaInfo, sourceSecrets, excusable, violations);
        }

        foreach (var secret in sourceSecrets)
        {
            if (PathContains(relativePath, secret))
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
        IReadOnlySet<string> excusable,
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
                    Inspect(child, secrets, excusable, violations);
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

                var technical = DeidentificationProfile.IsTechnical(item.Tag);

                if (secrets.Any(secret => Contains(value, secret)
                    && !(technical && IsExcusedRepetition(value, secret, excusable))))
                {
                    violations.Add(new DeidentificationViolation(
                        DeidentificationViolationCode.ResidualSourceValue,
                        Describe(item.Tag)));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Встречается ли исходное значение в значении результата.
    ///
    /// Короткое число ищется как отдельное число, а не как подстрока. Прогон
    /// по ретроспективной выборке показал, что подстрочный поиск коротких чисел
    /// отказывает большинству исследований и ни разу не находит утечку:
    /// серийный номер аппарата «находился» в координатах среза, номер
    /// исследования — в заново созданном UID вида 2.25.…, цифровой PatientID —
    /// в ImagePositionPatient. Три-пять цифр подряд встречаются в любом
    /// числовом поле случайно.
    ///
    /// Отдельное число при этом по-прежнему находится: идентификатор, скопированный
    /// в текстовое поле («ID 12345»), даёт нарушение. Длинные числа — даты
    /// рождения, длинные номера карт — ищутся подстрокой, как раньше: случайное
    /// совпадение восьми цифр подряд пренебрежимо, а дата рождения внутри
    /// значения DT — именно та утечка, ради которой поиск ведётся.
    ///
    /// Текстовые значения ищутся подстрокой без изменений.
    /// </summary>
    /// <param name="value">Значение результата.</param>
    /// <param name="secret">Исходное значение.</param>
    /// <returns><see langword="true"/>, если исходное значение найдено.</returns>
    internal static bool Contains(string value, string secret)
    {
        if (!IsShortNumber(secret))
        {
            return value.Contains(secret, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var token in value.Split(NotNumeric, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token.Trim('.'), secret.Trim('.'), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Встречается ли исходное значение в пути рабочей копии.
    ///
    /// Путь собирается из шестнадцатеричных псевдонимов, и короткое число
    /// находится в них подстрокой случайно — повторный прогон по выборке дал
    /// ровно такой отказ. Поэтому короткое число сравнивается с целым сегментом
    /// пути без расширения: путь, по ошибке построенный из исходного значения
    /// («12345.dcm»), по-прежнему даёт нарушение. Текст и длинные числа ищутся
    /// подстрокой, как раньше.
    /// </summary>
    /// <param name="relativePath">Путь файла внутри рабочей копии.</param>
    /// <param name="secret">Исходное значение.</param>
    /// <returns><see langword="true"/>, если исходное значение найдено.</returns>
    internal static bool PathContains(string relativePath, string secret)
    {
        // Короткое значение из шестнадцатеричных знаков («FE», «DE», «AB»)
        // находится внутри шестнадцатеричного псевдонима так же случайно,
        // как короткое число: папка пациента иНТГ не открывалась именно так.
        if (!IsShortNumber(secret) && !IsShortHexLike(secret))
        {
            return relativePath.Contains(secret, StringComparison.OrdinalIgnoreCase);
        }

        return relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Any(segment => string.Equals(segment, secret, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Прощается ли совпадение: техническое поле целиком равно значению,
    /// пришедшему только из описательных полей.
    ///
    /// Прогон по выборке: 39 исследований не открывались, потому что имя
    /// станции равнялось модели аппарата, а описание исследования «HEAD» —
    /// области и катушке. Нового сведения о пациенте такое повторение не
    /// раскрывает: значение и так лежит в поле, которое профиль сохраняет.
    /// Подстрока не прощается никогда, как и значение, встреченное хоть раз
    /// в идентифицирующем поле.
    /// </summary>
    /// <param name="value">Значение технического поля результата.</param>
    /// <param name="secret">Исходное значение.</param>
    /// <param name="excusable">Значения только из описательных полей.</param>
    /// <returns><see langword="true"/>, если совпадение прощается.</returns>
    internal static bool IsExcusedRepetition(string value, string secret, IReadOnlySet<string> excusable) =>
        excusable.Contains(secret)
        && string.Equals(value.Trim(), secret, StringComparison.OrdinalIgnoreCase);

    private static bool IsShortHexLike(string secret) =>
        secret.Length < LongNumberLength && secret.All(char.IsAsciiHexDigit);

    private static bool IsShortNumber(string secret) =>
        secret.Length < LongNumberLength
        && secret.Any(char.IsAsciiDigit)
        && secret.All(character => char.IsAsciiDigit(character) || character == '.');

    private static string Describe(DicomTag tag) =>
        $"({tag.Group:x4},{tag.Element:x4})";
}
