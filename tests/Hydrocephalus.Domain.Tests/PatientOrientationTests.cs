using Hydrocephalus.Domain.Imaging;

namespace Hydrocephalus.Domain.Tests;

/// <summary>
/// Определение сторон изображения по направляющим косинусам.
///
/// ADR 0007 называет путаницу сторон клинически значимой ошибкой, поэтому подписи
/// выводятся из геометрии, а не из типа серии: серия может быть перевёрнута или
/// наклонена, и подпись обязана следовать за геометрией, а не за ожиданием.
///
/// Система координат пациента в DICOM — LPS: X растёт влево от пациента,
/// Y — назад, Z — вверх.
/// </summary>
public sealed class PatientOrientationTests
{
    [Theory]
    [InlineData(1, 0, 0, AnatomicalDirection.Left)]
    [InlineData(-1, 0, 0, AnatomicalDirection.Right)]
    [InlineData(0, 1, 0, AnatomicalDirection.Posterior)]
    [InlineData(0, -1, 0, AnatomicalDirection.Anterior)]
    [InlineData(0, 0, 1, AnatomicalDirection.Superior)]
    [InlineData(0, 0, -1, AnatomicalDirection.Inferior)]
    public void Axis_directions_follow_the_dicom_patient_coordinate_system(
        double x,
        double y,
        double z,
        AnatomicalDirection expected) =>
        Assert.Equal(expected, PatientOrientation.Of(new SpatialVector(x, y, z)));

    [Fact]
    public void Zero_vector_has_no_direction()
    {
        // Ноль означает отсутствующие или битые теги; выдумывать сторону нельзя.
        Assert.Equal(AnatomicalDirection.Unknown, PatientOrientation.Of(default));
    }

    [Fact]
    public void Standard_axial_series_labels_left_and_right_correctly()
    {
        // Строка идёт влево от пациента, столбец — назад: правый край
        // изображения показывает левую сторону пациента.
        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, 1, 0));

        Assert.Equal(AnatomicalDirection.Left, labels.Right);
        Assert.Equal(AnatomicalDirection.Right, labels.Left);
        Assert.Equal(AnatomicalDirection.Posterior, labels.Bottom);
        Assert.Equal(AnatomicalDirection.Anterior, labels.Top);
        Assert.False(labels.IsOblique);
    }

    [Fact]
    public void Flipped_axial_series_swaps_the_sides()
    {
        // Та же плоскость, но строка идёт вправо: подписи обязаны поменяться
        // местами. Именно этот случай и делает вывод сторон по типу серии
        // опасным.
        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(-1, 0, 0),
            new SpatialVector(0, 1, 0));

        Assert.Equal(AnatomicalDirection.Right, labels.Right);
        Assert.Equal(AnatomicalDirection.Left, labels.Left);
    }

    [Fact]
    public void Coronal_series_is_labelled_from_its_own_cosines()
    {
        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, 0, -1));

        Assert.Equal(AnatomicalDirection.Left, labels.Right);
        Assert.Equal(AnatomicalDirection.Inferior, labels.Bottom);
        Assert.Equal(AnatomicalDirection.Superior, labels.Top);
    }

    [Fact]
    public void Sagittal_series_is_labelled_from_its_own_cosines()
    {
        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(0, 1, 0),
            new SpatialVector(0, 0, -1));

        Assert.Equal(AnatomicalDirection.Posterior, labels.Right);
        Assert.Equal(AnatomicalDirection.Anterior, labels.Left);
        Assert.Equal(AnatomicalDirection.Inferior, labels.Bottom);
    }

    [Fact]
    public void Strongly_oblique_plane_is_marked_as_approximate()
    {
        // Показать врачу уверенную букву на срезе под 45° — значит утверждать
        // больше, чем известно.
        const double Cosine = 0.7071067811865476;

        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, Cosine, Cosine));

        Assert.True(labels.IsOblique);
    }

    [Fact]
    public void Slightly_tilted_plane_is_not_marked_oblique()
    {
        // Небольшой наклон гентри — норма клинической съёмки, и помечать
        // приблизительными каждую такую серию значило бы обесценить пометку.
        var labels = PatientOrientation.ForImagePlane(
            new SpatialVector(1, 0, 0),
            new SpatialVector(0, 0.985, 0.174));

        Assert.False(labels.IsOblique);
    }

    [Theory]
    [InlineData(0, 0, 1, ImagingPlane.Axial)]
    [InlineData(0, 0, -1, ImagingPlane.Axial)]
    [InlineData(0, 1, 0, ImagingPlane.Coronal)]
    [InlineData(1, 0, 0, ImagingPlane.Sagittal)]
    [InlineData(0, 0, 0, ImagingPlane.Unknown)]
    public void Plane_is_derived_from_the_slice_normal(
        double x,
        double y,
        double z,
        ImagingPlane expected) =>
        Assert.Equal(expected, PatientOrientation.PlaneOf(new SpatialVector(x, y, z)));

    [Fact]
    public void Geometry_reports_its_plane_and_slice_direction()
    {
        var geometry = new SeriesGeometry
        {
            AcquisitionType = MrAcquisitionType.ThreeDimensional,
            SliceThicknessMillimetres = 1.0,
            SliceSpacingMillimetres = 1.0,
            PixelSpacing = new InPlaneSpacing(1.0, 1.0),
            Dimensions = new VolumeDimensions(256, 256, 180),
            RowDirection = new SpatialVector(1, 0, 0),
            ColumnDirection = new SpatialVector(0, 1, 0),
            Origin = new SpatialVector(0, 0, 0),
        };

        Assert.Equal(ImagingPlane.Axial, geometry.Plane);
        Assert.Equal(AnatomicalDirection.Left, geometry.RowDirectionTowards);
        Assert.Equal(AnatomicalDirection.Posterior, geometry.ColumnDirectionTowards);
        Assert.Equal(AnatomicalDirection.Superior, geometry.SliceDirectionTowards);
    }
}
