"""Сторож на входе, проверенный на реальных отказах.

Числа в тестах — измеренные, а не выдуманные: каждый негодный случай когда-то
прошёл дальше и дал неверную плоскость AC–PC, каждый годный должен пройти и
теперь. Если однажды порог захочется подвинуть, эти строки говорят, что
сломается.
"""

from __future__ import annotations

import pytest

from hydrocephalus_ml.mask_quality import inspect_mask


@pytest.mark.parametrize(
    ("millilitres", "extent", "code"),
    [
        # Снимок с неполной головой: у AFIDs sub-107 голова в кадре на 40 мм
        # короче обычной, маска вышла 810 мл, и выравнивание завалило плоскость
        # на 12 градусов — при том, что у остальных субъектов ошибка была ниже
        # двух.
        (810.0, (132.0, 119.0, 153.0), "brainMaskTooSmall"),
        # Клиническая серия, где сегментация захватила часть мозга.
        (659.0, (134.0, 134.0, 143.0), "brainMaskTooSmall"),
        # Блок из семидесяти двух срезов вместо головы.
        (90.0, (144.0, 123.0, 63.0), "brainMaskTooSmall"),
    ],
)
def test_incomplete_masks_are_refused(
    millilitres: float, extent: tuple[float, float, float], code: str
) -> None:
    problem = inspect_mask(millilitres, extent)

    assert problem is not None
    assert problem.code == code


def test_a_mask_of_scattered_fragments_is_refused_by_its_size() -> None:
    # Клиническая серия: 629 мл вещества в коробке 164x136x200 мм. Половина
    # объёма целого мозга при размахе больше целого мозга — это не мозг, а
    # разрозненные куски. Ловится и объёмом, и габаритом; здесь важно, что
    # ловится хоть чем-то.
    problem = inspect_mask(629.0, (164.0, 136.0, 200.0))

    assert problem is not None


def test_the_extent_rule_catches_what_the_volume_rule_misses() -> None:
    # Объём в пределах нормы, а размах — нет: маска расползлась за пределы
    # головы. Без отдельного правила по габаритам такой случай прошёл бы.
    problem = inspect_mask(1200.0, (164.0, 136.0, 200.0))

    assert problem is not None
    assert problem.code == "brainMaskExtentImplausible"


@pytest.mark.parametrize(
    ("millilitres", "extent"),
    [
        (965.0, (142.0, 137.0, 148.0)),   # худшая годная клиническая серия
        (1068.0, (142.0, 155.0, 144.0)),  # клиническая с низким габаритом
        (1267.0, (138.0, 145.0, 166.0)),
        (1000.0, (131.0, 134.0, 181.0)),  # AFIDs sub-113, ошибка -0,50 град
        (955.0, (125.0, 128.0, 153.0)),   # пятый процентиль нормы IXI
        (1514.0, (148.0, 149.0, 186.0)),  # самая крупная маска выборки
    ],
)
def test_masks_that_worked_are_let_through(
    millilitres: float, extent: tuple[float, float, float]
) -> None:
    assert inspect_mask(millilitres, extent) is None


def test_the_refusal_says_what_is_wrong_in_words() -> None:
    # Код нужен разбору, объяснение — человеку: отказ без причины неотличим от
    # поломки, и его начинают обходить.
    problem = inspect_mask(810.0, (132.0, 119.0, 153.0))

    assert problem is not None
    assert "810" in problem.explanation
    assert problem.explanation != problem.code


def test_extent_must_have_three_axes() -> None:
    with pytest.raises(ValueError, match="тремя"):
        inspect_mask(1200.0, (140.0, 140.0))  # type: ignore[arg-type]
