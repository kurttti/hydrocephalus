using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Буквы сторон для подписи видов.
///
/// Используются международные обозначения L/R/A/P/S/I, а не русские сокращения,
/// хотя весь остальной интерфейс русский. Причина в безопасности, а не в удобстве:
/// эти буквы стоят на каждой диагностической станции, врач читает их не задумываясь,
/// а придуманная кириллическая схема потребовала бы вспоминать её у постели больного.
///
/// Кириллические сокращения к тому же двусмысленны: «П» одинаково начинает
/// «правая» и «передняя», а «Р» кириллическое неотличимо на вид от латинского «P»
/// с противоположным значением. Путаница сторон клинически значима (ADR 0007).
/// </summary>
public static class OrientationLetters
{
    /// <summary>Подпись для неизвестного направления.</summary>
    public const string Unknown = "?";

    /// <summary>
    /// Возвращает букву стороны.
    /// </summary>
    /// <param name="direction">Анатомическое направление.</param>
    /// <returns>Буква либо знак вопроса.</returns>
    public static string Of(AnatomicalDirection direction) => direction switch
    {
        AnatomicalDirection.Left => "L",
        AnatomicalDirection.Right => "R",
        AnatomicalDirection.Anterior => "A",
        AnatomicalDirection.Posterior => "P",
        AnatomicalDirection.Superior => "S",
        AnatomicalDirection.Inferior => "I",

        // Неизвестное направление показывается знаком вопроса, а не пустотой:
        // отсутствие подписи читается как «сторона очевидна», и это опаснее.
        _ => Unknown,
    };

    /// <summary>
    /// Возвращает название плоскости для заголовка вида.
    /// </summary>
    /// <param name="plane">Плоскость.</param>
    /// <param name="approximate">Признак заметно наклонённой плоскости.</param>
    /// <returns>Название плоскости.</returns>
    public static string NameOf(ImagingPlane plane, bool approximate)
    {
        var name = plane switch
        {
            ImagingPlane.Axial => "Аксиальная",
            ImagingPlane.Coronal => "Корональная",
            ImagingPlane.Sagittal => "Сагиттальная",
            _ => "Плоскость не определена",
        };

        // Наклон называется прямо: подписи наклонённой плоскости приблизительны,
        // и скрывать это за уверенным названием нельзя.
        return approximate ? name + " (наклон)" : name;
    }
}
