using System.Globalization;
using FellowOakDicom;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Деидентифицированный экземпляр вместе с исходными значениями, которые в нём
/// не должны встречаться.
/// </summary>
internal sealed record DeidentifiedInstance
{
    /// <summary>Обработанный набор тегов.</summary>
    public required DicomDataset Dataset { get; init; }

    /// <summary>Псевдонимный идентификатор экземпляра, используемый как имя файла.</summary>
    public required string PseudonymousInstanceId { get; init; }

    /// <summary>
    /// Значения из исходного файла, попадание которых в результат означает утечку.
    /// Собираются до обработки и передаются в <see cref="DeidentificationAudit"/>:
    /// проверка «таких-то тегов нет» проходит и при утечке того же значения
    /// в другом теге или во вложенной последовательности.
    /// </summary>
    public required IReadOnlySet<string> SourceSecrets { get; init; }
}

/// <summary>
/// Применение профиля деидентификации к набору тегов (ADR 0003).
///
/// Обработка идёт в памяти, файл записывается уже очищенным. Обратный порядок —
/// скопировать, затем чистить — оставлял бы окно, в котором исходные данные лежат
/// внутри «защищённого» каталога, и падение в этом окне оставляло бы их на диске.
/// </summary>
internal sealed class DicomDeidentifier
{
    /// <summary>
    /// Наименьшая длина значения, которое имеет смысл искать в результате.
    /// Более короткие значения (код пола, односимвольные пометки) совпадают
    /// со служебными строками где угодно и дают только ложные срабатывания.
    /// </summary>
    internal const int MinimumSecretLength = 3;

    private readonly DicomImportOptions options;

    /// <summary>Создаёт деидентификатор.</summary>
    /// <param name="options">Параметры импорта, включая соль псевдонимизации.</param>
    internal DicomDeidentifier(DicomImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <summary>
    /// Деидентифицирует набор тегов одного экземпляра.
    /// </summary>
    /// <param name="source">Исходный набор тегов; не изменяется.</param>
    /// <param name="pseudonymousSubjectId">Псевдоним пациента, задающий сдвиг дат.</param>
    /// <returns>Обработанный экземпляр и исходные значения для проверки.</returns>
    internal DeidentifiedInstance Deidentify(DicomDataset source, string pseudonymousSubjectId)
    {
        ArgumentNullException.ThrowIfNull(source);

        var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectSecrets(source, secrets);

        var instanceId = Pseudonyms.Derive(
            this.options.PseudonymSalt,
            "instance",
            source.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty));

        var dayShift = DeidentificationProfile.DeriveDayShift(
            this.options.PseudonymSalt,
            pseudonymousSubjectId);

        var clean = source.Clone();
        this.Sanitize(clean, dayShift);

        return new DeidentifiedInstance
        {
            Dataset = clean,
            PseudonymousInstanceId = instanceId,
            SourceSecrets = secrets,
        };
    }

    /// <summary>
    /// Собирает исходные значения, которые не должны пережить обработку:
    /// значения удаляемых тегов, все имена (VR PN) и все замещаемые UID.
    /// Обход рекурсивный: исходные UID прячутся во вложенных последовательностях
    /// вроде ReferencedImageSequence.
    /// </summary>
    private static void CollectSecrets(DicomDataset dataset, HashSet<string> secrets)
    {
        foreach (var item in dataset)
        {
            if (item is DicomSequence sequence)
            {
                foreach (var child in sequence.Items)
                {
                    CollectSecrets(child, secrets);
                }

                continue;
            }

            // Рост и вес пациента профиль удаляет, но искать их значения в результате
            // бессмысленно: это физические величины, а не идентификаторы, и вес «100»
            // совпадает с кодом кодировки ISO_IR 100 у каждого второго файла.
            // Удаление самих тегов по-прежнему проверяется по списку профиля.
            if (item.Tag == DicomTag.PatientWeight || item.Tag == DicomTag.PatientSize)
            {
                continue;
            }

            var isSecret = DeidentificationProfile.IsRemoved(item.Tag)
                || item.ValueRepresentation == DicomVR.PN
                || (item.ValueRepresentation == DicomVR.UI
                    && DeidentificationProfile.IsRemappedUid(item.Tag));

            if (!isSecret)
            {
                continue;
            }

            foreach (var value in ReadStrings(dataset, item.Tag))
            {
                var trimmed = value.Trim();

                if (trimmed.Length >= MinimumSecretLength)
                {
                    secrets.Add(trimmed);
                }
            }
        }
    }

