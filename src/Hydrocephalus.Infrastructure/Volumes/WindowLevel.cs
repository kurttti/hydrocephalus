using System.Globalization;

namespace Hydrocephalus.Infrastructure.Volumes;

/// <summary>
/// Окно и уровень: отображение значений модальности в яркость экрана.
///
/// Формула — линейный VOI LUT из DICOM PS3.3, а не «растянуть от минимума
/// к максимуму»: врач сопоставляет увиденное с тем, что показывает диагностическая
/// станция, и произвольная нормализация делает такое сравнение неверным.
/// </summary>
/// <param name="Center">Центр окна в единицах модальности.</param>
/// <param name="Width">Ширина окна в единицах модальности.</param>
public readonly record struct WindowLevel(double Center, double Width)
{
    /// <summary>Наименьшая ширина окна: при нулевой ширине изображение вырождается.</summary>
    public const double MinWidth = 1.0;

    /// <summary>Признак того, что окно пригодно для отображения.</summary>
    public bool IsUsable => Width >= MinWidth && double.IsFinite(Center) && double.IsFinite(Width);

    /// <summary>
    /// Переводит значение модальности в яркость 0–255.
    /// </summary>
    /// <param name="value">Значение в единицах модальности.</param>
    /// <returns>Яркость от 0 до 255.</returns>
    public byte Map(double value)
    {
        var width = Math.Max(Width, MinWidth);
        var lower = Center - 0.5 - ((width - 1) / 2);
        var upper = Center - 0.5 + ((width - 1) / 2);

        if (value <= lower)
        {
            return 0;
        }

        if (value > upper)
        {
            return 255;
        }

        var scaled = (((value - (Center - 0.5)) / (width - 1)) + 0.5) * 255;

        return (byte)Math.Clamp(Math.Round(scaled, MidpointRounding.AwayFromZero), 0, 255);
    }

    /// <summary>Возвращает строковое представление окна.</summary>
    /// <returns>Центр и ширина.</returns>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"C {Center:0.###} / W {Width:0.###}");
}
