"""Сборка датасета: манифест плюс метки плюс разделение по пациентам.

Метки приходят отдельно от манифеста и соединяются по псевдонимному
идентификатору пациента. Иначе и быть не может: референсный диагноз ставится
по клиническим данным, а не по снимку, и живёт во внешнем защищённом реестре
(docs/data/README.md). В манифесте его нет.

Модуль отказывается собирать датасет, который выглядит рабочим и не является им:
при противоречивых метках одного пациента, при классе, отсутствующем в одной
из выборок, и при пустой выборке. Все три случая дают метрики, которые ничего
не значат, и ни один из них не проявляется как ошибка.

Только стандартная библиотека: модуль должен работать в CI без ML-стека.
"""

from __future__ import annotations

from collections import Counter
from collections.abc import Iterable, Mapping
from dataclasses import dataclass

from hydrocephalus_ml.manifest import DatasetManifest, SeriesRecord
from hydrocephalus_ml.splitting import SplitRatios, assign_splits

__all__ = [
    "SPLIT_NAMES",
    "DatasetError",
    "LabelledSeries",
    "PreparedDataset",
    "prepare_dataset",
    "subject_labels",
]

#: Имена выборок в том порядке, в котором они появляются в отчётах.
SPLIT_NAMES = ("train", "validation", "test")


class DatasetError(ValueError):
    """Датасет нельзя собрать в том виде, в каком его попросили."""


@dataclass(frozen=True, slots=True)
class LabelledSeries:
    """Серия вместе с меткой и назначенной выборкой."""

    series: SeriesRecord
    label: str
    split: str


@dataclass(frozen=True, slots=True)
class PreparedDataset:
    """Готовый датасет и всё, что нужно знать о его составе."""

    items: tuple[LabelledSeries, ...]
    seed: int

    #: Пациенты, для которых метки не нашлось. Они исключены, а не потеряны:
    #: их число говорит, насколько полон реестр.
    subjects_without_label: frozenset[str]

    #: Серии, отсеянные по уровню входа или постконтрастности.
    ineligible_series: int

    def in_split(self, split: str) -> tuple[LabelledSeries, ...]:
        """Возвращает записи одной выборки."""
        return tuple(item for item in self.items if item.split == split)

    def subjects_in(self, split: str) -> frozenset[str]:
        """Возвращает пациентов одной выборки."""
        return frozenset(item.series.subject_id for item in self.in_split(split))

    def label_counts(self, split: str) -> Mapping[str, int]:
        """Считает записи по классам в одной выборке."""
        return Counter(item.label for item in self.in_split(split))

    def summary(self) -> str:
        """Строит человекочитаемую сводку состава."""
        lines = [f"seed={self.seed}"]

        for split in SPLIT_NAMES:
            counts = self.label_counts(split)
            classes = ", ".join(f"{label}={counts[label]}" for label in sorted(counts))
            lines.append(
                f"{split}: пациентов {len(self.subjects_in(split))}, "
                f"серий {len(self.in_split(split))} ({classes})"
            )

        lines.append(f"без метки: пациентов {len(self.subjects_without_label)}")
        lines.append(f"не прошли отбор: серий {self.ineligible_series}")

        return "\n".join(lines)


def prepare_dataset(
    manifest: DatasetManifest,
    labels: Mapping[str, str],
    ratios: SplitRatios,
    seed: int,
    *,
    minimum_tier: str = "Baseline",
    allow_contrast: bool = False,
    require_every_class_in_every_split: bool = True,
) -> PreparedDataset:
    """Собирает датасет из манифеста и меток.

    Args:
        manifest: манифест рабочих копий.
        labels: метка по псевдонимному идентификатору пациента.
        ratios: доли train/validation/test.
        seed: seed разделения, фиксируемый в манифесте запуска.
        minimum_tier: наименьший допустимый уровень входа.
        allow_contrast: допускать ли постконтрастные серии.
        require_every_class_in_every_split: требовать присутствия каждого класса
            в каждой выборке.

    Returns:
        Готовый датасет.

    Raises:
        DatasetError: если датасет собрать нельзя.
    """
    _reject_conflicting_labels(labels)

    eligible = manifest.eligible(
        minimum_tier=minimum_tier,
        allow_contrast=allow_contrast,
    )

    ineligible = len(manifest.series) - len(eligible)

    labelled = tuple(record for record in eligible if record.subject_id in labels)

    without_label = frozenset(
        record.subject_id for record in eligible if record.subject_id not in labels
    )

    if not labelled:
        msg = "После отбора и соединения с метками не осталось ни одной серии."
        raise DatasetError(msg)

    assignment = assign_splits(
        ((record.series_id, record.subject_id) for record in labelled),
        ratios,
        seed,
    )

    items = tuple(
        LabelledSeries(
            series=record,
            label=labels[record.subject_id],
            split=assignment.by_record[record.series_id],
        )
        for record in labelled
    )

    dataset = PreparedDataset(
        items=items,
        seed=seed,
        subjects_without_label=without_label,
        ineligible_series=ineligible,
    )

    _reject_degenerate(dataset, ratios, require_every_class_in_every_split)

    return dataset


def _reject_conflicting_labels(labels: Mapping[str, str]) -> None:
    """Проверяет, что метки пригодны для соединения."""
    for subject, label in labels.items():
        if not isinstance(subject, str) or not subject.strip():
            msg = "Идентификатор пациента в метках должен быть непустой строкой."
            raise DatasetError(msg)

        if not isinstance(label, str) or not label.strip():
            # Пустая метка — это не «неизвестно», а молчаливый третий класс.
            msg = f"Метка пациента {subject} должна быть непустой строкой."
            raise DatasetError(msg)


def _reject_degenerate(
    dataset: PreparedDataset,
    ratios: SplitRatios,
    require_every_class: bool,
) -> None:
    """Отказывается от разделения, метрики которого ничего не значат."""
    requested = {
        "train": ratios.train,
        "validation": ratios.validation,
        "test": ratios.test,
    }

    present = {label for item in dataset.items for label in (item.label,)}

    for split in SPLIT_NAMES:
        if requested[split] <= 0:
            continue

        if not dataset.in_split(split):
            # Запрошенная, но пустая выборка означает, что пациентов слишком мало
            # для такого разделения. Обучение на ней пройдёт и ничего не покажет.
            msg = (
                f"Выборка {split} запрошена с долей {requested[split]}, "
                "но не получила ни одной записи."
            )
            raise DatasetError(msg)

        if not require_every_class:
            continue

        missing = present - set(dataset.label_counts(split))

        if missing:
            # Класс, отсутствующий в выборке, даёт метрики, которые выглядят
            # нормально и не измеряют ничего: по нему нечего предсказывать.
            msg = (
                f"В выборке {split} нет классов: {', '.join(sorted(missing))}. "
                "Разделение по пациентам при малой выборке не гарантирует баланс; "
                "измените seed, доли или состав выборки."
            )
            raise DatasetError(msg)


def subject_labels(pairs: Iterable[tuple[str, str]]) -> Mapping[str, str]:
    """Собирает метки из пар, отвергая противоречия.

    Один пациент с двумя разными метками — это ошибка реестра, а не повод
    выбрать одну из них: любой выбор здесь тихо подменяет референсный диагноз.
    """
    labels: dict[str, str] = {}

    for subject, label in pairs:
        existing = labels.get(subject)

        if existing is not None and existing != label:
            msg = f"Пациенту {subject} назначены разные метки: {existing!r} и {label!r}."
            raise DatasetError(msg)

        labels[subject] = label

    return labels
