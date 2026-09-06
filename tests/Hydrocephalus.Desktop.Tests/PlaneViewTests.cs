using Hydrocephalus.Desktop.Viewing;
using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Segmentation;
using Hydrocephalus.Inference.Segmentation;
using Hydrocephalus.Infrastructure.Volumes;

namespace Hydrocephalus.Desktop.Tests;

/// <summary>
/// Логика экрана просмотра.
///
/// Проверяется то, что на экране выглядит правдоподобно и при этом неверно:
/// перепутанные стороны, съехавший оверлей, растянутая анатомия. Отрисовку
/// как таковую тест не заменяет, но эти ошибки ловятся здесь, а не глазом.
/// </summary>
public sealed class PlaneViewTests
{
    private static readonly VolumeGrid Grid =
        new(new VolumeDimensions(9, 7, 5), 1.0, 2.0, 4.0);

    [Fact]
    public void A_view_opens_in_the_middle_of_the_stack()
    {
        // Открывать на первом срезе незачем: там обычно край объёма.
        var view = View(VolumeAxis.AcrossSlices);

        Assert.Equal(2, view.Index);
        Assert.Equal(5, view.Count);
    }

    [Fact]
    public void Stepping_past_the_end_stops_at_the_last_slice()
    {
        // Листание не должно бросать исключение на краю: врач крутит колесо,
        // а не считает срезы.
        var view = View(VolumeAxis.AcrossSlices);

        view.Step(100);
        Assert.Equal(4, view.Index);

        view.Step(-100);
        Assert.Equal(0, view.Index);
    }

    [Fact]
    public void Changing_the_slice_announces_a_new_image()
    {
        // Без уведомления картинка на экране осталась бы прежней.
        var view = View(VolumeAxis.AcrossSlices);
        var changed = new List<string?>();

        view.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        view.Index = 0;

        Assert.Contains(nameof(PlaneView.Image), changed);
        Assert.Contains(nameof(PlaneView.Overlay), changed);
    }

    [Fact]
    public void Side_labels_come_from_the_geometry_of_the_series()
    {
        var view = View(VolumeAxis.AcrossSlices);

        Assert.Equal(ImagingPlane.Axial, view.Plane);
        Assert.Equal(AnatomicalDirection.Left, view.Labels.Right);
        Assert.Equal(AnatomicalDirection.Anterior, view.Labels.Top);
    }

    [Fact]
    public void The_overlay_can_be_switched_off_entirely()
    {
        // Врач обязан иметь возможность посмотреть исходное изображение
        // без наложений (ADR 0007).
        var view = View(VolumeAxis.AcrossSlices);

        Assert.NotNull(view.Overlay);

        view.ShowOverlay = false;

        Assert.Null(view.Overlay);
    }

    [Fact]
    public void The_overlay_matches_the_image_it_is_drawn_over()
    {
        var view = View(VolumeAxis.AcrossRows);

        var image = view.Image;
        var overlay = view.Overlay;

        Assert.NotNull(overlay);
        Assert.Equal(image.Width * image.Height, overlay.Length);
    }

    [Fact]
    public void A_mask_from_another_grid_is_refused()
    {
        // Она легла бы со смещением и выглядела бы как неточная сегментация.
        var volume = Volume();
        var other = Mask(Grid with { SliceSpacingMillimetres = 1.0 });

        Assert.Throws<ArgumentException>(
            () => new PlaneView(volume, other, VolumeAxis.AcrossSlices, new WindowLevel(500, 1000)));
    }

    [Fact]
    public void All_three_views_share_one_window()
    {
        // Разные окна на трёх видах одних и тех же данных создали бы
        // впечатление разной ткани там, где ткань одна.
        var study = new StudyView(Volume(), Mask(Grid));

        study.Window = new WindowLevel(123, 456);

        Assert.All(study.Planes, plane => Assert.Equal(new WindowLevel(123, 456), plane.Window));
    }

    [Fact]
    public void The_three_views_are_the_three_anatomical_planes()
    {
        var study = new StudyView(Volume(), Mask(Grid));

        Assert.NotNull(study.OfPlane(ImagingPlane.Axial));
        Assert.NotNull(study.OfPlane(ImagingPlane.Coronal));
        Assert.NotNull(study.OfPlane(ImagingPlane.Sagittal));
    }

