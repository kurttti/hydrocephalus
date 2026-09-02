"""Разделение выборки на уровне пациента.

Главное правило (docs/ml/README.md, раздел «Защита от утечек»): все производные
одного пациента принадлежат одному split. Повторные исследования, пред- и
послеоперационные серии одного человека не могут оказаться в разных выборках,
иначе метрики завышаются за счёт утечки.

Модуль намеренно использует только стандартную библиотеку: он не должен требовать
ни pandas, ни numpy, ни torch. Это делает его быстрым, тестируемым и пригодным
для запуска в CI без ML-стека. Не добавляйте сюда зависимости.
"""

from __future__ import annotations

import hashlib
from collections import Counter
from collections.abc import Iterable, Mapping
from dataclasses import dataclass

__all__ = ["SplitAssignment", "SplitRatios", "assign_splits"]


@dataclass(frozen=True, slots=True)
class SplitRatios:
    """Доли выборок. Сумма должна равняться единице."""

    train: float
    validation: float
    test: float

    def __post_init__(self) -> None:
        total = self.train + self.validation + self.test

        if not all(value >= 0 for value in (self.train, self.validation, self.test)):
            msg = "Доли выборок не могут быть отрицательными."
            raise ValueError(msg)

        if abs(total - 1.0) > 1e-9:
            msg = f"Сумма долей должна равняться 1.0, получено {total}."
            raise ValueError(msg)


@dataclass(frozen=True, slots=True)
class SplitAssignment:
    """Результат разделения: имя выборки для каждого пациента и для каждой записи."""

    by_subject: Mapping[str, str]
    by_record: Mapping[str, str]

    def subjects_in(self, split: str) -> frozenset[str]:
        """Возвращает пациентов, попавших в указанную выборку."""
        return frozenset(
            subject for subject, assigned in self.by_subject.items() if assigned == split
        )


def _bucket(subject_id: str, seed: int) -> float:
    """Детерминированно отображает пациента в число из полуинтервала [0, 1).

    Хеш, а не генератор случайных чисел: назначение обязано быть воспроизводимым
    при том же seed и не зависеть от порядка поступления записей.
    """
    digest = hashlib.sha256(f"{seed}:{subject_id}".encode()).digest()
    return int.from_bytes(digest[:8], "big") / float(1 << 64)


def assign_splits(
    records: Iterable[tuple[str, str]],
    ratios: SplitRatios,
    seed: int,
) -> SplitAssignment:
    """Назначает split каждой записи по её пациенту.

    Args:
        records: пары «идентификатор записи, псевдонимный идентификатор пациента».
            Записью может быть исследование, серия или любой производный артефакт.
        ratios: доли train/validation/test.
        seed: seed, фиксируемый в манифесте запуска ради воспроизводимости.

    Returns:
        Назначение выборок по пациентам и по записям.

    Raises:
        ValueError: если один идентификатор записи встречается с разными пациентами.
    """
    subject_of_record: dict[str, str] = {}

    for record_id, subject_id in records:
        previous = subject_of_record.get(record_id)

        if previous is not None and previous != subject_id:
            msg = (
                f"Запись '{record_id}' отнесена сразу к двум пациентам: "
                f"'{previous}' и '{subject_id}'."
            )
            raise ValueError(msg)

        subject_of_record[record_id] = subject_id

    # Split назначается пациенту, а не записи: это и есть защита от утечки.
    by_subject = {
        subject_id: _split_for(_bucket(subject_id, seed), ratios)
        for subject_id in sorted(set(subject_of_record.values()))
    }

    by_record = {
        record_id: by_subject[subject_id] for record_id, subject_id in subject_of_record.items()
    }

    return SplitAssignment(by_subject=by_subject, by_record=by_record)


def _split_for(position: float, ratios: SplitRatios) -> str:
    if position < ratios.train:
        return "train"

    if position < ratios.train + ratios.validation:
        return "validation"

    return "test"


def subject_counts(assignment: SplitAssignment) -> Mapping[str, int]:
    """Считает число пациентов в каждой выборке — для отчёта о составе."""
    return Counter(assignment.by_subject.values())
