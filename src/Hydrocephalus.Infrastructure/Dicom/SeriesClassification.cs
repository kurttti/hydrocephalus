using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Распознавание характера серии по описанию, введённому оператором сканера.
///
/// Это эвристика над свободным текстом производителя, а не надёжный признак.
/// У неё заведомо есть ложноотрицательные срабатывания: описание может быть пустым,
/// на другом языке или содержать нестандартное сокращение. Поэтому правило собрано
/// в одном месте — чтобы его можно было проверить, расширить и обсудить, а не искать
/// разбросанные по коду проверки подстрок.
/// </summary>
internal static class SeriesClassification
{
    private static readonly string[] ContrastMarkers =
    [
        "+c",
        "+ c",
        "post c",
        "postc",
        "post-c",
        "ce t1",
        "t1 ce",
        "c+",
        "gd",
        "gado",
        "contrast",
        "контраст",
        "с контрастом",
    ];

    private static readonly string[] NonContrastMarkers =
    [
        "non contrast",
        "non-contrast",
        "pre contrast",
        "pre-contrast",
        "без контраста",
    ];

    /// <summary>
    /// Названия последовательностей, однозначно задающие взвешенность.
    /// Сюда попадают только те, где сомнений нет: MPRAGE, BRAVO и SPGR — это
    /// всегда T1. Намеренно не включены SPACE, CUBE, VISTA, TSE и FSE: под этими
    /// именами выпускаются и T1, и T2, и FLAIR, и угадывание по ним внесло бы
    /// ошибки там, где сейчас честное «не знаю».
    /// </summary>
    private static readonly (string Marker, SeriesWeighting Weighting)[] SequenceMarkers =
    [
        ("mprage", SeriesWeighting.T1),
        ("mp-rage", SeriesWeighting.T1),
        ("mp rage", SeriesWeighting.T1),
        ("bravo", SeriesWeighting.T1),
        ("spgr", SeriesWeighting.T1),
        ("haste", SeriesWeighting.T2),
    ];

    /// <summary>
    /// Кириллические буквы, неотличимые на вид от латинских. Описания серий
    /// набирают руками, и раскладка в них смешивается: «Т1» с кириллической «Т»
    /// выглядит точно так же, как «T1», но это другая строка. Без приведения
    /// такая серия уходит в <see cref="SeriesWeighting.Unknown"/> — при том, что
    /// маркеры контраста русский текст уже понимают, и правила расходятся.
    /// </summary>
    private static readonly Dictionary<char, char> LookalikeLetters = new()
    {
        ['а'] = 'a',
        ['в'] = 'b',
        ['е'] = 'e',
        ['к'] = 'k',
        ['м'] = 'm',
        ['н'] = 'h',
        ['о'] = 'o',
        ['р'] = 'p',
        ['с'] = 'c',
        ['т'] = 't',
        ['у'] = 'y',
        ['х'] = 'x',
    };

