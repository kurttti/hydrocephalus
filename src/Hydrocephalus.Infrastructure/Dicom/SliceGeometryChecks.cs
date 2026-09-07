using System.Globalization;
using Hydrocephalus.Domain.Quality;

namespace Hydrocephalus.Infrastructure.Dicom;

/// <summary>
/// Проверки взаимного расположения срезов серии.
///
/// Зазор между срезами здесь замечанием не считается: рутинные 2D-серии выборки
/// идут с зазором штатно, и отмечать это на каждой серии — шум. Зазор влияет на
/// пригодность через фактический шаг выборки в доменной модели. Замечания даются
/// на то, что делает объём непостроимым.
///
/// Проверки выстроены по порядку, а не рядом: каждая следующая опирается на то,
/// что предыдущая не сработала. Серия из нескольких плоскостей не имеет одной
/// нормали, и «совпадающие положения» на ней — артефакт проекции, а не свойство
/// данных. Серия с совпадающими положениями не имеет одного шага: нули в
/// промежутках тянут медиану вниз, и «неравномерный шаг» после этого измеряет
/// испорченную величину. Выдавать оба замечания на одну серию значило бы считать
/// одну и ту же поломку дважды и лечить следствие вместо причины.
/// </summary>
internal static class SliceGeometryChecks
{
    /// <summary>
    /// Допустимое отклонение отдельного промежутка от медианного шага, доля.
    /// Небольшой разброс объясняется округлением координат в тегах.
    /// </summary>
    internal const double MaxSpacingDeviationFraction = 0.10;

    /// <summary>
    /// Проверяет расположение срезов.
    /// </summary>
    /// <param name="positioning">Результат разбора положений.</param>
    /// <returns>Найденные замечания, возможно пустой список.</returns>
    internal static IEnumerable<QualityIssue> Inspect(SlicePositioning positioning)
    {
        if (positioning.MultiFrameInstances > 0)
        {
            // Многокадровый экземпляр — это целый набор срезов в одном файле,
            // и его геометрия лежит в функциональных группах, а не в тегах
            // верхнего уровня. Модель «файл — срез» на нём неверна целиком.
            yield return Issue(
                "multiFrameInstances",
                ("instances", Count(positioning.Instances)),
                ("multiFrame", Count(positioning.MultiFrameInstances)));

            yield break;
        }

        if (positioning.PositionedInstances < positioning.Instances)
        {
            // Отсутствующий ImagePositionPatient читается как нулевое положение,
            // то есть серия из таких экземпляров выглядит как набор совпадающих
            // срезов. Это другая поломка, и называть её надо своим именем.
            yield return Issue(
                "missingSlicePositions",
                ("instances", Count(positioning.Instances)),
                ("positioned", Count(positioning.PositionedInstances)));

            yield break;
        }

        if (positioning.DistinctOrientations > 1)
        {
            // Обзорная серия из нескольких проекций под одним SeriesInstanceUID.
            // Одной нормали у неё нет, объём не строится, а проверка совпадающих
            // положений на ней бессмысленна.
            yield return Issue(
                "mixedOrientations",
                ("instances", Count(positioning.Instances)),
                ("distinctOrientations", Count(positioning.DistinctOrientations)));

            yield break;
        }

        if (positioning.HasDuplicatePositions)
        {
            // Совпадающие положения означают, что под одним SeriesInstanceUID
            // лежит больше одного набора срезов — например, разные эхо.
            // Какой из них строить, метаданные не говорят. Параметры отвечают
            // на следующий вопрос: распадается ли серия на равные наборы по
            // известной оси, то есть можно ли её разделить однозначно.
            yield return Issue(
                "duplicateSlicePositions",
                ("instances", Count(positioning.Instances)),
                ("distinctOffsets", Count(positioning.DistinctOffsets)),
                ("distinctEchoes", Count(positioning.DistinctEchoes)),
                ("distinctAcquisitions", Count(positioning.DistinctAcquisitions)),
                ("stackSplit", SplitAxis(positioning)));

            yield break;
        }

        if (positioning.SpacingMillimetres <= 0)
        {
            yield break;
        }

        var tolerance = positioning.SpacingMillimetres * MaxSpacingDeviationFraction;

        if (positioning.MaxDeviationMillimetres > tolerance)
        {
            // Пропущенный срез посреди серии не виден ни по числу файлов, ни по
            // толщине: он проявляется только как промежуток, выпадающий из шага.
            // Объём, посчитанный по такой серии, занижен молча.
            yield return Issue(
                "irregularSliceSpacing",
                ("spacingMm", Format(positioning.SpacingMillimetres)),
                ("maxDeviationMm", Format(positioning.MaxDeviationMillimetres)));
        }
    }

    /// <summary>
    /// Определяет ось, по которой серия распадается на равные наборы срезов.
    ///
    /// Условие строгое: число экземпляров обязано быть равно числу различных
    /// положений, умноженному на число значений оси. Иначе набор неполный или
    /// смешанный, и разделение по этой оси было бы догадкой, а не разбором.
    /// </summary>
    /// <param name="positioning">Результат разбора положений.</param>
    /// <returns>Название оси либо признак того, что объяснения нет.</returns>
    private static string SplitAxis(SlicePositioning positioning)
    {
        if (Explains(positioning, positioning.DistinctEchoes))
        {
            return "byEcho";
        }

        return Explains(positioning, positioning.DistinctAcquisitions)
            ? "byAcquisition"
            : "unexplained";
    }

    private static bool Explains(SlicePositioning positioning, int distinctValues) =>
        distinctValues > 1
        && positioning.DistinctOffsets * distinctValues == positioning.PositionedInstances;

    private static QualityIssue Issue(string reason, params (string Key, string Value)[] parameters)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason };

        foreach (var (key, value) in parameters)
        {
            values[key] = value;
        }

        return new QualityIssue
        {
            Code = QualityIssueCode.InconsistentGeometry,
            Severity = QualityIssueSeverity.Blocking,
            Parameters = values,
        };
    }

    private static string Count(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
