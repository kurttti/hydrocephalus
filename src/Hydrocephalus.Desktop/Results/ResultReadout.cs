using System.Globalization;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Domain.Quality;
using Hydrocephalus.Domain.Reporting;

namespace Hydrocephalus.Desktop.Results;

/// <summary>Насколько строка результата требует внимания врача.</summary>
public enum ResultSeverity
{
    /// <summary>Обычное сообщение.</summary>
    Neutral = 0,

    /// <summary>Оговорка: результат есть, но с ограничением.</summary>
    Warning = 1,

    /// <summary>Препятствие: на этих данных так делать нельзя.</summary>
    Blocking = 2,
}

/// <summary>
/// Одна строка результата: то, что видно, и то, что к этому нужно знать.
/// </summary>
public sealed record ResultRow
{
    /// <summary>Основной текст строки.</summary>
    public required string Text { get; init; }

    /// <summary>Пояснение под основным текстом; может отсутствовать.</summary>
    public string? Note { get; init; }

    /// <summary>Требуемое внимание.</summary>
    public required ResultSeverity Severity { get; init; }
}

/// <summary>
/// Строка списка серий: чем серия адресуется и чем она видна врачу.
/// </summary>
/// <param name="Id">Псевдонимный идентификатор серии.</param>
/// <param name="Text">Описание серии для показа.</param>
public sealed record SeriesChoice(string Id, string Text);

/// <summary>Раздел экрана результата.</summary>
public sealed record ResultSection
{
    /// <summary>Заголовок раздела.</summary>
    public required string Title { get; init; }

    /// <summary>Строки раздела. Пустым не бывает: отсутствие данных — тоже строка.</summary>
    public required IReadOnlyList<ResultRow> Rows { get; init; }
}

/// <summary>
/// Отчёт, приведённый к тексту для экрана.
///
/// В домене и в каноническом отчёте хранятся коды и параметры; человекочитаемый
/// текст формируется здесь (ADR 0005). Числа из параметров подставляются в текст,
/// а не опускаются: «UnsupportedVoxelGeometry» ничего не говорит врачу, а «толщина
/// среза 5 мм при допустимых 1,5 мм» говорит, и по этому уже можно действовать.
///
/// Замечания приходят из двух мест и показываются в разных разделах.
/// В «Контроле качества» — замечания входного контроля (<c>InputQualityControl</c>)
/// к выбранной серии: толщина среза, размер пикселя, зазор, неквадратный пиксель,
/// поле обзора, покрытие. В «Сериях исследования» — замечания разбора срезов
/// при импорте: они оставляют серию за пределами рабочей копии, и увидеть их
/// в отчёте нельзя, потому что отчёта по такой серии не существует.
///
/// Замечания сегментации не показываются нигде: движок оставляет от неё только
/// маску и достоверность. Тексты для них здесь есть и проверены, но это заготовка,
/// а не то, что видно врачу сейчас.
///
/// Два правила, которые здесь соблюдаются намеренно.
///
/// **Число неотделимо от своей достоверности.** Достоверность стоит в той же
/// строке, что и значение, а не рядом и не ниже: строка «Объём желудочковой
/// системы: 42,3 мл» — это утверждение, которого код не делает.
///
/// **Выход за диапазон — это про измерение, а не про пациента.** Правдоподобный
/// диапазон ограничивает то, что метод способен посчитать. Значение вне него
/// означает ошибку измерения, и текст говорит именно это, иначе врач прочтёт
/// его как находку.
///
/// Экран не объясняет, почему измерений нет: отчёт этой причины не содержит,
/// а восстанавливать её здесь по условиям движка значило бы завести вторую копию
/// его правил, которая разойдётся с первой незаметно. Вместо объяснения
/// показываются свойства серии, по которым видно, чего ей не хватило.
/// </summary>
public static class ResultReadout
{
    private static readonly Dictionary<string, string> MethodNames = new(StringComparer.Ordinal)
    {
        ["volume.ventricular-system"] = "Объём желудочковой системы",
        ["evans-index"] = "Индекс Эванса",
        ["callosal-angle"] = "Каллозальный угол",
    };