    private static IEnumerable<string> ReadStrings(DicomDataset dataset, DicomTag tag) =>
        dataset.TryGetValues<string>(tag, out var values) && values is not null
            ? values.Where(value => !string.IsNullOrWhiteSpace(value))
            : [];

    /// <summary>Сдвигает дату в формате DICOM DA на заданное число дней.</summary>
    private static string? ShiftDate(string value, int days)
    {
        var trimmed = value.Trim();

        return DateTime.TryParseExact(
            trimmed,
            "yyyyMMdd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed.AddDays(days).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// Сдвигает дату внутри значения DT, сохраняя время и часовой пояс.
    /// Сдвиг только даты и только целыми сутками — иначе интервалы между
    /// исследованиями перестанут совпадать с исходными.
    /// </summary>
    private static string? ShiftDateTime(string value, int days)
    {
        var trimmed = value.Trim();

        if (trimmed.Length < 8)
        {
            return null;
        }

        var shifted = ShiftDate(trimmed[..8], days);

        return shifted is null ? null : shifted + trimmed[8..];
    }

    private void Sanitize(DicomDataset dataset, int dayShift)
    {
        var removals = new List<DicomTag>();

        foreach (var item in dataset.ToList())
        {
            if (DeidentificationProfile.IsInRemovedGroup(item.Tag)
                || DeidentificationProfile.IsRemoved(item.Tag))
            {
                removals.Add(item.Tag);
                continue;
            }

            if (item is DicomSequence sequence)
            {
                foreach (var child in sequence.Items)
                {
                    this.Sanitize(child, dayShift);
                }

                continue;
            }

            if (item.ValueRepresentation == DicomVR.UI
                && DeidentificationProfile.IsRemappedUid(item.Tag))
            {
                this.RemapUids(dataset, item.Tag, removals);
                continue;
            }

            if (item.ValueRepresentation == DicomVR.DA)
            {
                Rewrite(dataset, item.Tag, removals, value => ShiftDate(value, dayShift));
                continue;
            }

            if (item.ValueRepresentation == DicomVR.DT)
            {
                Rewrite(dataset, item.Tag, removals, value => ShiftDateTime(value, dayShift));
            }
        }

        dataset.Remove([.. removals]);
    }

    private void RemapUids(DicomDataset dataset, DicomTag tag, List<DicomTag> removals) =>
        Rewrite(
            dataset,
            tag,
            removals,

            // Область имён одна для всех тегов с UID: только так один и тот же
            // исходный UID даёт один и тот же замещающий, и ссылки внутри
            // исследования продолжают указывать на те же экземпляры.
            value => PseudonymousUid.Derive(this.options.PseudonymSalt, "uid", value.Trim()));

    /// <summary>
    /// Перезаписывает значения тега. Значение, которое не удалось преобразовать,
    /// приводит к удалению тега целиком: оставить исходное было бы утечкой,
    /// а подставить произвольное — искажением данных.
    /// </summary>
    private static void Rewrite(
        DicomDataset dataset,
        DicomTag tag,
        List<DicomTag> removals,
        Func<string, string?> transform)
    {
        var source = ReadStrings(dataset, tag).ToArray();

        if (source.Length == 0)
        {
            removals.Add(tag);
            return;
        }

        var rewritten = new string[source.Length];

        for (var index = 0; index < source.Length; index++)
        {
            var value = transform(source[index]);

            if (value is null)
            {
                removals.Add(tag);
                return;
            }

            rewritten[index] = value;
        }

        dataset.AddOrUpdate(tag, rewritten);
    }
}
