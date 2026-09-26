"""Угол мозолистого тела из маски желудочков.

Маски здесь синтетические: у настоящего снимка нет известного правильного
ответа, а у двух прямых с заданным наклоном — есть, и он проверяем точно.
Клиническая проверка — согласие с измерением врача — делается не здесь.
"""

from __future__ import annotations

import math

import pytest

from hydrocephalus_ml.callosal import (
    MAXIMUM_VERTEX_OFFSET_MILLIMETRES,
    Point2D,
    crosses_midline,
    measure_callosal_angle,
    roof_profile,
    slice_spread,
)


def roof_of(half_angle_degrees: float, span: float = 25.0, step: float = 1.0) -> list[Point2D]:
    """Две прямые крыши, сходящиеся в нуле под известным углом.

    Каждая опускается наружу под ``half_angle_degrees`` к горизонтали, так что
    искомый угол между лучами равен ``180 - 2 * half_angle_degrees``.
    """
    slope = math.tan(math.radians(half_angle_degrees))
    points = []
    x = -span

    while x <= span + 1e-9:
        if x != 0.0:
            points.append(Point2D(x, -slope * abs(x)))
        x += step

    return points


@pytest.mark.parametrize("half_angle", [10.0, 30.0, 45.0, 60.0])
def test_a_known_angle_is_recovered(half_angle: float) -> None:
    measurement = measure_callosal_angle(roof_of(half_angle))

    assert measurement.degrees == pytest.approx(180.0 - 2.0 * half_angle, abs=0.01)


def test_a_flat_roof_is_refused_rather_than_called_180_degrees() -> None:
    # Прежде здесь ожидался угол 180° с доводом, что так выглядят самые широкие
    # желудочки. Довод неверен: при иНТГ расширенные желудочки поднимают крыши
    # в острый пик, а не расплющивают их. Плоская крыша — вырожденный случай, и
    # клиническая серия с такой крышей дала 163,7° и 170,2° в двух прогонах.
    with pytest.raises(ValueError, match="пик"):
        measure_callosal_angle(roof_of(0.0))


def test_the_narrow_angle_of_hydrocephalus_and_the_wide_one_of_health_differ() -> None:
    # Порогов в коде нет, но направление должно быть верным: при иНТГ угол
    # острый. Если знак где-то перевернуть, тест поймает это раньше данных.
    narrow = measure_callosal_angle(roof_of(60.0))
    wide = measure_callosal_angle(roof_of(30.0))

    assert narrow.degrees < 90.0 < wide.degrees


def test_the_vertex_is_where_the_roofs_meet() -> None:
    measurement = measure_callosal_angle(roof_of(30.0))

    assert measurement.vertex is not None
    assert measurement.vertex.x == pytest.approx(0.0, abs=0.01)
    assert measurement.vertex.y == pytest.approx(0.0, abs=0.01)


def test_the_vertex_can_sit_above_the_mask() -> None:
    # У расширенных желудочков крыши сходятся выше, чем идёт сама маска:
    # вершина ищется пересечением прямых, а не поиском верхней точки.
    roof = [Point2D(x, -abs(x) + 0.0) for x in (-8.0, -6.0, -4.0, 4.0, 6.0, 8.0)]

    measurement = measure_callosal_angle(roof)

    assert measurement.vertex is not None
    assert measurement.vertex.y > max(point.y for point in roof) - 1e-9


def test_only_the_medial_part_of_the_roof_is_used() -> None:
    # Дальше от средней линии стенка заворачивает вбок. Если брать её целиком,
    # прямая описывает изгиб, а не крышу, и угол уезжает.
    roof = roof_of(30.0, span=25.0)
    bent = [Point2D(p.x, p.y - 6.0 if abs(p.x) > 20.0 else p.y) for p in roof]

    assert measure_callosal_angle(bent, span_millimetres=20.0).degrees == pytest.approx(
        120.0, abs=0.01
    )
    assert measure_callosal_angle(bent, span_millimetres=25.0).degrees < 119.0


def test_a_roof_found_on_one_side_only_is_refused() -> None:
    # Односторонняя маска даёт число, неотличимое по виду от настоящего.
    # Тот же принцип уже принят для сегментации (baseline-1.4.0).
    one_sided = [point for point in roof_of(30.0) if point.x < 0.0]

    with pytest.raises(ValueError, match="с обеих сторон"):
        measure_callosal_angle(one_sided)


def test_too_few_points_are_refused() -> None:
    sparse = [Point2D(-2.0, -1.0), Point2D(-1.0, -0.5), Point2D(1.0, -0.5), Point2D(2.0, -1.0)]

    with pytest.raises(ValueError, match="с обеих сторон"):
        measure_callosal_angle(sparse)


def test_the_roof_is_the_topmost_voxel_of_each_column() -> None:
    #  . X .      столбцы: -1, 0, +1; строки снизу вверх: 0, 1
    #  X X X
    mask = [
        [True, True, True],
        [False, True, False],
    ]

    profile = roof_profile(mask, column_positions=[-1.0, 0.0, 1.0], row_positions=[0.0, 1.0])

    assert profile == [Point2D(-1.0, 0.0), Point2D(0.0, 1.0), Point2D(1.0, 0.0)]