    private static readonly Dictionary<string, string> StructureNames = new(StringComparer.Ordinal)
    {
        ["ventricular-system"] = "желудочковая система",
    };

    /// <summary>
    /// Описывает отчёт разделами экрана.
    /// </summary>
    /// <param name="report">Отчёт по открытому исследованию.</param>
    /// <param name="study">Состав исследования и выбранная серия.</param>
    /// <returns>Разделы: итог, измерения, контроль качества, серии.</returns>
    public static IReadOnlyList<ResultSection> Describe(AnalysisReport report, AnalysedStudy study)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(study);

        return
        [
            new ResultSection { Title = "Итог", Rows = DescribeOutcome(report.Outcome) },
            new ResultSection
            {
                Title = "Измерения",
                Rows = DescribeMeasurements(report.Biomarkers, study.Analysed),
            },
            new ResultSection
            {
                Title = "Контроль качества",
                Rows = DescribeQuality(report.Quality),
            },
            new ResultSection
            {
                Title = "Серии исследования",
                Rows = DescribeComposition(study),
            },
        ];
    }

    /// <summary>
    /// Перечисляет серии, которые можно открыть.
    /// </summary>
    /// <param name="study">Состав исследования.</param>
    /// <returns>Строки списка выбора серии.</returns>
    /// <remarks>
    /// Только серии рабочей копии. Отброшенные на импорте открыть нельзя —
    /// их данных на диске нет; они названы в разделе серий вместе с причиной,
    /// и предложить их к выбору значило бы обещать то, чего не будет.
    /// </remarks>
    public static IReadOnlyList<SeriesChoice> ChoicesFor(AnalysedStudy study)
    {
        ArgumentNullException.ThrowIfNull(study);

        return [.. study.Study.Series.Select(series => new SeriesChoice(
            series.PseudonymousSeriesId,
            DescribeSeries(series)))];
    }

    private static List<ResultRow> DescribeComposition(AnalysedStudy study)
    {
        var rows = new List<ResultRow>();

        foreach (var series in study.Study.Series)
        {
            var analysed = ReferenceEquals(series, study.Analysed)
                || string.Equals(
                    series.PseudonymousSeriesId,
                    study.Analysed.PseudonymousSeriesId,
                    StringComparison.Ordinal);

            rows.Add(new ResultRow
            {
                Text = DescribeSeries(series) + (analysed ? " — выбрана для анализа" : string.Empty),
                Severity = ResultSeverity.Neutral,
            });
        }

        // Отброшенные серии перечисляются вместе с остальными, а не прячутся:
        // «в исследовании была одна серия» и «было восемь, семь отброшено» —
        // разные положения дел, и второе значит, что смотреть надо на выгрузку,
        // а не на остаток.
        foreach (var excluded in study.Excluded)
        {
            rows.Add(new ResultRow
            {
                Text = DescribeSeries(excluded.Series) + " — не загружена",
                Note = excluded.Issues.Count == 0
                    ? "Причина в рабочей копии не сохранена."
                    : string.Join(" ", excluded.Issues.Select(Describe)),
                Severity = ResultSeverity.Blocking,
            });
        }

        return rows;
    }

    /// <summary>
    /// Описывает серию тем, что о ней известно домену.
    /// </summary>
    /// <param name="series">Серия исследования.</param>
    /// <returns>Строка для показа пользователю.</returns>
    /// <remarks>
    /// Имени у серии нет: домен хранит только псевдонимный идентификатор,
    /// а он читается как случайная строка. Различать серии врачу приходится
    /// по их свойствам, и здесь названы те, по которым это возможно.
    /// </remarks>
    public static string DescribeSeries(ImagingSeries series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var geometry = series.Geometry;
        var dimensions = geometry.Dimensions;

        var text = NameOf(series.Weighting) + ", " + NameOf(series.Tier) + ", "
            + dimensions.Columns.ToString(CultureInfo.CurrentCulture) + "×"
            + dimensions.Rows.ToString(CultureInfo.CurrentCulture) + "×"
            + dimensions.Slices.ToString(CultureInfo.CurrentCulture)
            + ", шаг срезов " + Number(geometry.SliceSpacingMillimetres) + " мм";

        // Постконтрастная серия называется постконтрастной: конвейер работает
        // только по нативным сериям, и по одной геометрии этого не увидеть.
        return series.IsContrastEnhanced ? text + ", постконтрастная" : text;
    }

    private static List<ResultRow> DescribeOutcome(AnalysisOutcome outcome) => outcome switch
    {
        AnalysisOutcome.Completed completed => DescribePrediction(completed),
        AnalysisOutcome.Refused refused => [DescribeRefusal(refused.Reason)],

        // Иерархия закрыта, третьего состояния не существует. Строка нужна
        // компилятору и на случай, если закрытость когда-нибудь снимут.
        _ => [Row("Итог анализа не распознан этой версией экрана.", ResultSeverity.Warning)],
    };

    private static List<ResultRow> DescribePrediction(AnalysisOutcome.Completed completed)
    {
        var rows = new List<ResultRow>
        {
            new()
            {
                Text = "Прогноз получен моделью " + completed.Prediction.Model.Name
                    + " " + completed.Prediction.Model.Version + ".",
                Note = "Вероятность класса — не диагноз и не заменяет заключения врача.",
                Severity = ResultSeverity.Neutral,
            },
        };

        // Показываются все классы, а не только наиболее вероятный: один класс
        // с числом читается как ответ, а весь вектор — как то, чем он является.
        foreach (var probability in completed.Prediction.Probabilities)
        {
            rows.Add(Row(
                probability.Class.Code + ": " + Percent(probability.Probability),
                ResultSeverity.Neutral));
        }

        return rows;
    }

    private static ResultRow DescribeRefusal(RefusalReason reason) => reason.Code switch
    {
        // Отказ по отсутствию пакета модели — это состояние сборки, а не поломка.
        // Дословный перевод кода («пакет несовместим или не прошёл проверку»)
        // читается как сломанная установка, и первое, что сделает врач, —
        // заявит о неисправности исправно работающей программы.
        RefusalCode.ModelPackageUnusable => new ResultRow
        {
            Text = "Классификация не выполняется.",
            Note = "В этой сборке нет проверенного пакета модели, и вероятность диагноза "
                + "не рассчитывается намеренно (ADR 0004). Измерения от этого не зависят.",
            Severity = ResultSeverity.Neutral,
        },

        RefusalCode.QualityControlFailed => new ResultRow
        {
            Text = "Анализ не выполнен: входной контроль качества не пройден.",
            Note = "Препятствия перечислены ниже.",
            Severity = ResultSeverity.Blocking,
        },

        RefusalCode.InsufficientAcquisitionTier => new ResultRow
        {
            Text = "Анализ не выполнен: подходящей серии в исследовании нет.",
            Note = "Нужна нативная серия, геометрии которой хватает хотя бы на линейные измерения.",
            Severity = ResultSeverity.Blocking,
        },

        RefusalCode.OutOfDistribution => new ResultRow
        {
            Text = "Классификация не выполнена: исследование вне области применимости модели.",
            Note = "Модель валидирована на другом материале, и её ответ здесь не обоснован.",
            Severity = ResultSeverity.Blocking,
        },

        RefusalCode.CancelledByUser => Row("Анализ прерван.", ResultSeverity.Neutral),

        RefusalCode.Unspecified => Row(
            "Анализ не выполнен; причина в отчёте не названа.",
            ResultSeverity.Warning),

        // Код, добавленный в перечисление и забытый здесь, не должен
        // притвориться одним из известных.
        _ => Row(
            "Анализ не выполнен; причина отказа не распознана этой версией экрана.",
            ResultSeverity.Warning),
    };

    private static IReadOnlyList<ResultRow> DescribeMeasurements(
        IReadOnlyList<Biomarker> biomarkers,
        ImagingSeries series)
    {
        if (biomarkers.Count == 0)
        {
            // Пустой список — это «не измерено», а не «ноль». Причина в отчёте
            // не записана, поэтому вместо неё называются свойства серии:
            // по ним видно, чего не хватило, и утверждения о правилах движка
            // экран при этом не делает.
            return
            [
                new ResultRow
                {
                    Text = "Измерения не выполнены.",

                    // Свойства серии названы свойствами, а не причиной. Иначе
                    // при отказе по контролю качества исправная серия читалась бы
                    // как «здесь всё в порядке», хотя препятствие названо выше.
                    Note = "Причина в отчёте не записана. Свойства серии: уровень получения — "
                        + NameOf(series.Tier) + ", взвешенность — " + NameOf(series.Weighting) + ".",
                    Severity = ResultSeverity.Warning,
                },
            ];
        }

        return [.. biomarkers.Select(Describe)];
    }

    private static ResultRow Describe(Biomarker biomarker)
    {
        var name = MethodNames.TryGetValue(biomarker.Method.Code, out var known)
            ? known

            // Код метода машинно-читаем и однозначен: показать его честнее,
            // чем придумать название признаку, которого экран не знает.
            : biomarker.Method.Code;

        var note = "Метод: " + biomarker.Method.Code
            + ", версия определения " + biomarker.Method.DefinitionVersion + ".";

        if (biomarker.IsOutOfRange)
        {
            // Диапазон ограничивает не пациента, а метод: он говорит, какие
            // значения метод вообще способен получить осмысленно.
            note += " Значение вне диапазона, в котором метод даёт осмысленный результат ("
                + Number(biomarker.AllowedRange.Minimum) + "–" + Number(biomarker.AllowedRange.Maximum)
                + "): это указывает на ошибку измерения, а не на находку у пациента.";
        }

        return new ResultRow
        {
            // Достоверность стоит в той же строке, что и число: скопированное
            // или прочитанное вслух значение обязано нести её с собой.
            Text = name + ": " + Number(biomarker.Value) + UnitOf(biomarker.Unit)
                + " (" + NameOf(biomarker.Quality) + ")",
            Note = note,
            Severity = SeverityOf(biomarker),
        };
    }

    private static ResultSeverity SeverityOf(Biomarker biomarker) =>
        biomarker.IsOutOfRange || biomarker.Quality == MeasurementQuality.Unreliable
            ? ResultSeverity.Blocking
            : biomarker.Quality == MeasurementQuality.Reliable
                ? ResultSeverity.Neutral
                : ResultSeverity.Warning;

    private static IReadOnlyList<ResultRow> DescribeQuality(QualityAssessment quality)
    {
        if (quality.Issues.Count == 0)
        {
            return [Row("Замечаний к качеству нет.", ResultSeverity.Neutral)];
        }

        // Показывается один список. RefusalReason.ContributingIssues — это
        // подмножество блокирующих замечаний отсюда же, и вторым разделом
        // те же находки выглядели бы как новые.
        return [.. quality.Issues.Select(issue => new ResultRow
        {
            Text = Describe(issue),
            Severity = issue.Severity == QualityIssueSeverity.Blocking
                ? ResultSeverity.Blocking
                : ResultSeverity.Warning,
        })];
    }

    private static string Describe(QualityIssue issue)
    {
        var parameters = issue.Parameters;

        // Разбор идёт по коду вместе с уточнением из параметров: один код
        // покрывает несколько разных находок, и без уточнения текст был бы
        // либо неверным, либо бессодержательным.
        var reason = Value(parameters, "reason");
        var parameter = Value(parameters, "parameter");

        return issue.Code switch
        {
            QualityIssueCode.InconsistentGeometry => DescribeGeometry(reason, parameters),
            QualityIssueCode.UnsupportedVoxelGeometry => DescribeVoxelGeometry(parameter, parameters),
            QualityIssueCode.HeadTruncated => DescribeTruncation(reason, parameter, parameters),

            QualityIssueCode.AcquisitionTierTooLow =>
                "Геометрии серии не хватает даже на линейные измерения.",

            QualityIssueCode.MotionArtefact =>
                "Выраженные двигательные артефакты.",

            QualityIssueCode.ContrastEnhancedSeries =>
                "Серия постконтрастная: конвейер работает только по нативным МР-сериям.",

            QualityIssueCode.BurnedInAnnotation =>
                "В изображение вписаны подписи: их не удаляет деидентификация тегов.",

            QualityIssueCode.ImplausibleMetadata =>
                "Недостоверное значение тега " + Value(parameters, "tag")
                + ": " + Value(parameters, "value") + ".",

            QualityIssueCode.OutOfDistribution => string.Equals(
                reason,
                "unvalidatedBaselineSegmentation",
                StringComparison.Ordinal)
                ? "Маска получена неаттестованным baseline-методом (" + Value(parameters, "method")
                    + "), а не проверенной моделью."
                : "Исследование вне области применимости модели.",

            _ => "Замечание с кодом " + issue.Code + ".",
        };
    }

    private static string DescribeGeometry(
        string reason,
        IReadOnlyDictionary<string, string> parameters) => reason switch
        {
            "multiFrameInstances" =>
                "Срезы лежат внутри многокадровых файлов (" + Value(parameters, "multiFrame")
                + " из " + Value(parameters, "instances")
                + "): их геометрия хранится иначе, и объём по такой серии не собирается.",

            "missingSlicePositions" =>
                "У части срезов нет положения в пространстве (с положением "
                + Value(parameters, "positioned") + " из " + Value(parameters, "instances")
                + "): порядок и шаг срезов восстановить нечем.",

            "mixedOrientations" =>
                "Срезы сняты в разных плоскостях (" + Value(parameters, "distinctOrientations")
                + " ориентации на " + Value(parameters, "instances")
                + " файлов): это обзорная серия, а не один объём.",

            "duplicateSlicePositions" =>
                "Положения срезов повторяются (" + Value(parameters, "distinctOffsets")
                + " различных положений на " + Value(parameters, "instances") + " файлов): "
                + DescribeSplit(Value(parameters, "stackSplit"), parameters),

            "irregularSliceSpacing" =>
                "Шаг срезов непостоянен: " + Number(Value(parameters, "spacingMm"))
                + " мм при отклонении до " + Number(Value(parameters, "maxDeviationMm"))
                + " мм — похоже на пропущенный срез, и объём по такой серии занижен.",

            "thresholdDidNotIsolateCsf" =>
                "Порог не выделил ликвор: под маску попала доля объёма головы "
                + Number(Value(parameters, "selectedFraction")) + ".",

            "peripheralCsfDiscarded" =>
                "Отброшено скоплений ликвора вне желудочков: " + Value(parameters, "components") + ".",

            "requiredStructureMissing" =>
                "Сегментация не нашла структуру: " + NameOf(Value(parameters, "structure")) + ".",

            "fragmentedStructure" =>
                "Структура «" + NameOf(Value(parameters, "structure")) + "» распалась на "
                + Value(parameters, "components") + " несвязных частей.",

            _ => "Геометрия DICOM противоречива или неполна.",
        };

    private static string DescribeSplit(
        string split,
        IReadOnlyDictionary<string, string> parameters) => split switch
        {
            "byEcho" => "серия распадается на равные наборы по эху ("
                + Value(parameters, "distinctEchoes") + ").",

            "byAcquisition" => "серия распадается на равные наборы по номеру получения ("
                + Value(parameters, "distinctAcquisitions") + ").",

            _ => "разделить её на отдельные наборы по метаданным нечем.",
        };

    private static string DescribeVoxelGeometry(
        string parameter,
        IReadOnlyDictionary<string, string> parameters) => parameter switch
        {
            "sliceThickness" =>
                "Толщина среза " + Number(Value(parameters, "valueMm"))
                + " мм вне поддерживаемого диапазона (до " + Number(Value(parameters, "maxMm")) + " мм).",

            "pixelSpacing" =>
                "Размер пикселя " + Number(Value(parameters, "valueMm"))
                + " мм вне поддерживаемого диапазона (до " + Number(Value(parameters, "maxMm")) + " мм).",

            "sliceGap" =>
                "Между срезами есть зазор: шаг " + Number(Value(parameters, "spacingMm"))
                + " мм при толщине " + Number(Value(parameters, "thicknessMm"))
                + " мм — объём занижен на долю неполученной ткани.",

            "inPlaneAnisotropy" =>
                "Пиксель сильно неквадратный: отношение сторон " + Number(Value(parameters, "value"))
                + " при допустимом " + Number(Value(parameters, "max")) + ".",

            _ => "Шаг вокселя вне диапазона, поддерживаемого конвейером.",
        };

    private static string DescribeTruncation(
        string reason,
        string parameter,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (string.Equals(reason, "structureTouchesVolumeBoundary", StringComparison.Ordinal))
        {
            return "Структура «" + NameOf(Value(parameters, "structure"))
                + "» упирается в край объёма: её размер занижен на неизвестную величину.";
        }

        return parameter switch
        {
            "fieldOfView" =>
                "Поле обзора " + Number(Value(parameters, "widthMm")) + "×"
                + Number(Value(parameters, "heightMm")) + " мм меньше минимального "
                + Number(Value(parameters, "minMm")) + " мм: голова в него не помещается.",

            "sliceCoverage" =>
                "Покрытие по оси срезов " + Number(Value(parameters, "valueMm"))
                + " мм меньше " + Number(Value(parameters, "minMm"))
                + " мм: голова захвачена не целиком.",

            _ => "Голова обрезана полем обзора.",
        };
    }

    private static ResultRow Row(string text, ResultSeverity severity) =>
        new() { Text = text, Severity = severity };

    private static string Value(IReadOnlyDictionary<string, string> parameters, string key) =>
        parameters.TryGetValue(key, out var value) ? value : "?";

    /// <summary>
    /// Приводит число из параметров к виду, принятому в интерфейсе.
    ///
    /// Параметры записаны в инвариантной культуре, чтобы отчёт читался одинаково
    /// везде; на экране число обязано выглядеть так же, как остальные числа.
    /// Нечисловое значение возвращается как есть — подставлять вместо него
    /// прочерк значило бы скрыть, что пришло что-то неожиданное.
    /// </summary>
    private static string Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Number(parsed)
            : value;

    private static string Number(double value) =>
        value.ToString("0.###", CultureInfo.CurrentCulture);

    private static string Percent(double value) =>
        value.ToString("P0", CultureInfo.CurrentCulture);

    private static string UnitOf(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Millilitre => " мл",
        MeasurementUnit.Millimetre => " мм",
        MeasurementUnit.Degree => "°",

        // Отношение безразмерно, и приписывать ему единицу нечего.
        MeasurementUnit.Ratio => string.Empty,

        _ => " (единица не задана)",
    };

    private static string NameOf(MeasurementQuality quality) => quality switch
    {
        MeasurementQuality.Reliable => "надёжно",
        MeasurementQuality.Questionable => "сомнительно",
        MeasurementQuality.Unreliable => "непригодно для интерпретации",
        _ => "достоверность не оценена",
    };

    private static string NameOf(AcquisitionTier tier) => tier switch
    {
        AcquisitionTier.Extended => "расширенный (3D)",
        AcquisitionTier.Baseline => "базовый (2D)",
        AcquisitionTier.Unusable => "непригодный",
        _ => "не распознан",
    };

    private static string NameOf(SeriesWeighting weighting) => weighting switch
    {
        SeriesWeighting.T1 => "T1",
        SeriesWeighting.T2 => "T2",
        SeriesWeighting.Flair => "FLAIR",
        _ => "не распознана",
    };

    private static string NameOf(string structureCode) =>
        StructureNames.TryGetValue(structureCode, out var known) ? known : structureCode;
}
