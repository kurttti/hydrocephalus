using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Viewing;

/// <summary>
/// Перестановка осей среза при выводе: транспонирование и отражения.
/// </summary>
/// <param name="Transpose">Поменять местами строки и столбцы.</param>
/// <param name="FlipHorizontal">Отразить слева направо (после транспонирования).</param>
/// <param name="FlipVertical">Отразить сверху вниз (после транспонирования).</param>
public readonly record struct PlaneTransform(bool Transpose, bool FlipHorizontal, bool FlipVertical)
{
    /// <summary>Вывод как есть.</summary>
    public static PlaneTransform Identity => default;
}

/// <summary>
/// Приведение среза к принятой в радиологии ориентации.
///
/// Срез извлекается по осям объёма, а оси объёма заданы тем, как шло
/// получение: у корональной серии реконструкция «сагиттальной» плоскости
/// выходит лёжа на боку — сверху передняя сторона, слева верх. Подписи
/// сторон при этом верны, но читать такой снимок врач не может: глаз ищет
/// темя сверху. Поэтому каждый вид поворачивается и отражается к виду
/// радиологического стандарта:
///
/// - аксиальный: сверху передняя сторона, правая сторона пациента слева;
/// - корональный: сверху темя, правая сторона пациента слева;
/// - сагиттальный: сверху темя, лицо слева.
///
/// Преобразование выводится из подписей сторон, а не из типа серии: подписи
/// сами следуют за направляющими косинусами, и перевёрнутая серия приводится
/// так же, как прямая. Маска преобразуется тем же преобразованием, что и
/// изображение, — иначе оверлей лёг бы зеркально.
/// </summary>
public static class DisplayOrientation
{
    /// <summary>
    /// Находит преобразование, приводящее срез к стандартной ориентации.
    /// </summary>
    /// <param name="labels">Подписи сторон извлечённого среза.</param>
    /// <param name="plane">Анатомическая плоскость среза.</param>
    /// <returns>Преобразование; тождественное, если стороны не определены.</returns>
    public static PlaneTransform For(EdgeLabels labels, ImagingPlane plane)
    {
        var (top, left) = plane switch
        {
            ImagingPlane.Axial => (AnatomicalDirection.Anterior, AnatomicalDirection.Right),
            ImagingPlane.Coronal => (AnatomicalDirection.Superior, AnatomicalDirection.Right),
            ImagingPlane.Sagittal => (AnatomicalDirection.Superior, AnatomicalDirection.Anterior),
            _ => (AnatomicalDirection.Unknown, AnatomicalDirection.Unknown),
        };

        if (top == AnatomicalDirection.Unknown
            || labels.Top == AnatomicalDirection.Unknown
            || labels.Left == AnatomicalDirection.Unknown)
        {
            return PlaneTransform.Identity;
        }

        // Нужное «верх» сейчас лежит по горизонтали — оси меняются местами.
        var transpose = labels.Left == top || labels.Right == top;
        var current = transpose ? Transposed(labels) : labels;

        return new PlaneTransform(
            transpose,
            FlipHorizontal: current.Left != left,
            FlipVertical: current.Top != top);
    }

    /// <summary>
    /// Применяет преобразование к срезу изображения.
    /// </summary>
    /// <param name="image">Срез, как он извлечён из объёма.</param>
    /// <param name="transform">Преобразование.</param>
    /// <returns>Срез в ориентации вывода.</returns>
    public static PlaneImage Apply(PlaneImage image, PlaneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (transform == PlaneTransform.Identity)
        {
            return image;
        }

        var labels = transform.Transpose ? Transposed(image.Labels) : image.Labels;

        if (transform.FlipHorizontal)
        {
            labels = labels with { Left = labels.Right, Right = labels.Left };
        }

        if (transform.FlipVertical)
        {
            labels = labels with { Top = labels.Bottom, Bottom = labels.Top };
        }

        return new PlaneImage
        {
            Width = transform.Transpose ? image.Height : image.Width,
            Height = transform.Transpose ? image.Width : image.Height,
            Pixels = Apply(image.Pixels, image.Width, image.Height, transform),
            PixelWidthMillimetres = transform.Transpose ? image.PixelHeightMillimetres : image.PixelWidthMillimetres,
            PixelHeightMillimetres = transform.Transpose ? image.PixelWidthMillimetres : image.PixelHeightMillimetres,
            Labels = labels,
            Plane = image.Plane,
        };
    }

    /// <summary>
    /// Применяет преобразование к срезу, заданному построчным массивом.
    /// </summary>
    /// <param name="plane">Значения среза строка за строкой.</param>
    /// <param name="width">Ширина среза до преобразования.</param>
    /// <param name="height">Высота среза до преобразования.</param>
    /// <param name="transform">Преобразование.</param>
    /// <returns>Значения в порядке вывода.</returns>
    public static byte[] Apply(byte[] plane, int width, int height, PlaneTransform transform)
    {
        ArgumentNullException.ThrowIfNull(plane);

        if (plane.Length != width * height)
        {
            throw new ArgumentException("The plane does not match its declared size.", nameof(plane));
        }

        if (transform == PlaneTransform.Identity)
        {
            return plane;
        }

        var outputWidth = transform.Transpose ? height : width;
        var outputHeight = transform.Transpose ? width : height;
        var output = new byte[plane.Length];

        for (var y = 0; y < outputHeight; y++)
        {
            for (var x = 0; x < outputWidth; x++)
            {
                var tx = transform.FlipHorizontal ? outputWidth - 1 - x : x;
                var ty = transform.FlipVertical ? outputHeight - 1 - y : y;

                var (sourceX, sourceY) = transform.Transpose ? (ty, tx) : (tx, ty);

                output[(y * outputWidth) + x] = plane[(sourceY * width) + sourceX];
            }
        }

        return output;
    }

    private static EdgeLabels Transposed(EdgeLabels labels) =>
        labels with
        {
            Left = labels.Top,
            Right = labels.Bottom,
            Top = labels.Left,
            Bottom = labels.Right,
        };
}