def test_empty_columns_are_skipped_rather_than_guessed() -> None:
    mask = [[True, False, True]]

    profile = roof_profile(mask, column_positions=[-1.0, 0.0, 1.0], row_positions=[0.0])

    assert [point.x for point in profile] == [-1.0, 1.0]


def test_a_mask_that_does_not_match_its_grid_is_refused() -> None:
    with pytest.raises(ValueError, match="строк"):
        roof_profile([[True]], column_positions=[0.0], row_positions=[0.0, 1.0])


def test_a_vertex_away_from_the_midline_is_refused() -> None:
    # Крыши разной высоты и наклона пересекаются в стороне: подогнанные прямые
    # описывают не две симметричные крыши. Так выглядела клиническая серия, где
    # желудочки слились в одну массу, — угол вышел 170 градусов, что лежит
    # внутри правдоподобного диапазона и ничем иным не отличимо от настоящего.
    roof = [Point2D(x, -0.05 * abs(x)) for x in (-18.0, -14.0, -10.0, -6.0)]
    roof += [Point2D(x, -1.2 * x + 8.0) for x in (6.0, 10.0, 14.0, 18.0)]

    with pytest.raises(ValueError, match="средней линии"):
        measure_callosal_angle(roof)


def test_a_vertex_just_off_the_midline_is_accepted() -> None:
    # Порог не должен браковать норму: у 27 субъектов AFIDs вершина уложилась
    # в ±2,2 мм, и эти случаи обязаны проходить.
    roof = roof_of(30.0)
    shifted = [Point2D(point.x + 2.0, point.y) for point in roof]

    measurement = measure_callosal_angle(shifted)

    assert measurement.vertex is not None
    assert abs(measurement.vertex.x) < MAXIMUM_VERTEX_OFFSET_MILLIMETRES


@pytest.mark.parametrize(
    ("angles", "expected"),
    [
        # Измерено на публичных снимках AFIDs, шаг в миллиметр от плоскости
        # задней спайки. Третий случай — тот, ради которого размах и выводится.
        ([140.9, 142.5, 140.1, 140.7, 137.6], 4.9),
        ([129.0, 126.6, 123.8, 123.6, 122.1], 6.9),
        ([90.9, 88.0, 86.7, 98.2, 102.1], 15.4),
    ],
)
def test_the_measured_spreads_are_reproduced(angles: list[float], expected: float) -> None:
    assert slice_spread(angles) == pytest.approx(expected, abs=0.05)


def test_planes_without_a_measurement_are_skipped() -> None:
    # Отказ на крайней плоскости говорит о покрытии, а не об устойчивости.
    assert slice_spread([None, 120.0, 124.0, None]) == pytest.approx(4.0)


def test_a_single_plane_gives_no_spread() -> None:
    with pytest.raises(ValueError, match="двум плоскостям"):
        slice_spread([120.0, None])


def test_two_separate_ventricles_do_not_cross_the_midline() -> None:
    #  X X . . . X X      два желудочка, между ними перегородка
    mask = [[True, True, False, False, False, True, True]]
    columns = [-9.0, -6.0, -3.0, 0.0, 3.0, 6.0, 9.0]

    assert not crosses_midline(mask, columns)


def test_merged_ventricles_are_detected() -> None:
    # Та же маска, но перегородки нет: одна область дотягивается до обеих
    # сторон. Так выглядела клиническая серия, давшая 163,7°.
    mask = [[True, True, True, True, True, True, True]]
    columns = [-9.0, -6.0, -3.0, 0.0, 3.0, 6.0, 9.0]

    assert crosses_midline(mask, columns)


def test_touching_the_midline_from_one_side_is_not_crossing() -> None:
    # Желудочек, подходящий к средней линии, но не переходящий её, — норма.
    mask = [[True, True, True, True, False, False, False]]
    columns = [-9.0, -6.0, -3.0, 0.0, 3.0, 6.0, 9.0]

    assert not crosses_midline(mask, columns)


def test_components_are_traced_through_rows() -> None:
    #  X . X      сверху две отдельные области,
    #  X X X      снизу они соединены — значит это одна область
    mask = [[True, False, True], [True, True, True]]
    columns = [-9.0, 0.0, 9.0]

    assert crosses_midline(mask, columns)


def test_a_one_sided_slope_is_refused() -> None:
    # Односкатная крыша: обе половины идут в одну сторону. Пика нет, и вершина
    # оказывается далеко в стороне.
    roof = [Point2D(x, 0.4 * x) for x in (-15.0, -10.0, -5.0, 5.0, 10.0, 15.0)]

    with pytest.raises(ValueError, match="пик"):
        measure_callosal_angle(roof)


def test_a_proper_peak_is_accepted() -> None:
    measurement = measure_callosal_angle(roof_of(30.0))

    assert measurement.degrees == pytest.approx(120.0, abs=0.01)
