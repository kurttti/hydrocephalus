using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Приведение видов к радиологической ориентации.
///
/// Реконструкция сагиттальной плоскости корональной серии выходила лёжа на боку:
/// сверху передняя сторона, слева темя. Подписи были верны, но снимок нечитаем.
/// Главное здесь — что изображение и маска поворачиваются одинаково: иначе
/// оверлей лёг бы зеркально к анатомии, и это выглядело бы как неточная сегментация.
/// </summary>
public sealed class DisplayOrientationTests
{
    private static readonly AnatomicalDirection A = AnatomicalDirection.Anterior;
    private static readonly AnatomicalDirection P = AnatomicalDirection.Posterior;
    private static readonly AnatomicalDirection S = AnatomicalDirection.Superior;
    private static readonly AnatomicalDirection I = AnatomicalDirection.Inferior;
    private static readonly AnatomicalDirection R = AnatomicalDirection.Right;
    private static readonly AnatomicalDirection L = AnatomicalDirection.Left;

    [Fact]
    public void A_sagittal_view_lying_on_its_side_is_stood_up_with_the_face_to_the_left()
    {
        // Ровно то, что было на экране: сверху A, слева S.
        var image = Image(3, 2, new EdgeLabels(Left: S, Right: I, Top: A, Bottom: P, IsOblique: false), ImagingPlane.Sagittal);

        var shown = DisplayOrientation.Apply(image, DisplayOrientation.For(image.Labels, image.Plane));

        Assert.Equal(S, shown.Labels.Top);
        Assert.Equal(A, shown.Labels.Left);
        Assert.Equal((2, 3), (shown.Width, shown.Height));
    }

    [Theory]
    [InlineData(ImagingPlane.Axial)]
    [InlineData(ImagingPlane.Coronal)]
    public void Axial_and_coronal_views_put_the_patient_right_on_the_left(ImagingPlane plane)
    {
        var top = plane == ImagingPlane.Axial ? A : S;
        var bottom = plane == ImagingPlane.Axial ? P : I;

        // Перевёрнутая серия: сверху низ, слева левая сторона пациента.
        var image = Image(3, 2, new EdgeLabels(Left: L, Right: R, Top: bottom, Bottom: top, IsOblique: false), plane);

        var shown = DisplayOrientation.Apply(image, DisplayOrientation.For(image.Labels, image.Plane));

        Assert.Equal(top, shown.Labels.Top);
        Assert.Equal(R, shown.Labels.Left);
        Assert.Equal((3, 2), (shown.Width, shown.Height));
    }

    [Fact]
    public void A_view_already_in_standard_orientation_is_left_as_it_is()
    {
        var labels = new EdgeLabels(Left: R, Right: L, Top: A, Bottom: P, IsOblique: false);

        Assert.Equal(PlaneTransform.Identity, DisplayOrientation.For(labels, ImagingPlane.Axial));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void The_mask_turns_together_with_the_image(bool transpose, bool flipHorizontal, bool flipVertical)
    {
        // Пиксель изображения и значение маски заданы одним и тем же номером
        // исходной позиции: после любого преобразования они обязаны совпадать.
        const int Width = 4;
        const int Height = 3;

        var transform = new PlaneTransform(transpose, flipHorizontal, flipVertical);
        var image = Image(Width, Height, new EdgeLabels(R, L, A, P, false), ImagingPlane.Axial);
        var mask = image.Pixels.ToArray();

        var shownImage = DisplayOrientation.Apply(image, transform);
        var shownMask = DisplayOrientation.Apply(mask, Width, Height, transform);

        Assert.Equal(shownImage.Pixels, shownMask);
        Assert.Equal(image.Pixels.Order(), shownImage.Pixels.Order());
    }

    [Fact]
    public void Transposing_swaps_the_physical_pixel_size_as_well()
    {
        // Анизотропный пиксель, не повёрнутый вместе с картинкой, растянул бы анатомию.
        var image = Image(3, 2, new EdgeLabels(S, I, A, P, false), ImagingPlane.Sagittal, pixelWidth: 1.0, pixelHeight: 7.0);

        var shown = DisplayOrientation.Apply(image, DisplayOrientation.For(image.Labels, image.Plane));

        Assert.Equal(7.0, shown.PixelWidthMillimetres);
        Assert.Equal(1.0, shown.PixelHeightMillimetres);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Every_shown_pixel_points_back_to_the_source_pixel_it_came_from(
        bool transpose,
        bool flipHorizontal,
        bool flipVertical)
    {
        // Ручное измерение идёт в обратную сторону: врач указывает точку на
        // повёрнутом изображении, а мерить нужно в сетке объёма. Если прямое и
        // обратное отображения разойдутся, точка сядет не туда, и на экране это
        // будет выглядеть правдоподобно.
        //
        // Срез неквадратный намеренно: на квадратном перестановка ширины и
        // высоты не обнаруживается. Значения пикселов различны, поэтому
        // совпадение адреса проверяется по самому значению.
        const int Width = 5;
        const int Height = 3;

        var transform = new PlaneTransform(transpose, flipHorizontal, flipVertical);
        var source = Enumerable.Range(0, Width * Height).Select(index => (byte)index).ToArray();
        var shown = DisplayOrientation.Apply(source, Width, Height, transform);

        var outputWidth = transpose ? Height : Width;
        var outputHeight = transpose ? Width : Height;

        for (var y = 0; y < outputHeight; y++)
        {
            for (var x = 0; x < outputWidth; x++)
            {
                var (sourceX, sourceY) = DisplayOrientation.ToSource(
                    x, y, outputWidth, outputHeight, transform);

                Assert.InRange(sourceX, 0, Width - 1);
                Assert.InRange(sourceY, 0, Height - 1);
                Assert.Equal(shown[(y * outputWidth) + x], source[(sourceY * Width) + sourceX]);
            }
        }
    }

    private static PlaneImage Image(
        int width,
        int height,
        EdgeLabels labels,
        ImagingPlane plane,
        double pixelWidth = 1.0,
        double pixelHeight = 1.0) => new()
        {
            Width = width,
            Height = height,
            Pixels = [.. Enumerable.Range(0, width * height).Select(index => (byte)index)],
            PixelWidthMillimetres = pixelWidth,
            PixelHeightMillimetres = pixelHeight,
            Labels = labels,
            Plane = plane,
        };
}
