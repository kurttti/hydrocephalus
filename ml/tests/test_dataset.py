"""Проверка сборки датасета.

Главное здесь — отказы. Датасет с утечкой между выборками, с классом,
отсутствующим в одной из них, или с противоречивой меткой пациента выглядит
рабочим и даёт метрики, которые ничего не значат.
"""

from __future__ import annotations

import pytest

from hydrocephalus_ml.dataset import (
    SPLIT_NAMES,
    DatasetError,
    prepare_dataset,
    subject_labels,
)
from hydrocephalus_ml.manifest import (
    SUPPORTED_SCHEMA_VERSION,
    DatasetManifest,
    parse_manifest,
)
from hydrocephalus_ml.splitting import SplitRatios

RATIOS = SplitRatios(train=0.6, validation=0.2, test=0.2)


def build_manifest(subjects: int, series_per_subject: int = 2) -> DatasetManifest:
    records = [
        {
            "series_id": f"series-{subject}-{index}",
            "study_id": f"study-{subject}-{index}",
            "subject_id": f"subject-{subject:03d}",
            "tier": "Extended",
            "weighting": "T1",
            "contrast_enhanced": False,
            "slice_count": 180,
            "voxel_spacing_mm": [1.0, 1.0, 1.0],
        }
        for subject in range(subjects)
        for index in range(series_per_subject)
    ]

    return parse_manifest({"schema_version": SUPPORTED_SCHEMA_VERSION, "series": records})


def alternating_labels(subjects: int) -> dict[str, str]:
    return {
        f"subject-{subject:03d}": "iNPH_pattern" if subject % 2 == 0 else "non_iNPH"
        for subject in range(subjects)
    }


def test_every_series_of_a_subject_lands_in_one_split() -> None:
    # Правило, ради которого всё и делается: повторные исследования одного
    # человека в разных выборках завышают метрики за счёт утечки.
    manifest = build_manifest(subjects=60, series_per_subject=3)

    dataset = prepare_dataset(manifest, alternating_labels(60), RATIOS, seed=7)

    by_subject: dict[str, set[str]] = {}

    for item in dataset.items:
        by_subject.setdefault(item.series.subject_id, set()).add(item.split)

    assert all(len(splits) == 1 for splits in by_subject.values())


def test_no_subject_appears_in_two_splits() -> None:
    manifest = build_manifest(subjects=60)

    dataset = prepare_dataset(manifest, alternating_labels(60), RATIOS, seed=7)

    seen: set[str] = set()

    for split in SPLIT_NAMES:
        subjects = dataset.subjects_in(split)

        assert not (subjects & seen)

        seen |= subjects


def test_the_split_is_reproducible_for_one_seed() -> None:
    manifest = build_manifest(subjects=40)
    labels = alternating_labels(40)

    first = prepare_dataset(manifest, labels, RATIOS, seed=11)
    again = prepare_dataset(manifest, labels, RATIOS, seed=11)

    assert [(item.series.series_id, item.split) for item in first.items] == [
        (item.series.series_id, item.split) for item in again.items
    ]


def test_subjects_without_a_label_are_excluded_and_counted() -> None:
    # Исключены, а не потеряны: их число говорит, насколько полон реестр.
    manifest = build_manifest(subjects=40)
    labels = alternating_labels(40)
    del labels["subject-000"]
    del labels["subject-001"]

    dataset = prepare_dataset(manifest, labels, RATIOS, seed=3)

    assert dataset.subjects_without_label == frozenset({"subject-000", "subject-001"})
    assert all(
        item.series.subject_id not in dataset.subjects_without_label for item in dataset.items
    )


def test_series_that_fail_the_filter_are_counted() -> None:
    records = [
        {
            "series_id": f"series-{index}",
            "study_id": "study-1",
            "subject_id": f"subject-{index:03d}",
            "tier": "Extended" if index % 2 == 0 else "Unusable",
            "weighting": "T1",
            "contrast_enhanced": False,
            "slice_count": 180,
            "voxel_spacing_mm": [1.0, 1.0, 1.0],
        }
        for index in range(40)
    ]

    manifest = parse_manifest({"schema_version": SUPPORTED_SCHEMA_VERSION, "series": records})

    dataset = prepare_dataset(manifest, alternating_labels(40), RATIOS, seed=5)

    assert dataset.ineligible_series == 20


def test_a_class_missing_from_a_split_is_refused() -> None:
    # Метрики по такой выборке выглядят нормально и не измеряют ничего.
    manifest = build_manifest(subjects=40)
    labels = {f"subject-{subject:03d}": "iNPH_pattern" for subject in range(40)}
    labels["subject-000"] = "non_iNPH"

    with pytest.raises(DatasetError, match="нет классов"):
        prepare_dataset(manifest, labels, RATIOS, seed=1)


def test_the_class_requirement_can_be_lifted_deliberately() -> None:
    manifest = build_manifest(subjects=40)
    labels = {f"subject-{subject:03d}": "iNPH_pattern" for subject in range(40)}
    labels["subject-000"] = "non_iNPH"

    dataset = prepare_dataset(
        manifest,
        labels,
        RATIOS,
        seed=1,
        require_every_class_in_every_split=False,
    )

    assert dataset.items


def test_an_empty_requested_split_is_refused() -> None:
    # Пациентов слишком мало для такого разделения; обучение прошло бы
    # и ничего не показало.
    manifest = build_manifest(subjects=2, series_per_subject=1)
    labels = {"subject-000": "iNPH_pattern", "subject-001": "non_iNPH"}

    with pytest.raises(DatasetError):
        prepare_dataset(manifest, labels, RATIOS, seed=1)


def test_a_dataset_without_any_labelled_series_is_refused() -> None:
    manifest = build_manifest(subjects=10)

    with pytest.raises(DatasetError, match="не осталось"):
        prepare_dataset(manifest, {}, RATIOS, seed=1)


def test_an_empty_label_is_refused() -> None:
    # Пустая метка — не «неизвестно», а молчаливый третий класс.
    manifest = build_manifest(subjects=10)

    with pytest.raises(DatasetError, match="непустой строкой"):
        prepare_dataset(manifest, {"subject-000": "  "}, RATIOS, seed=1)


def test_conflicting_labels_for_one_subject_are_refused() -> None:
    # Любой выбор здесь тихо подменяет референсный диагноз.
    with pytest.raises(DatasetError, match="разные метки"):
        subject_labels(
            [
                ("subject-000", "iNPH_pattern"),
                ("subject-000", "non_iNPH"),
            ]
        )


def test_repeating_the_same_label_is_not_a_conflict() -> None:
    labels = subject_labels([("subject-000", "iNPH_pattern"), ("subject-000", "iNPH_pattern")])

    assert labels == {"subject-000": "iNPH_pattern"}


def test_the_summary_reports_composition_of_every_split() -> None:
    manifest = build_manifest(subjects=60)

    dataset = prepare_dataset(manifest, alternating_labels(60), RATIOS, seed=7)

    summary = dataset.summary()

    for split in SPLIT_NAMES:
        assert split in summary

    assert "seed=7" in summary
    assert "без метки" in summary
