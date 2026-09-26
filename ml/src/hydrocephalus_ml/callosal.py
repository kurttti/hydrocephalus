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
    "MAXIMUM_VERTEX_OFFSET_MILLIMETRES",
    "PLAUSIBLE_RANGE_DEGREES",
    "CallosalMeasurement",
    "Point2D",
    "crosses_midline",
    "is_plausible",
    "measure_callosal_angle",
    "roof_profile",
    "slice_spread",
]

# Сколько миллиметров крыши от средней линии брать для прямой. Двадцать — это
# примерно половина ширины бокового желудочка у взрослого: дальше стенка
# заворачивает вбок, и прямая начинает описывать изгиб, а не крышу.
DEFAULT_ROOF_SPAN_MILLIMETRES = 20.0

# Меньше трёх точек прямую не задают устойчиво: две дают её ровно, и любая
# ошибка сегментации в одном столбце уходит в результат целиком.
MINIMUM_ROOF_POINTS = 3

# Насколько вершина может отстоять от средней линии. По определению она лежит
# на ней: там сходятся крыши под мозолистым телом. У 27 субъектов AFIDs
# вершина уложилась в ±2,2 мм; клиническая серия, где желудочки слились в одну
# массу с плоской крышей, дала вершину в десятке миллиметров и угол 170° —
# число, лежащее внутри правдоподобного диапазона 30–180° и потому не
# отличимое от настоящего ничем, кроме этой проверки.
MAXIMUM_VERTEX_OFFSET_MILLIMETRES = 5.0


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

        # Уехавшая вершина означает, что прямые описывают не две симметричные
        # крыши. Угол при этом остаётся правдоподобным на вид, и отличить его
        # от настоящего больше нечем.
        if abs(vertex.x) > MAXIMUM_VERTEX_OFFSET_MILLIMETRES:
            raise ValueError(
                f"Вершина отстоит от средней линии на {vertex.x:+.1f} мм при "
                f"допустимых {MAXIMUM_VERTEX_OFFSET_MILLIMETRES:.0f}: крыши "
                f"не сходятся под мозолистым телом."
            )

    # Лучи направлены от вершины наружу: угол между ними и есть измеряемый.
    left_ray = (-1.0, -left_slope)
    right_ray = (1.0, right_slope)

    dot = left_ray[0] * right_ray[0] + left_ray[1] * right_ray[1]
    lengths = math.hypot(*left_ray) * math.hypot(*right_ray)
    degrees = math.degrees(math.acos(max(-1.0, min(1.0, dot / lengths))))

    return CallosalMeasurement(degrees, vertex, len(left), len(right))


def slice_spread(angles: Sequence[float | None]) -> float:
    """Насколько угол меняется между соседними плоскостями, в градусах.

    Угол меряется на одном срезе и сильно зависит от того, каком: измерено от
    1,2 до 11,5 градуса на миллиметр, а на одном снимке два соседних среза дали
    86,7° и 98,2° — по разные стороны от клинически значимой границы. Задняя
    спайка при этом берётся из разметки шаблона, а у пациента лежит в другом
    месте, и эта разница переходит прямо в угол.

    Поэтому размах выводится рядом с самим углом. Порога здесь намеренно нет:
    он назначается по данным, а размах измерен пока на трёх снимках, и
    назначать по трём — то же самое, что назначать на глаз. Отказы, которые в
    проекте уже есть, опираются на десятки случаев и на разрыв между годным и
    негодным; здесь такого основания ещё нет.

    Плоскости, где измерение не получилось, пропускаются: они говорят о
    покрытии, а не об устойчивости.
    """
    measured = [angle for angle in angles if angle is not None]

    if len(measured) < 2:
        raise ValueError(
            f"Размах считается минимум по двум плоскостям, измерено {len(measured)}."
        )

    return max(measured) - min(measured)


def crosses_midline(
    mask: Sequence[Sequence[bool]],
    column_positions: Sequence[float],
    reach_millimetres: float = 5.0,
) -> bool:
    """Слиты ли желудочки в одну область, пересекающую среднюю линию.

    На корональном срезе через заднюю спайку левый и правый боковые желудочки
    разделены прозрачной перегородкой и образуют две отдельные области. Если
    маска оказывается одним целым, дотягивающимся до обеих сторон, то это не
    два желудочка, и угол между их крышами не определён: крыша выходит одной
    пологой дугой, а две прямые по её половинам — почти параллельными.

    Так выглядела клиническая серия, давшая 163,7° и 170,2° в двух прогонах.
    Проверка вершины её пропускает — вершина остаётся у средней линии, — и
    отличить такое измерение от настоящего больше нечем.

    При тяжёлой гидроцефалии перегородка разрушается и желудочки сливаются
    по-настоящему. Отказ тогда верен по существу: угол мозолистого тела в его
    обычном смысле на таком срезе не измеряется.
    """
    if not mask:
        return False

    width = len(column_positions)
    height = len(mask)
    seen = [[False] * width for _ in range(height)]

    for row in range(height):
        for column in range(width):
            if not mask[row][column] or seen[row][column]:
                continue

            stack = [(row, column)]
            seen[row][column] = True
            left = right = False

            while stack:
                y, x = stack.pop()
                position = column_positions[x]
                left = left or position <= -reach_millimetres
                right = right or position >= reach_millimetres

                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < height and 0 <= nx < width \
                            and mask[ny][nx] and not seen[ny][nx]:
                        seen[ny][nx] = True
                        stack.append((ny, nx))

            if left and right:
                return True

    return False


# Правдоподобный диапазон угла, в градусах. Повторяет
# `LinearBiomarkers.CallosalAnglePlausibleRange`: измеритель в приложении
# написан на C#, исследовательский путь — здесь, и границы должны совпадать.
# Тест ниже закрепляет оба числа, чтобы расхождение было видно сразу.
#
# Верхняя граница взята по опубликованным данным, а не по геометрии: у
# контролей 112 ± 11°, по упрощённой методике 138,5 ± 5,2°, при иНТГ 66 ± 14°.
# Развёрнутый угол геометрически возможен, анатомически нет.
PLAUSIBLE_RANGE_DEGREES = (30.0, 150.0)


def is_plausible(degrees: float) -> bool:
    """Попадает ли угол в правдоподобный диапазон.

    Это проверка измерения, а не признак болезни: диагностический порог 90°
    принадлежит модели и протоколу валидации, а не измерителю.
    """
    low, high = PLAUSIBLE_RANGE_DEGREES
    return low <= degrees <= high
