"""Геометрия кадра AC–PC.

Главное, что здесь закреплено, — не формула, а факт о шаблоне: ICBM152 2009c не
выровнен по AC–PC. Если однажды поправка «перестанет быть нужной», эти тесты
скажут, что изменилось — разметка, шаблон или чья-то правка.
"""

from __future__ import annotations

import math

import pytest

from hydrocephalus_ml.acpc import (
    ICBM152_2009C_AC,
    ICBM152_2009C_PC,
    Point,
    commissural_pitch,
    level_commissures,
    rotate_about_left_right_axis,
)


def test_the_template_frame_is_not_an_acpc_frame() -> None:
    # Ради этого числа и написан модуль: кадр, унаследованный от шаблона без
    # поправки, повёрнут на пять с половиной градусов.
    pitch = commissural_pitch(ICBM152_2009C_AC, ICBM152_2009C_PC)

    assert pitch == pytest.approx(-5.49, abs=0.01)


def test_the_commissures_of_the_template_lie_on_the_midline() -> None:
    # Поправлять нужно только тангаж: по крену и рысканью кадр шаблона верен.
    assert abs(ICBM152_2009C_AC.x) < 0.6
    assert abs(ICBM152_2009C_PC.x) < 0.6


def test_the_correction_levels_the_line() -> None:
    correction = level_commissures(ICBM152_2009C_AC, ICBM152_2009C_PC)
    levelled = rotate_about_left_right_axis(
        ICBM152_2009C_PC, correction, centre=ICBM152_2009C_AC
    )

    assert correction == pytest.approx(5.49, abs=0.01)
    assert commissural_pitch(ICBM152_2009C_AC, levelled) == pytest.approx(0.0, abs=1e-9)


def test_the_correction_is_a_rotation_and_keeps_the_distance() -> None:
    correction = level_commissures(ICBM152_2009C_AC, ICBM152_2009C_PC)
    levelled = rotate_about_left_right_axis(
        ICBM152_2009C_PC, correction, centre=ICBM152_2009C_AC
    )

    def distance(a: Point, b: Point) -> float:
        return math.dist((a.x, a.y, a.z), (b.x, b.y, b.z))

    assert distance(ICBM152_2009C_AC, levelled) == pytest.approx(
        distance(ICBM152_2009C_AC, ICBM152_2009C_PC), abs=1e-9
    )


def test_a_level_line_needs_no_correction() -> None:
    ac, pc = Point(0.0, 24.0, 0.0), Point(0.0, -4.0, 0.0)

    assert commissural_pitch(ac, pc) == pytest.approx(0.0)
    assert level_commissures(ac, pc) == pytest.approx(0.0)


def test_the_sign_says_which_commissure_is_higher() -> None:
    ac_above = Point(0.0, 24.0, 1.0)
    pc = Point(0.0, -4.0, 0.0)

    assert commissural_pitch(ac_above, pc) > 0


def test_coincident_commissures_are_refused() -> None:
    # Молчаливый ноль здесь означал бы «кадр верен», что противоположно правде.
    point = Point(1.0, 2.0, 3.0)

    with pytest.raises(ValueError, match="совпадать"):
        commissural_pitch(point, point)


def test_rotation_leaves_the_midline_coordinate_alone() -> None:
    moved = rotate_about_left_right_axis(
        Point(3.0, 10.0, 5.0), 17.0, centre=Point(0.0, 0.0, 0.0)
    )

    assert moved.x == pytest.approx(3.0)
