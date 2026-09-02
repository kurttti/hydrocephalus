"""Тесты правила patient-level split.

Проверяется не размер выборок, а отсутствие утечки: она и есть то, ради чего
правило существует (docs/ml/README.md, «Защита от утечек»).
"""

from __future__ import annotations

import pytest

from hydrocephalus_ml.splitting import (
    SplitRatios,
    assign_splits,
    subject_counts,
)

RATIOS = SplitRatios(train=0.6, validation=0.2, test=0.2)


def _records() -> list[tuple[str, str]]:
    """Выборка, где у пациентов есть повторные и пред-/послеоперационные исследования."""
    records: list[tuple[str, str]] = []

    for index in range(50):
        subject = f"subject-{index:03d}"
        records.append((f"{subject}-study-preop", subject))
        records.append((f"{subject}-study-postop", subject))
        records.append((f"{subject}-study-followup", subject))

    return records


def test_no_subject_appears_in_more_than_one_split() -> None:
    assignment = assign_splits(_records(), RATIOS, seed=20260902)

    splits = ("train", "validation", "test")

    for left_index, left in enumerate(splits):
        for right in splits[left_index + 1 :]:
            overlap = assignment.subjects_in(left) & assignment.subjects_in(right)
            assert not overlap, f"Пациент оказался и в {left}, и в {right}: {sorted(overlap)}"


def test_all_records_of_one_subject_land_in_the_same_split() -> None:
    records = _records()
    assignment = assign_splits(records, RATIOS, seed=20260902)

    splits_per_subject: dict[str, set[str]] = {}

    for record_id, subject_id in records:
        splits_per_subject.setdefault(subject_id, set()).add(assignment.by_record[record_id])

    leaking = {subject: found for subject, found in splits_per_subject.items() if len(found) > 1}

    assert not leaking, f"Производные одного пациента разошлись по выборкам: {leaking}"


def test_assignment_is_deterministic_for_the_same_seed() -> None:
    # Seed фиксируется в манифесте запуска, поэтому повтор обязан совпадать.
    first = assign_splits(_records(), RATIOS, seed=20260902)
    second = assign_splits(_records(), RATIOS, seed=20260902)

    assert first.by_subject == second.by_subject


def test_assignment_does_not_depend_on_record_order() -> None:
    records = _records()
    shuffled = list(reversed(records))

    assert (
        assign_splits(records, RATIOS, seed=20260902).by_subject
        == assign_splits(shuffled, RATIOS, seed=20260902).by_subject
    )


def test_different_seeds_produce_a_different_assignment() -> None:
    first = assign_splits(_records(), RATIOS, seed=1)
    second = assign_splits(_records(), RATIOS, seed=2)

    assert first.by_subject != second.by_subject


def test_every_subject_receives_a_split() -> None:
    assignment = assign_splits(_records(), RATIOS, seed=20260902)
    counts = subject_counts(assignment)

    assert sum(counts.values()) == 50


def test_conflicting_subject_for_one_record_is_rejected() -> None:
    # Одна и та же запись у двух пациентов — признак сломанной дедупликации,
    # молча выбирать одного из них нельзя.
    with pytest.raises(ValueError, match="двум пациентам"):
        assign_splits(
            [("study-1", "subject-a"), ("study-1", "subject-b")],
            RATIOS,
            seed=1,
        )


@pytest.mark.parametrize(
    ("train", "validation", "test"),
    [(0.6, 0.2, 0.3), (0.5, 0.2, 0.2), (-0.1, 0.6, 0.5)],
)
def test_ratios_must_be_valid(train: float, validation: float, test: float) -> None:
    with pytest.raises(ValueError, match=r"[Дд]оли|Сумма"):
        SplitRatios(train=train, validation=validation, test=test)