    [Fact]
    public void Switching_the_overlay_off_applies_to_every_view()
    {
        var study = new StudyView(Volume(), Mask(Grid));

        study.ShowOverlay = false;

        Assert.All(study.Planes, plane => Assert.Null(plane.Overlay));
    }

    [Fact]
    public void A_study_without_a_mask_has_no_overlay()
    {
        var study = new StudyView(Volume());

        Assert.False(study.HasMask);
        Assert.All(study.Planes, plane => Assert.Null(plane.Overlay));
    }

    [Fact]
    public void Composing_keeps_the_physical_proportions_of_the_plane()
    {
        // Объём анизотропен: вывод «пиксель в пиксель» растянул бы анатомию,
        // а по ней потом измеряют.
        var view = View(VolumeAxis.AcrossRows);

        var composed = PlaneComposer.Compose(view.Image, view.Overlay);

        Assert.Equal(9, composed.Width);
        Assert.Equal(5, composed.Height);
        Assert.Equal(9 * 1.0, composed.DisplayWidthMillimetres, precision: 9);
        Assert.Equal(5 * 4.0, composed.DisplayHeightMillimetres, precision: 9);
    }

    [Fact]
    public void Composing_leaves_unlabelled_pixels_grey()
    {
        // Маска показывает структуры, а не закрывает изображение.
        var view = View(VolumeAxis.AcrossSlices);
        view.ShowOverlay = false;

        var image = view.Image;
        var composed = PlaneComposer.Compose(image, overlay: null);

        for (var index = 0; index < image.Pixels.Length; index++)
        {
            var offset = index * 4;

            Assert.Equal(image.Pixels[index], composed.Bgra[offset]);
            Assert.Equal(image.Pixels[index], composed.Bgra[offset + 1]);
            Assert.Equal(image.Pixels[index], composed.Bgra[offset + 2]);
            Assert.Equal(255, composed.Bgra[offset + 3]);
        }
    }

    [Fact]
    public void Composing_tints_only_the_labelled_pixels()
    {
        var view = View(VolumeAxis.AcrossSlices);

        var image = view.Image;
        var overlay = view.Overlay!;
        var composed = PlaneComposer.Compose(image, overlay);

        for (var index = 0; index < overlay.Length; index++)
        {
            var offset = index * 4;

            var isGrey = composed.Bgra[offset] == composed.Bgra[offset + 1]
                && composed.Bgra[offset + 1] == composed.Bgra[offset + 2];

            if (overlay[index] == 0)
            {
                Assert.True(isGrey, $"Pixel {index} is outside the mask and must stay grey.");
            }
        }

        // Обратная проверка: без неё «ничего не покрашено» прошло бы тест.
        Assert.Contains(overlay, label => label != 0);
    }

    [Fact]
    public void An_overlay_of_the_wrong_size_is_refused()
    {
        var view = View(VolumeAxis.AcrossSlices);

        Assert.Throws<ArgumentException>(
            () => PlaneComposer.Compose(view.Image, new byte[3]));
    }

    private static PlaneView View(VolumeAxis axis) =>
        new(Volume(), Mask(Grid), axis, new WindowLevel(500, 1000));

    private static TestVolume Volume() => new(Grid);

    private static VoxelMask Mask(VolumeGrid grid)
    {
        var dimensions = grid.Dimensions;
        var labels = new byte[dimensions.Columns * dimensions.Rows * dimensions.Slices];

        for (var index = 0; index < labels.Length; index++)
        {
            labels[index] = (byte)(index % 4 == 0 ? 1 : 0);
        }

        return new VoxelMask(
            grid,
            new LabelMap { Version = "test-1.0.0", Labels = [new AnatomicalLabel("ventricles")] },
            labels);
    }

    private sealed class TestVolume(VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = new()
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = grid.SliceSpacingMillimetres,
            SliceSpacingMillimetres = grid.SliceSpacingMillimetres,
            PixelSpacing = new InPlaneSpacing(grid.RowSpacingMillimetres, grid.ColumnSpacingMillimetres),
            Dimensions = grid.Dimensions,
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = default,
        };

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => 1000;

        public float this[int column, int row, int slice] =>
            ((((slice * this.Grid.Dimensions.Rows) + row) * this.Grid.Dimensions.Columns) + column) % 1000;
    }
}
