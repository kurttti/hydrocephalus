"""Проверки обёртки FastSurfer, не требующие ни кода модели, ни весов.

Ни того, ни другого в репозитории нет и в CI не появляется (ADR 0009), поэтому
здесь проверяется только то, что проверяется без них: разбор аргументов до
первого обращения к чужому коду, и перечень меток желудочков.

Стек обучения в CI тоже не ставится — пакет ставится с `--no-deps`, — поэтому
модуль пропускается целиком, когда torch или numpy недоступны. Пропуск, а не
падение: отсутствие стека обучения в CI это решение, а не поломка.
"""

from __future__ import annotations

import pytest

np = pytest.importorskip("numpy")
pytest.importorskip("torch")

from hydrocephalus_ml.fastsurfer import (  # noqa: E402
    VENTRICLE_LABELS,
    segment,
    ventricle_mask,
)


def test_no_planes_is_named_rather_than_crashing_on_the_sum() -> None:
    # Пустой перечень видов оставлял вероятности несобранными, и argmax получал
    # None: падение приходило на складывании, а не на том, что складывать
    # нечего. Проверка стоит до чтения путей, поэтому установленный FastSurfer
    # этому тесту не нужен.
    with pytest.raises(ValueError, match="Ни один вид не задан"):
        segment(np.zeros((4, 4, 4), dtype=np.float32), (1.0, 1.0, 1.0), np.eye(4), planes=())


def test_the_mask_keeps_only_ventricle_labels() -> None:
    labels = np.array([[[0, 2, 4, 43, 14, 15, 5, 44]]], dtype=np.int32)

    mask = ventricle_mask(labels)

    assert mask.dtype == np.uint8
    assert mask.tolist() == [[[0, 0, 1, 1, 1, 1, 1, 1]]]


def test_no_cortex_or_white_matter_counts_as_ventricle() -> None:
    # Метки коры и белого вещества соседствуют с желудочковыми по номерам,
    # и ошибка в перечне дала бы объём на порядок больше.
    for label in (2, 3, 41, 42):
        assert label not in VENTRICLE_LABELS
