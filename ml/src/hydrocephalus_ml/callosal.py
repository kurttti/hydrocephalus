"""Угол мозолистого тела по маске желудочков на корональном срезе.

Угол измеряется между крышами боковых желудочков на корональной плоскости,
перпендикулярной линии AC–PC, на уровне задней спайки. Плоскость даёт
`hydrocephalus_ml.acpc`; здесь — то, что происходит уже на срезе.

Сам угол считается в `Hydrocephalus.Inference.Measurements.LinearBiomarkers` по
трём точкам. Не хватало способа поставить эти точки без врача, и модуль
закрывает именно это: из маски желудочков получаются два луча и вершина.

Две вещи задаются явно, потому что меняют результат, а в публикациях описаны
словами. Первая — сколько крыши брать для прямой: линия проводится вдоль
верхнемедиальной стенки, и чем дальше от средней линии её тянуть, тем сильнее
на неё влияет боковой изгиб. Вторая — что считать крышей: верхняя точка маски в
каждом столбце, а не её граница целиком.

Модуль обходится стандартной библиотекой: срез приходит решёткой логических
значений, и это позволяет проверять его синтетическими масками, без снимков и
без ML-стека.
"""

from __future__ import annotations

import math
from collections.abc import Sequence
from typing import NamedTuple

__all__ = [
    "DEFAULT_ROOF_SPAN_MILLIMETRES",
    "CallosalMeasurement",
    "Point2D",
    "measure_callosal_angle",
    "roof_profile",
]

# Сколько миллиметров крыши от средней линии брать для прямой. Двадцать — это
# примерно половина ширины бокового желудочка у взрослого: дальше стенка
# заворачивает вбок, и прямая начинает описывать изгиб, а не крышу.
DEFAULT_ROOF_SPAN_MILLIMETRES = 20.0

# Меньше трёх точек прямую не задают устойчиво: две дают её ровно, и любая
# ошибка сегментации в одном столбце уходит в результат целиком.
MINIMUM_ROOF_POINTS = 3


class Point2D(NamedTuple):
    """Точка на корональном срезе, в миллиметрах.

    ``x`` — влево-вправо, ноль на средней линии; ``y`` — вверх.
    """

    x: float
    y: float


class CallosalMeasurement(NamedTuple):
    """Результат: угол, вершина и по сколько точек лёг каждый луч.

    Вершина отсутствует, когда крыши параллельны: угол при этом определён и
    равен 180°, а точки пересечения нет. Отказывать в таком случае было бы
    неверно — это очень широкие желудочки, а не негодное измерение.
    """

    degrees: float
    vertex: Point2D | None
    left_points: int
    right_points: int


def roof_profile(
    mask: Sequence[Sequence[bool]],
    column_positions: Sequence[float],
    row_positions: Sequence[float],
) -> list[Point2D]:
    """Верхняя граница маски: по одной точке на столбец.

    ``column_positions`` и ``row_positions`` задают координаты решётки в
    миллиметрах, поэтому шаг вокселя может быть любым и неодинаковым по осям.
    """
    if len(mask) != len(row_positions):
        raise ValueError("Число строк маски и координат строк не совпадает.")

    points: list[Point2D] = []

    for column, x in enumerate(column_positions):
        highest: float | None = None

        for row, y in zip(range(len(mask)), row_positions, strict=True):
            if column >= len(mask[row]):
                raise ValueError("Строки маски разной длины.")
            if mask[row][column] and (highest is None or y > highest):
                highest = y

        if highest is not None:
            points.append(Point2D(x, highest))

    return points


def _fit_line(points: Sequence[Point2D]) -> tuple[float, float]:
    """Прямая ``y = slope * x + intercept`` методом наименьших квадратов."""
    count = len(points)
    mean_x = sum(point.x for point in points) / count
    mean_y = sum(point.y for point in points) / count
    variance = sum((point.x - mean_x) ** 2 for point in points)

    if variance == 0.0:
        raise ValueError("Точки крыши лежат в одном столбце: прямая не определена.")

    covariance = sum((point.x - mean_x) * (point.y - mean_y) for point in points)
    slope = covariance / variance

    return slope, mean_y - slope * mean_x


def measure_callosal_angle(
    roof: Sequence[Point2D],
    span_millimetres: float = DEFAULT_ROOF_SPAN_MILLIMETRES,
) -> CallosalMeasurement:
    """Угол между крышами боковых желудочков, в градусах.

    Угол берётся верхний — тот, что раскрыт кверху, как его и рисуют: при иНТГ
    он острый, у здоровых тупой. Считается между лучами, идущими от вершины
    наружу вдоль каждой крыши.
    """
    if span_millimetres <= 0.0:
        raise ValueError("Ширина участка крыши должна быть положительной.")

    left = [point for point in roof if -span_millimetres <= point.x < 0.0]
    right = [point for point in roof if 0.0 < point.x <= span_millimetres]

    if len(left) < MINIMUM_ROOF_POINTS or len(right) < MINIMUM_ROOF_POINTS:
        raise ValueError(
            f"Крыша найдена не с обеих сторон: слева {len(left)}, справа {len(right)} "
            f"точек при необходимых {MINIMUM_ROOF_POINTS}."
        )

    left_slope, left_intercept = _fit_line(left)
    right_slope, right_intercept = _fit_line(right)

    # Вершина — пересечение прямых, а не верхняя точка маски: у расширенных
    # желудочков крыши сходятся выше, чем идёт сама маска. Параллельные крыши
    # вершины не дают, но углу она и не нужна.
    if left_slope == right_slope:
        vertex = None
    else:
        vertex_x = (right_intercept - left_intercept) / (left_slope - right_slope)
        vertex = Point2D(vertex_x, left_slope * vertex_x + left_intercept)

    # Лучи направлены от вершины наружу: угол между ними и есть измеряемый.
    left_ray = (-1.0, -left_slope)
    right_ray = (1.0, right_slope)

    dot = left_ray[0] * right_ray[0] + left_ray[1] * right_ray[1]
    lengths = math.hypot(*left_ray) * math.hypot(*right_ray)
    degrees = math.degrees(math.acos(max(-1.0, min(1.0, dot / lengths))))

    return CallosalMeasurement(degrees, vertex, len(left), len(right))
