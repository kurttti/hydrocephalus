namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Перевод точки, указанной мышью, в пиксел показанного среза.
///
/// Изображение выводится с растяжением «по содержимому»: оно вписывается в
/// отведённый прямоугольник целиком, с сохранением отношения сторон, и потому
/// обычно не заполняет его — по двум сторонам остаются поля. Считать пиксел
/// делением на размер элемента значило бы ошибиться ровно на эти поля, причём
/// тем сильнее, чем дальше от центра.
///
/// Поправка на неквадратный пиксел здесь не нужна и не делается: она задана
/// преобразованием разметки самого элемента, а координаты мыши приходят в
/// системе элемента, то есть уже с учётом этого преобразования.
///
/// Вынесено отдельной чистой функцией намеренно: это единственный шаг пути от
/// нажатия до измерения, который нельзя проверить, не нажимая мышью, — а именно
/// на нём ошибка выглядит как точка, поставленная врачом чуть не туда.
/// </summary>
public static class ImagePointing
{
    /// <summary>
    /// Находит пиксел изображения под указанной точкой.
    /// </summary>
    /// <param name="x">Координата точки по ширине элемента.</param>
    /// <param name="y">Координата точки по высоте элемента.</param>
    /// <param name="elementWidth">Ширина элемента вывода.</param>
    /// <param name="elementHeight">Высота элемента вывода.</param>
    /// <param name="pixelWidth">Ширина изображения в пикселах.</param>
    /// <param name="pixelHeight">Высота изображения в пикселах.</param>
    /// <returns>
    /// Столбец и строка пиксела либо <see langword="null"/>, если точка попала
    /// на поле рядом с изображением или вне элемента.
    /// </returns>
    public static (int X, int Y)? PixelAt(
        double x,
        double y,
        double elementWidth,
        double elementHeight,
        int pixelWidth,
        int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0 || elementWidth <= 0 || elementHeight <= 0)
        {
            return null;
        }

        var scale = Math.Min(elementWidth / pixelWidth, elementHeight / pixelHeight);

        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return null;
        }

        // Изображение вписано по центру: непокрытое место делится пополам.
        var shownWidth = pixelWidth * scale;
        var shownHeight = pixelHeight * scale;
        var offsetX = (elementWidth - shownWidth) / 2;
        var offsetY = (elementHeight - shownHeight) / 2;

        var column = (int)Math.Floor((x - offsetX) / scale);
        var row = (int)Math.Floor((y - offsetY) / scale);

        if (column < 0 || row < 0 || column >= pixelWidth || row >= pixelHeight)
        {
            return null;
        }

        return (column, row);
    }
}