    /// <summary>
    /// Приводит описание к виду, в котором кириллица и латиница неразличимы.
    /// Применяется и к тексту, и к самим маркерам — иначе русские маркеры
    /// вроде «без контраста» перестали бы совпадать с приведённым текстом.
    /// </summary>
    private static string Normalize(string text)
    {
        var lower = text.ToLowerInvariant();

        return string.Create(lower.Length, lower, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                span[index] = LookalikeLetters.TryGetValue(source[index], out var latin)
                    ? latin
                    : source[index];
            }
        });
    }

    /// <summary>
    /// Определяет, является ли серия постконтрастной по её описанию.
    /// Такие серии не подаются в MRI-only конвейер ни на одном уровне входа.
    /// </summary>
    /// <param name="seriesDescription">Описание серии, возможно пустое.</param>
    /// <returns><see langword="true"/>, если описание указывает на введение контраста.</returns>
    internal static bool LooksContrastEnhanced(string? seriesDescription)
    {
        if (string.IsNullOrWhiteSpace(seriesDescription))
        {
            return false;
        }

        var text = Normalize(seriesDescription);

        // Явное указание на отсутствие контраста имеет приоритет: описания вида
        // "T1 non-contrast" содержат подстроки из обоих списков.
        if (NonContrastMarkers.Any(marker => text.Contains(Normalize(marker), StringComparison.Ordinal)))
        {
            return false;
        }

        return ContrastMarkers.Any(marker => text.Contains(Normalize(marker), StringComparison.Ordinal));
    }

    /// <summary>
    /// Параметры импульсной последовательности, по которым взвешенность видна
    /// независимо от того, что оператор набрал в описании.
    /// </summary>
    /// <param name="ScanningSequence">(0018,0020), например SE, GR, EP.</param>
    /// <param name="SequenceVariant">(0018,0021), например MP, SK, SP, TOF.</param>
    /// <param name="AcquisitionType">(0018,0023): 2D или 3D.</param>
    /// <param name="RepetitionTimeMilliseconds">(0018,0080), 0 если тега нет.</param>
    /// <param name="EchoTimeMilliseconds">(0018,0081), 0 если тега нет.</param>
    /// <param name="InversionTimeMilliseconds">(0018,0082), 0 если тега нет.</param>
    internal readonly record struct AcquisitionParameters(
        string? ScanningSequence,
        string? SequenceVariant,
        string? AcquisitionType,
        double RepetitionTimeMilliseconds,
        double EchoTimeMilliseconds,
        double InversionTimeMilliseconds);

    /// <summary>
    /// Определяет взвешенность по параметрам последовательности, когда описание
    /// молчит. Правила выведены из разбора выборки, а не из общих соображений:
    /// в ней 611 серий из 1302 не опознавались по тексту.
    ///
    /// Диффузия распознаётся первой и намеренно остаётся нераспознанной по
    /// взвешенности. По физике EPI-диффузия T2-взвешена, и правило «длинные TR и
    /// TE — это T2» записало бы в T2 сразу 172 серии выборки. Анатомическим
    /// снимком они при этом не являются, и в конвейер их подавать нельзя.
    /// </summary>
    private static SeriesWeighting FromAcquisition(in AcquisitionParameters parameters)
    {
        var sequence = Normalize(parameters.ScanningSequence ?? string.Empty);
        var variant = Normalize(parameters.SequenceVariant ?? string.Empty);
        var threeDimensional = Normalize(parameters.AcquisitionType ?? string.Empty).Contains('3');
        double tr = parameters.RepetitionTimeMilliseconds,
            te = parameters.EchoTimeMilliseconds,
            ti = parameters.InversionTimeMilliseconds;

        // Эхо-планарные последовательности — диффузия и перфузия, не анатомия.
        // Ангиография по времени пролёта — тоже не взвешенный снимок.
        if (sequence.Contains("ep", StringComparison.Ordinal)
            || variant.Contains("tof", StringComparison.Ordinal))
        {
            return SeriesWeighting.Unknown;
        }

        if (tr <= 0 || te <= 0)
        {
            return SeriesWeighting.Unknown;
        }

        // Инверсия-восстановление с длинным эхом — FLAIR.
        if (ti > 0 && te >= 80 && tr >= 2000)
        {
            return SeriesWeighting.Flair;
        }

        // Подготовленное намагничивание в объёме — MPRAGE и его родня: это T1,
        // как бы серия ни называлась. Самая ценная группа: объёмные T1.
        if (variant.Contains("mp", StringComparison.Ordinal) && threeDimensional && te < 30)
        {
            return SeriesWeighting.T1;
        }

        if (tr < 800 && te < 30)
        {
            return SeriesWeighting.T1;
        }

        return tr >= 2000 && te >= 80 ? SeriesWeighting.T2 : SeriesWeighting.Unknown;
    }

    /// <summary>
    /// Определяет взвешенность серии по описанию.
    /// Та же оговорка: эвристика над свободным текстом.
    /// </summary>
    /// <param name="seriesDescription">Описание серии, возможно пустое.</param>
    /// <param name="parameters">Параметры последовательности, если они прочитаны.</param>
    /// <returns>Распознанная взвешенность либо <see cref="SeriesWeighting.Unknown"/>.</returns>
    internal static SeriesWeighting DetectWeighting(
        string? seriesDescription,
        in AcquisitionParameters parameters = default)
    {
        var fromText = DetectWeightingFromText(seriesDescription);

        // Текст важнее: его набирал человек, знавший, что снимал. Параметры
        // подключаются только там, где текста не хватило.
        return fromText != SeriesWeighting.Unknown ? fromText : FromAcquisition(parameters);
    }

    private static SeriesWeighting DetectWeightingFromText(string? seriesDescription)
    {
        if (string.IsNullOrWhiteSpace(seriesDescription))
        {
            return SeriesWeighting.Unknown;
        }

        var text = Normalize(seriesDescription);

        // FLAIR проверяется первым: описания вида "T2 FLAIR" содержат и "t2".
        if (text.Contains("flair", StringComparison.Ordinal)
            || text.Contains(Normalize("флаир"), StringComparison.Ordinal))
        {
            return SeriesWeighting.Flair;
        }

        if (text.Contains("t1", StringComparison.Ordinal))
        {
            return SeriesWeighting.T1;
        }

        if (text.Contains("t2", StringComparison.Ordinal))
        {
            return SeriesWeighting.T2;
        }

        // Название последовательности — запасной признак: описание может не
        // содержать «T1» вовсе, а называться только «MPRAGE» или «BRAVO».
        foreach (var (marker, weighting) in SequenceMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return weighting;
            }
        }

        return SeriesWeighting.Unknown;
    }
}
