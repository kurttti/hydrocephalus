"""Проверка разбора манифеста датасета.

Манифест приходит извне, и молча принятая неверная запись даёт датасет,
о составе которого никто не знает. Поэтому проверяется не только успешный
разбор, но и каждый отказ.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from hydrocephalus_ml.manifest import (
    SUPPORTED_SCHEMA_VERSION,
    ManifestError,
    load_manifest,
    parse_manifest,
)


def series(**overrides: object) -> dict[str, object]:
    record: dict[str, object] = {
        "series_id": "series-1",
        "study_id": "study-1",
        "subject_id": "subject-1",
        "tier": "Extended",
        "weighting": "T1",
        "contrast_enhanced": False,
        "slice_count": 180,
        "voxel_spacing_mm": [1.0, 1.0, 1.0],
    }
    record.update(overrides)
    return record


def manifest(*records: dict[str, object]) -> dict[str, object]:
    return {
        "schema_version": SUPPORTED_SCHEMA_VERSION,
        "series": list(records) or [series()],
    }


def test_a_valid_manifest_parses() -> None:
    parsed = parse_manifest(manifest())

    assert len(parsed.series) == 1
    assert parsed.series[0].subject_id == "subject-1"
    assert parsed.subjects() == frozenset({"subject-1"})


def test_manifest_is_read_from_a_file(tmp_path: Path) -> None:
    path = tmp_path / "manifest.json"
    path.write_text(json.dumps(manifest()), encoding="utf-8")

    assert len(load_manifest(path).series) == 1


def test_another_schema_version_is_refused() -> None:
    # Поля могли поменять смысл, а датасет по неверно понятой схеме
    # выглядит нормально.
    payload = manifest()
    payload["schema_version"] = "2.0.0"

    with pytest.raises(ManifestError, match="версия схемы"):
        parse_manifest(payload)


@pytest.mark.parametrize(
    "field",
    [
        "series_id",
        "study_id",
        "subject_id",
        "tier",
        "weighting",
        "contrast_enhanced",
        "slice_count",
        "voxel_spacing_mm",
    ],
)
def test_a_missing_field_is_refused(field: str) -> None:
    record = series()
    del record[field]

    with pytest.raises(ManifestError, match="отсутствуют поля"):
        parse_manifest(manifest(record))


def test_a_duplicate_series_is_refused() -> None:
    # Дубликат попал бы в обучение дважды и получил вдвое больший вес.
    with pytest.raises(ManifestError, match="дважды"):
        parse_manifest(manifest(series(), series()))


def test_an_unknown_tier_is_refused() -> None:
    with pytest.raises(ManifestError, match="уровень входа"):
        parse_manifest(manifest(series(tier="Excellent")))


@pytest.mark.parametrize("spacing", [[1.0, 1.0], [0.0, 1.0, 1.0], [-1.0, 1.0, 1.0]])
def test_an_implausible_voxel_spacing_is_refused(spacing: list[float]) -> None:
    with pytest.raises(ManifestError):
        parse_manifest(manifest(series(voxel_spacing_mm=spacing)))


@pytest.mark.parametrize("count", [0, -5, "180", True])
def test_an_implausible_slice_count_is_refused(count: object) -> None:
    with pytest.raises(ManifestError, match="срезов"):
        parse_manifest(manifest(series(slice_count=count)))


def test_an_empty_identifier_is_refused() -> None:
    with pytest.raises(ManifestError, match="непустой строкой"):
        parse_manifest(manifest(series(subject_id="   ")))


def test_tier_comparison_follows_the_documented_order() -> None:
    parsed = parse_manifest(
        manifest(series(series_id="a", tier="Baseline"), series(series_id="b", tier="Extended"))
    )

    baseline, extended = parsed.series

    assert baseline.at_least("Baseline")
    assert not baseline.at_least("Extended")
    assert extended.at_least("Baseline")


def test_contrast_enhanced_series_are_excluded_by_default() -> None:
    # В MRI-only конвейер они не подаются ни на одном уровне входа,
    # и в обучающей выборке им тоже не место.
    parsed = parse_manifest(
        manifest(
            series(series_id="plain"),
            series(series_id="post", contrast_enhanced=True),
        )
    )

    eligible = parsed.eligible()

    assert [record.series_id for record in eligible] == ["plain"]
    assert len(parsed.eligible(allow_contrast=True)) == 2


def test_the_minimum_tier_filters_the_selection() -> None:
    parsed = parse_manifest(
        manifest(
            series(series_id="thin", tier="Extended"),
            series(series_id="thick", tier="Baseline"),
            series(series_id="broken", tier="Unusable"),
        )
    )

    assert len(parsed.eligible(minimum_tier="Baseline")) == 2
    assert len(parsed.eligible(minimum_tier="Extended")) == 1
