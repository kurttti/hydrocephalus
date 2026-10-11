using Hydrocephalus.Domain.Imaging;
using Hydrocephalus.Domain.Measurements;
using Hydrocephalus.Inference.Measurements;

namespace Hydrocephalus.Inference.Tests;

/// <summary>
/// Угол мозолистого тела, отмеченный врачом.
///
/// Случай, ради которого разметка и нужна: это второй признак плана M3 и
/// единственный, который отличает иНТГ от заместительной гидроцефалии — индекс
/// Эванса растёт и там и там. Меряется он на одном корональном срезе, а
/// корональная серия с покрытием есть в наборе почти у всех исследований против
/// семи с объёмной (docs/data/README.md).
///
/// Проверяется не арифметика угла — она общая с автоматическим путём, — а то,
/// что из неправильных точек не выходит правдоподобное число и что рядом с
/// углом записано всё, без чего он несравним.
/// </summary>
public sealed class ManualCallosalAngleTests
{
    [Fact]
    public void Three_points_on_a_thick_sliced_coronal_series_give_the_angle()
    {
        // Лучи под 45° влево-вверх и вправо-вверх: угол 90°.
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(100, 100, 10)));
        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(80, 80, 10)));
        Assert.Equal(MarkingOutcome.Added, marking.Add(new VoxelPosition(120, 80, 10)));

        Assert.True(marking.IsComplete);

        var result = marking.Measure(ThickSlicedCoronal());

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.Equal(90.0, result.Biomarker.Value, tolerance: 1e-9);
        Assert.False(result.Biomarker.IsOutOfRange);
        Assert.Equal(10, result.PlaneIndex);
    }

    [Fact]
    public void The_order_of_the_two_roofs_does_not_change_the_angle()
    {
        // Отличие от индекса Эванса: там порядок задаёт ось измерения, здесь он
        // называет только вершину. Левую и правую крыши врач вправе отмечать
        // как придётся, и число от этого меняться не должно.
        var volume = ThickSlicedCoronal();

        var first = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);
        first.Add(new VoxelPosition(100, 100, 10));
        first.Add(new VoxelPosition(80, 80, 10));
        first.Add(new VoxelPosition(120, 80, 10));

        var second = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);
        second.Add(new VoxelPosition(100, 100, 10));
        second.Add(new VoxelPosition(120, 80, 10));
        second.Add(new VoxelPosition(80, 80, 10));

        Assert.Equal(
            first.Measure(volume).Biomarker!.Value,
            second.Measure(volume).Biomarker!.Value,
            tolerance: 1e-9);
    }

    [Fact]
    public void A_manual_angle_is_told_apart_from_an_automatic_one()
    {
        // Автоматический путь уже существует в исследовательском контуре
        // (`hydrocephalus_ml.callosal`) и после переноса в приложение писал бы
        // в отчёт тот же признак. Без различия в коде метода две записи стали
        // бы неотличимы, а сверять предстоит именно их друг с другом.
        var result = Complete().Measure(ThickSlicedCoronal());

        Assert.NotNull(result.Biomarker);
        Assert.Equal(LinearBiomarkers.ManualCallosalAngleCode, result.Biomarker.Method.Code);
        Assert.NotEqual(LinearBiomarkers.CallosalAngleCode, result.Biomarker.Method.Code);
    }

    [Fact]
    public void A_manual_angle_is_marked_questionable_unlike_a_manual_index()
    {
        // Решение, а не копия соседнего измерения. Ручной индекс Эванса надёжен
        // потому, что ручная разметка и есть его эталон: определение требует
        // одного аксиального среза, и отмеченный срез ему отвечает. Здесь
        // определение требует среза через заднюю спайку перпендикулярно линии
        // AC–PC, а приложение их не ищет, — отмеченная плоскость определению не
        // равна, и насколько она от него отстоит, измерено (docs/ml/README.md).
        var result = Complete().Measure(ThickSlicedCoronal());

        Assert.NotNull(result.Biomarker);
        Assert.Equal(MeasurementQuality.Questionable, result.Biomarker.Quality);
    }

    [Fact]
    public void The_tilt_of_the_marking_plane_is_recorded_next_to_the_angle()
    {
        // Без наклона плоскости углы несравнимы между собой: отклонение от
        // истинной корональной значимо меняет угол у больных иНТГ и незначимо
        // у здоровых уже при ±5–10°, а геометрия серии в отчёт не идёт. По
        // описи набора медиана наклона различается между группами — 7,9° против
        // 4,7°, а на однородных корональных T2 10,0° против 4,7° при p = 0,016, —
        // то есть это не теоретическая оговорка.
        var straight = Complete().Measure(ThickSlicedCoronal());

        Assert.Equal(0.0, straight.PitchDegrees!.Value, tolerance: 1e-9);
        Assert.Equal(0.0, straight.YawDegrees!.Value, tolerance: 1e-9);

        // Блок срезов наклонён на 20° вокруг оси лево-право: нормаль поднимается
        // к темени, то есть верх плоскости уходит вперёд.
        var tilted = Complete().Measure(ThickSlicedCoronal(pitchDegrees: 20.0));

        Assert.Equal(20.0, tilted.PitchDegrees!.Value, tolerance: 1e-6);
        Assert.Equal(0.0, tilted.YawDegrees!.Value, tolerance: 1e-6);

        // Угол при этом тот же: лучи лежат в плоскости, и наклон самой плоскости
        // на отмеченные точки не действует. Меняется то, какую анатомию режет
        // плоскость, — а это видно только по записанному наклону.
        Assert.Equal(90.0, tilted.Biomarker!.Value, tolerance: 1e-6);
    }

    [Fact]
    public void A_plane_turned_about_the_head_foot_axis_shows_up_as_yaw_and_not_as_pitch()
    {
        // Два угла записываются отдельно, а не складываются в одно полное
        // отклонение: чувствительность угла зависит от оси поворота, и
        // сложенные числа обратно уже не разделить.
        var result = Complete().Measure(ThickSlicedCoronal(yawDegrees: 15.0));

        Assert.Equal(15.0, result.YawDegrees!.Value, tolerance: 1e-6);
        Assert.Equal(0.0, result.PitchDegrees!.Value, tolerance: 1e-6);
    }

    [Fact]
    public void Turning_the_frame_in_its_own_plane_does_not_change_the_angle()
    {
        // Преимущество ручного пути перед автоматическим: угол между двумя
        // лучами при вершине к повороту плоскости вокруг своей нормали
        // безразличен, тогда как подгонка прямых к профилю крыши от него
        // зависит. Отмечено теми же точками, повёрнутыми на 30° вокруг вершины.
        var volume = ThickSlicedCoronal();
        var upright = Complete().Measure(volume).Biomarker!.Value;

        var turned = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);
        turned.Add(new VoxelPosition(100, 100, 10));
        turned.Add(Rotated(column: -20, row: -20, degrees: 30));
        turned.Add(Rotated(column: 20, row: -20, degrees: 30));

        Assert.Equal(upright, turned.Measure(volume).Biomarker!.Value, tolerance: 1e-6);
    }

    [Fact]
    public void Marking_on_another_slice_starts_over_instead_of_measuring_across_slices()
    {
        // Врач листает срезы, подбирая уровень. Точки, поставленные до
        // перелистывания, к новому срезу отношения не имеют, а косое измерение
        // выглядит как обычное.
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(80, 80, 10));

        Assert.Equal(
            MarkingOutcome.RestartedOnAnotherPlane,
            marking.Add(new VoxelPosition(120, 80, 11)));

        Assert.Single(marking.Points);
        Assert.Equal(11, marking.PlaneIndex);
    }

    [Fact]
    public void An_incomplete_marking_refuses_instead_of_measuring_what_it_has()
    {
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(80, 80, 10));

        var result = marking.Measure(ThickSlicedCoronal());

        Assert.Equal(ManualCallosalAngleRefusal.PointsIncomplete, result.Refusal);
        Assert.Null(result.Biomarker);
        Assert.Empty(result.Measurements);
    }

    [Fact]
    public void A_roof_point_on_the_vertex_is_refused_by_name()
    {
        // Промах постановки: луч вырожден в точку, угла нет. Назвать причину
        // нужно потому, что на экране два совпавших нажатия ничем не отличаются
        // от одного, а записанное в хранилище остаётся навсегда.
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(120, 80, 10));

        var result = marking.Measure(ThickSlicedCoronal());

        Assert.Equal(ManualCallosalAngleRefusal.RayHasZeroLength, result.Refusal);
        Assert.Null(result.Biomarker);
    }

    [Fact]
    public void An_axial_plane_is_refused_by_name()
    {
        // Те же три точки на аксиальном срезе дают число того же вида и другого
        // смысла, и различить их в отчёте по значению невозможно.
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(80, 80, 10));
        marking.Add(new VoxelPosition(120, 80, 10));

        var result = marking.Measure(ThickSlicedAxial());

        Assert.Equal(ManualCallosalAngleRefusal.PlaneIsNotCoronal, result.Refusal);
        Assert.Null(result.Biomarker);
    }

    [Fact]
    public void A_nearly_straight_angle_is_recorded_and_flagged_rather_than_hidden()
    {
        // Соглашение то же, что у индекса: значение не скрывается, но и не
        // выдаётся за измерение. Плоские крыши дают почти развёрнутый угол, и
        // такое число ничем не отличалось бы от настоящего.
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(20, 97, 10));
        marking.Add(new VoxelPosition(180, 97, 10));

        var result = marking.Measure(ThickSlicedCoronal());

        Assert.Null(result.Refusal);
        Assert.NotNull(result.Biomarker);
        Assert.True(result.Biomarker.IsOutOfRange);
    }

    [Fact]
    public void The_angle_comes_with_both_tilt_values_as_one_measurement()
    {
        // Записываются они одной версией отчёта, потому что получены одним
        // действием, и порядок задан здесь: угол первым, за ним наклон и
        // поворот плоскости.
        var result = Complete().Measure(ThickSlicedCoronal());

        Assert.Equal(3, result.Measurements.Count);
        Assert.Equal(LinearBiomarkers.ManualCallosalAngleCode, result.Measurements[0].Method.Code);
        Assert.Equal(LinearBiomarkers.CoronalPlanePitchCode, result.Measurements[1].Method.Code);
        Assert.Equal(LinearBiomarkers.CoronalPlaneYawCode, result.Measurements[2].Method.Code);

        // Наклон известен точно: он прочитан из геометрии серии, а не измерен
        // врачом, и сомнительность угла на него не переносится.
        Assert.Equal(MeasurementQuality.Reliable, result.Measurements[1].Quality);
    }

    [Fact]
    public void The_last_point_can_be_taken_back()
    {
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(80, 80, 10));

        Assert.True(marking.UndoLast());
        Assert.Single(marking.Points);

        Assert.True(marking.UndoLast());
        Assert.Empty(marking.Points);
        Assert.Null(marking.PlaneIndex);

        Assert.False(marking.UndoLast());
    }

    [Fact]
    public void A_fourth_point_is_not_taken()
    {
        var marking = Complete();

        Assert.Equal(MarkingOutcome.AlreadyComplete, marking.Add(new VoxelPosition(1, 1, 10)));
        Assert.Equal(ManualCallosalAngleMarking.RequiredPoints, marking.Points.Count);
    }

    [Fact]
    public void A_sagittally_acquired_volume_is_marked_on_its_coronal_planes()
    {
        // То же, что у индекса Эванса: объёмная серия не обязана быть получена
        // коронально, и сагиттальная 3D T1 содержит корональные плоскости так
        // же, только поперёк другой оси сетки.
        var dimensions = new VolumeDimensions(200, 200, 200);

        var volume = new TestVolume(
            new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.ThreeDimensional,
                SliceThicknessMillimetres = 1.0,
                SliceSpacingMillimetres = 1.0,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = dimensions,

                // Столбцы вперёд-назад, строки вверх-вниз: срезы идут налево,
                // а корональные плоскости лежат поперёк столбцов.
                RowDirection = new SpatialVector(0, 1, 0),
                ColumnDirection = new SpatialVector(0, 0, -1),
                Origin = default,
            },
            new VolumeGrid(dimensions, 1.0, 1.0, 1.0));

        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossColumns);

        // Вершина и две крыши: лучи расходятся на 20 налево-вверх и
        // 20 направо-вверх, то есть угол 90°.
        marking.Add(new VoxelPosition(100, 100, 100));
        marking.Add(new VoxelPosition(100, 80, 80));
        marking.Add(new VoxelPosition(100, 80, 120));

        var result = marking.Measure(volume);

        Assert.Null(result.Refusal);
        Assert.Equal(90.0, result.Biomarker!.Value, tolerance: 1e-9);
        Assert.Equal(0.0, result.PitchDegrees!.Value, tolerance: 1e-9);
    }

    /// <summary>
    /// Поворачивает смещение от вершины в плоскости кадра.
    /// </summary>
    private static VoxelPosition Rotated(double column, double row, double degrees)
    {
        var radians = double.DegreesToRadians(degrees);
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);

        return new VoxelPosition(
            100 + ((column * cosine) - (row * sine)),
            100 + ((column * sine) + (row * cosine)),
            10);
    }

    private static ManualCallosalAngleMarking Complete()
    {
        var marking = new ManualCallosalAngleMarking(VolumeAxis.AcrossSlices);

        marking.Add(new VoxelPosition(100, 100, 10));
        marking.Add(new VoxelPosition(80, 80, 10));
        marking.Add(new VoxelPosition(120, 80, 10));

        return marking;
    }

    /// <summary>
    /// Толстосрезовая корональная серия, при необходимости наклонённая.
    ///
    /// Наклон задаётся направляющими косинусами, а не полем: нормаль срезов
    /// выводится из них векторным произведением, как в DICOM, и подменить её
    /// отдельно значило бы проверять не ту геометрию, с которой работает код.
    /// </summary>
    /// <param name="pitchDegrees">Поворот блока вокруг оси лево-право.</param>
    /// <param name="yawDegrees">Поворот блока вокруг оси голова-ноги.</param>
    private static TestVolume ThickSlicedCoronal(
        double pitchDegrees = 0.0,
        double yawDegrees = 0.0)
    {
        var dimensions = new VolumeDimensions(200, 200, 30);

        var pitch = double.DegreesToRadians(pitchDegrees);

        // Строки идут налево, столбцы — к ногам: нормаль смотрит назад.
        // Наклон вокруг лево-право поднимает нормаль к темени.
        var row = new SpatialVector(1, 0, 0);
        var column = new SpatialVector(0, Math.Sin(pitch), -Math.Cos(pitch));

        if (yawDegrees != 0.0)
        {
            // Поворот вокруг оси голова-ноги уводит налево сами строки; знак
            // взят так, чтобы положительный поворот блока давал положительный
            // отсчёт в системе пациента.
            var yaw = double.DegreesToRadians(-yawDegrees);

            row = new SpatialVector(Math.Cos(yaw), Math.Sin(yaw), 0);
        }

        return new TestVolume(
            new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.TwoDimensional,
                SliceThicknessMillimetres = 5.0,
                SliceSpacingMillimetres = 5.0,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = dimensions,
                RowDirection = row,
                ColumnDirection = column,
                Origin = default,
            },
            new VolumeGrid(dimensions, 1.0, 1.0, 5.0));
    }

    private static TestVolume ThickSlicedAxial()
    {
        var dimensions = new VolumeDimensions(200, 200, 40);

        return new TestVolume(
            new SeriesGeometry
            {
                AcquisitionType = MrAcquisitionType.TwoDimensional,
                SliceThicknessMillimetres = 5.0,
                SliceSpacingMillimetres = 5.0,
                PixelSpacing = new InPlaneSpacing(1.0, 1.0),
                Dimensions = dimensions,
                RowDirection = new SpatialVector(1, 0, 0),
                ColumnDirection = new SpatialVector(0, 1, 0),
                Origin = default,
            },
            new VolumeGrid(dimensions, 1.0, 1.0, 5.0));
    }

    // Значения вокселей не нужны: разметка приходит точками, а не находится
    // по яркости. Проверяется геометрия и порядок, а не содержимое снимка.
    private sealed class TestVolume(SeriesGeometry geometry, VolumeGrid grid) : IVoxelVolume
    {
        public SeriesGeometry Geometry { get; } = geometry;

        public VolumeGrid Grid { get; } = grid;

        public float Minimum => 0;

        public float Maximum => 0;

        public float this[int column, int row, int slice] => 0;
    }
}
