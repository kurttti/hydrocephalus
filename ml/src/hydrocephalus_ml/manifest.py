"""Манифест датасета: что доступно для обучения.

Манифест **создаётся приложением на .NET**, а не собирается здесь. Разбор DICOM,
вывод геометрии и уровня входа, псевдонимизация — всё это уже реализовано
и покрыто тестами в `Hydrocephalus.Infrastructure`. Второй разбор на Python
означал бы две реализации, которые могут разойтись, и датасет, построенный
на геометрии, отличной от той, по которой приложение измеряет. Расхождение
проявилось бы не сбоем, а метриками, которые нельзя перенести на продукт.

Здесь манифест только читается и строго проверяется: молча принять запись
с пропущенным полем значит получить датасет, о составе которого никто не знает.

Модуль использует только стандартную библиотеку: он должен работать в CI
без ML-стека. Не добавляйте сюда зависимости.
"""

from __future__ import annotations

import json
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

__all__ = [
    "SUPPORTED_SCHEMA_VERSION",
    "DatasetManifest",
    "ManifestError",
    "SeriesRecord",
    "load_manifest",
    "parse_manifest",
]

#: Версия схемы, которую понимает этот модуль.
SUPPORTED_SCHEMA_VERSION = "1.0.0"

#: Уровни входа в порядке возрастания. Порядок значим: он задаёт сравнение.
_TIERS = ("Unusable", "Baseline", "Extended")

_REQUIRED_SERIES_FIELDS = frozenset(
    {
        "series_id",
        "study_id",
        "subject_id",
        "tier",
        "weighting",
        "contrast_enhanced",
        "slice_count",
        "voxel_spacing_mm",
    }
)


class ManifestError(ValueError):
    """Манифест не соответствует схеме."""


@dataclass(frozen=True, slots=True)
class SeriesRecord:
    """Одна серия рабочей копии.

    Все идентификаторы псевдонимные: манифест уходит в ML-контур, и исходных
    UID или имён в нём быть не может (docs/data/README.md).
    """

    series_id: str
    study_id: str
    subject_id: str
    tier: str
    weighting: str
    contrast_enhanced: bool
    slice_count: int
    voxel_spacing_mm: tuple[float, float, float]

    @property
    def tier_rank(self) -> int:
        """Порядковый номер уровня входа."""
        return _TIERS.index(self.tier)

    def at_least(self, tier: str) -> bool:
        """Проверяет, что уровень входа не ниже указанного."""
        if tier not in _TIERS:
            msg = f"Неизвестный уровень входа: {tier!r}."
            raise ManifestError(msg)

        return self.tier_rank >= _TIERS.index(tier)


@dataclass(frozen=True, slots=True)
class DatasetManifest:
    """Полный манифест: версия схемы и серии."""

    schema_version: str
    series: tuple[SeriesRecord, ...]

    def subjects(self) -> frozenset[str]:
        """Возвращает псевдонимные идентификаторы пациентов."""
        return frozenset(record.subject_id for record in self.series)

    def eligible(
        self,
        *,
        minimum_tier: str = "Baseline",
        allow_contrast: bool = False,
    ) -> tuple[SeriesRecord, ...]:
        """Отбирает серии, пригодные для обучения.

        Постконтрастные серии исключаются по умолчанию: в MRI-only конвейер они
        не подаются ни на одном уровне входа, и в обучающей выборке им тоже
        не место (docs/clinical/README.md).
        """
        return tuple(
            record
            for record in self.series
            if record.at_least(minimum_tier) and (allow_contrast or not record.contrast_enhanced)
        )


def load_manifest(path: Path | str) -> DatasetManifest:
    """Читает манифест из файла."""
    text = Path(path).read_text(encoding="utf-8")

    return parse_manifest(json.loads(text))


def parse_manifest(payload: Any) -> DatasetManifest:
    """Разбирает и проверяет манифест."""
    if not isinstance(payload, Mapping):
        msg = "Манифест должен быть объектом JSON."
        raise ManifestError(msg)

    version = payload.get("schema_version")

    if version != SUPPORTED_SCHEMA_VERSION:
        # Молча читать чужую версию нельзя: поля могли поменять смысл,
        # а датасет, собранный по неверно понятой схеме, выглядит нормально.
        msg = f"Ожидалась версия схемы {SUPPORTED_SCHEMA_VERSION}, получена {version!r}."
        raise ManifestError(msg)

    raw_series = payload.get("series")

    if not isinstance(raw_series, Sequence) or isinstance(raw_series, str | bytes):
        msg = "Поле series должно быть списком."
        raise ManifestError(msg)

    records = tuple(_parse_series(item) for item in raw_series)

    _reject_duplicates(records)

    return DatasetManifest(schema_version=version, series=records)


def _parse_series(item: Any) -> SeriesRecord:
    if not isinstance(item, Mapping):
        msg = "Каждая запись series должна быть объектом."
        raise ManifestError(msg)

    missing = _REQUIRED_SERIES_FIELDS - set(item)

    if missing:
        msg = f"В записи серии отсутствуют поля: {', '.join(sorted(missing))}."
        raise ManifestError(msg)

    tier = _require_str(item, "tier")

    if tier not in _TIERS:
        msg = f"Неизвестный уровень входа: {tier!r}."
        raise ManifestError(msg)

    spacing = item["voxel_spacing_mm"]

    if not isinstance(spacing, Sequence) or isinstance(spacing, str | bytes) or len(spacing) != 3:
        msg = "Поле voxel_spacing_mm должно содержать три числа."
        raise ManifestError(msg)

    values = tuple(float(value) for value in spacing)

    if any(value <= 0 for value in values):
        # Неположительный шаг означает непригодную геометрию; такую серию
        # нельзя ни измерить, ни обучить на ней.
        msg = f"Шаг вокселя должен быть положительным, получено {values}."
        raise ManifestError(msg)

    slice_count = item["slice_count"]

    if not isinstance(slice_count, int) or isinstance(slice_count, bool) or slice_count <= 0:
        msg = f"Число срезов должно быть положительным целым, получено {slice_count!r}."
        raise ManifestError(msg)

    contrast = item["contrast_enhanced"]

    if not isinstance(contrast, bool):
        msg = "Поле contrast_enhanced должно быть логическим."
        raise ManifestError(msg)

    return SeriesRecord(
        series_id=_require_str(item, "series_id"),
        study_id=_require_str(item, "study_id"),
        subject_id=_require_str(item, "subject_id"),
        tier=tier,
        weighting=_require_str(item, "weighting"),
        contrast_enhanced=contrast,
        slice_count=slice_count,
        voxel_spacing_mm=(values[0], values[1], values[2]),
    )


def _require_str(item: Mapping[str, Any], field: str) -> str:
    value = item[field]

    if not isinstance(value, str) or not value.strip():
        msg = f"Поле {field} должно быть непустой строкой."
        raise ManifestError(msg)

    return value


def _reject_duplicates(records: Iterable[SeriesRecord]) -> None:
    seen: set[str] = set()

    for record in records:
        if record.series_id in seen:
            # Дубликат означает, что одна серия попадёт в обучение дважды
            # и получит вдвое больший вес — незаметно и без всякого повода.
            msg = f"Серия {record.series_id} встречается в манифесте дважды."
            raise ManifestError(msg)

        seen.add(record.series_id)
