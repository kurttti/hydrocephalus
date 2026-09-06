using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Готовая к выводу картинка: пиксели и физические пропорции.
/// </summary>
/// <param name="Bgra">Пиксели в порядке BGRA, строка за строкой.</param>
/// <param name="Width">Ширина в пикселях.</param>
/// <param name="Height">Высота в пикселях.</param>
/// <param name="DisplayWidthMillimetres">Физическая ширина картинки, мм.</param>
/// <param name="DisplayHeightMillimetres">Физическая высота картинки, мм.</param>
public readonly record struct ComposedPlane(
    byte[] Bgra,
    int Width,
    int Height,
    double DisplayWidthMillimetres,
    double DisplayHeightMillimetres);

/// <summary>
/// Сборка изображения среза с наложенной маской.
///
/// Отделено от разметки, чтобы проверялось тестом: съехавший на пиксель или
/// перекрасивший не ту метку оверлей на экране выглядит как неточная
/// сегментация, а не как дефект.
///
/// Физический размер картинки считается здесь же. Объём анизотропен, и вывод
/// «пиксель в пиксель» растянул бы анатомию — а по ней потом измеряют.
/// </summary>
public static class PlaneComposer
{
    /// <summary>Непрозрачность оверлея по умолчанию.</summary>
    public const double DefaultOverlayOpacity = 0.45;

    /// <summary>
    /// Цвета меток. Фон не окрашивается вовсе: маска должна показывать
    /// структуры, а не закрывать изображение.
    /// </summary>
    private static readonly (byte Blue, byte Green, byte Red)[] LabelColours =
    [
        (80, 160, 255),
        (80, 255, 160),
        (255, 160, 80),
        (255, 80, 200),
    ];

    /// <summary>
    /// Собирает картинку.
    /// </summary>
    /// <param name="image">Срез изображения.</param>
    /// <param name="overlay">Срез маски либо <see langword="null"/>.</param>
    /// <param name="opacity">Непрозрачность оверлея от 0 до 1.</param>
    /// <returns>Пиксели и пропорции.</returns>
    public static ComposedPlane Compose(
        PlaneImage image,
        byte[]? overlay = null,
        double opacity = DefaultOverlayOpacity)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(opacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(opacity, 1.0);

        if (overlay is not null && overlay.Length != image.Pixels.Length)
        {
            // Разные размеры означают, что маска не с этого среза.
            throw new ArgumentException(
                "The overlay does not match the image plane.",
                nameof(overlay));
        }

        var bgra = new byte[image.Pixels.Length * 4];

        for (var index = 0; index < image.Pixels.Length; index++)
        {
            var grey = image.Pixels[index];
            var label = overlay?[index] ?? 0;

            var (blue, green, red) = label == 0
                ? (grey, grey, grey)
                : Blend(grey, LabelColours[(label - 1) % LabelColours.Length], opacity);

            var offset = index * 4;

            bgra[offset] = blue;
            bgra[offset + 1] = green;
            bgra[offset + 2] = red;
            bgra[offset + 3] = 255;
        }

        return new ComposedPlane(
            bgra,
            image.Width,
            image.Height,
            image.Width * image.PixelWidthMillimetres,
            image.Height * image.PixelHeightMillimetres);
    }

    private static (byte Blue, byte Green, byte Red) Blend(
        byte grey,
        (byte Blue, byte Green, byte Red) colour,
        double opacity) =>
        (
            Mix(grey, colour.Blue, opacity),
            Mix(grey, colour.Green, opacity),
            Mix(grey, colour.Red, opacity));

    private static byte Mix(byte grey, byte channel, double opacity) =>
        (byte)Math.Clamp(Math.Round((grey * (1 - opacity)) + (channel * opacity)), 0, 255);
}
