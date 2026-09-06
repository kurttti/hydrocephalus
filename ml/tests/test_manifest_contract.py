"""Договор о манифесте между приложением и ML-контуром.

`data/example-manifest.json` — общий эталон: его читает этот тест, и с ним же
сверяется тест на стороне .NET. Схема — единственное место, где два языка
обязаны совпадать, и разойтись они могут молча: приложение продолжит писать,
пакет продолжит читать, а поля будут значить разное.

Проверка структурная, а не побайтовая: отступы и порядок ключей у двух
сериализаторов различаются законно, а вот состав полей и их смысл — нет.
"""

from __future__ import annotations

from pathlib import Path

from hydrocephalus_ml.dataset import prepare_dataset
from hydrocephalus_ml.manifest import SUPPORTED_SCHEMA_VERSION, load_manifest
from hydrocephalus_ml.splitting import SplitRatios

EXAMPLE = Path(__file__).parent / "data" / "example-manifest.json"


def test_the_example_written_by_the_application_parses() -> None:
    manifest = load_manifest(EXAMPLE)

    assert manifest.schema_version == SUPPORTED_SCHEMA_VERSION
    assert len(manifest.series) == 3


def test_the_example_carries_the_documented_fields() -> None:
    record = load_manifest(EXAMPLE).series[0]

    assert record.series_id == "9f2c41a7be03d5e6"
    assert record.subject_id == "c40e8b31d7a5f9e2"
    assert record.tier == "Extended"
    assert record.weighting == "T1"
    assert record.contrast_enhanced is False
    assert record.slice_count == 176
    assert record.voxel_spacing_mm == (0.9, 0.9, 1.0)


def test_the_example_contains_no_identifying_value() -> None:
    # Манифест уходит в исследовательский контур: исходных UID и имён в нём
    # быть не может. Проверка по тексту, а не по полям: утечка попала бы
    # в любое из них.
    text = EXAMPLE.read_text(encoding="utf-8")

    assert "1.2." not in text
    assert "PatientName" not in text
    assert "label" not in text


def test_a_gapped_series_keeps_its_slice_step() -> None:
    # 6мм при 24 срезах — рутинная 2D-серия выборки. Шаг, а не толщина:
    # объём по толщине был бы занижен на долю неполученной ткани.
    gapped = load_manifest(EXAMPLE).series[1]

    assert gapped.tier == "Baseline"
    assert gapped.voxel_spacing_mm[2] == 6.0


def test_the_example_yields_a_usable_dataset() -> None:
    # Сквозная проверка: манифест приложения проходит весь путь до разделения.
    manifest = load_manifest(EXAMPLE)

    labels = {
        "c40e8b31d7a5f9e2": "iNPH_pattern",
        "70b1d4e8c25f9a36": "non_iNPH",
    }

    dataset = prepare_dataset(
        manifest,
        labels,
        SplitRatios(train=1.0, validation=0.0, test=0.0),
        seed=1,
    )

    # Постконтрастная серия отсеяна: в MRI-only конвейер она не подаётся.
    assert dataset.ineligible_series == 1
    assert {item.series.series_id for item in dataset.items} == {
        "9f2c41a7be03d5e6",
        "3d81f0a94c6b2e57",
    }
