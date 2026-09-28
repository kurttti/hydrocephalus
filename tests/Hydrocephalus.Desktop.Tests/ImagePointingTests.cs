using Hydrocephalus.Desktop.Viewing;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Перевод нажатия мышью в пиксел изображения.
///
/// Единственный шаг пути от нажатия до измерения, который иначе проверялся бы
/// только рукой. Ошибка здесь не падает и не отказывает: точка садится рядом с
/// указанной, число выходит правдоподобным, и заметить это на экране нельзя.
/// </summary>
public sealed class ImagePointingTests
{
    [Fact]
    public void The_centre_of_the_element_is_the_centre_of_the_image()
    {
        var pixel = ImagePointing.PixelAt(
            x: 100, y: 100, elementWidth: 200, elementHeight: 200, pixelWidth: 10, pixelHeight: 10);

        Assert.Equal((5, 5), pixel);
    }

    [Fact]
    public void The_letterbox_beside_a_wide_image_names_no_pixel()
    {
        // Изображение 20 на 10 в квадрате 200 на 200: масштаб 10, показанная
        // высота 100, сверху и снизу по 50 пустого места. Нажатие в поле — не
        // промах по пикселу, а промах по изображению, и назвать его пикселом
        // значило бы поставить точку на край анатомии.
        Assert.Null(ImagePointing.PixelAt(100, 10, 200, 200, 20, 10));
        Assert.Null(ImagePointing.PixelAt(100, 190, 200, 200, 20, 10));

        Assert.Equal((10, 5), ImagePointing.PixelAt(100, 100, 200, 200, 20, 10));
    }

    [Fact]
    public void The_letterbox_beside_a_tall_image_names_no_pixel()
    {
        // То же в другую сторону: 10 на 20, поля слева и справа.
        Assert.Null(ImagePointing.PixelAt(10, 100, 200, 200, 10, 20));
        Assert.Null(ImagePointing.PixelAt(190, 100, 200, 200, 10, 20));

        Assert.Equal((5, 10), ImagePointing.PixelAt(100, 100, 200, 200, 10, 20));
    }

    [Fact]
    public void The_corners_of_the_shown_image_are_its_corner_pixels()
    {
        // Границы проверяются отдельно: смещение на пиксел у края — обычная
        // цена ошибки в округлении, а диаметр черепа меряется именно у края.
        const int Width = 8;
        const int Height = 5;
        const double Element = 80;

        // Масштаб 10, показано 80 на 50, поля по 15 сверху и снизу.
        Assert.Equal((0, 0), ImagePointing.PixelAt(0.5, 15.5, Element, Element, Width, Height));
        Assert.Equal((7, 4), ImagePointing.PixelAt(79.5, 64.5, Element, Element, Width, Height));

        Assert.Null(ImagePointing.PixelAt(-0.5, 15.5, Element, Element, Width, Height));
        Assert.Null(ImagePointing.PixelAt(80.5, 64.5, Element, Element, Width, Height));
    }

    [Fact]
    public void Every_pixel_of_a_shown_image_can_be_reached()
    {
        // Если какой-то пиксел недостижим или достаётся дважды, точку рядом с ним
        // поставить нельзя, а это как раз конец отрезка.
        const int Width = 7;
        const int Height = 4;
        const double Element = 70;

        var scale = Math.Min(Element / Width, Element / Height);
        var offsetY = (Element - (Height * scale)) / 2;
        var reached = new HashSet<(int, int)>();

        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                // Середина пиксела.
                var x = (column + 0.5) * scale;
                var y = offsetY + ((row + 0.5) * scale);

                var pixel = ImagePointing.PixelAt(x, y, Element, Element, Width, Height);

                Assert.Equal((column, row), pixel);
                Assert.True(reached.Add((column, row)));
            }
        }

        Assert.Equal(Width * Height, reached.Count);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    public void An_element_without_size_names_no_pixel(double width, double height)
    {
        // Вид может быть измерен до первой отрисовки: нулевой размер здесь не
        // ошибка вызова, а обычное состояние, и делить на него нельзя.
        Assert.Null(ImagePointing.PixelAt(1, 1, width, height, 10, 10));
    }

    [Fact]
    public void An_image_without_pixels_names_no_pixel()
    {
        Assert.Null(ImagePointing.PixelAt(1, 1, 100, 100, 0, 10));
        Assert.Null(ImagePointing.PixelAt(1, 1, 100, 100, 10, 0));
    }
}
