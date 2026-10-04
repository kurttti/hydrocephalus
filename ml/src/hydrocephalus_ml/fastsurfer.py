"""Запуск предобученной модели FastSurfer VINN в исследовательском контуре.

Модель принята решением ADR 0009 первой обученной моделью сегментации
желудочков: это единственный кандидат, у которого и код, и веса под Apache-2.0.
Здесь она только запускается — ни код, ни веса в репозиторий не входят, и в
клиническую поставку Python не попадает (ADR 0002). Результат приходит в
приложение маской в формате NIfTI.

Веса и исходный код лежат на машине владельца данных вне репозитория; пути
задаются вызывающим, по умолчанию берутся из переменных окружения.

Сеть двумерная с толстым входом: `NUM_CHANNELS: 7` — семь соседних срезов при
базовом разрешении 1 мм. Для серий толще этого семь срезов охватывают 35–49 мм
анатомии, что заведомо вне обучающего распределения, поэтому толстые серии
приводятся к 1 мм до запуска, а не подаются как есть.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import torch

#: Метки желудочков в разметке FreeSurfer, которую выдаёт модель.
#: Боковые желудочки, нижние рога, III и IV, плюс межжелудочковые пути.
VENTRICLE_LABELS: tuple[int, ...] = (
    4,  # левый боковой
    5,  # левый нижний рог
    14,  # III
    15,  # IV
    43,  # правый боковой
    44,  # правый нижний рог
    72,  # пятый желудочек (полость прозрачной перегородки)
)


def _code_root(explicit: str | Path | None = None) -> Path:
    """Корень исходного кода FastSurfer."""
    root = explicit or os.environ.get("FASTSURFER_CODE")

    if root is None:
        raise RuntimeError(
            "Не задан путь к коду FastSurfer: переменная FASTSURFER_CODE "
            "или аргумент code_root."
        )

    path = Path(root)

    if not (path / "FastSurferCNN").is_dir():
        raise RuntimeError(f"В {path} нет каталога FastSurferCNN.")

    return path


def _weights(plane: str, explicit: str | Path | None = None) -> Path:
    """Файл весов для одного вида."""
    root = explicit or os.environ.get("FASTSURFER_WEIGHTS")

    if root is None:
        raise RuntimeError(
            "Не задан путь к весам FastSurfer: переменная FASTSURFER_WEIGHTS "
            "или аргумент weights_root."
        )

    path = Path(root) / f"aparc_vinn_{plane}_v2.0.0.pkl"

    if not path.is_file():
        raise RuntimeError(f"Нет файла весов {path}.")

    return path


def load_model(
    plane: str = "axial",
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
) -> torch.nn.Module:
    """Собирает сеть одного вида и загружает в неё опубликованные веса.

    Архитектура берётся из кода FastSurfer, а не восстанавливается по именам
    весов: расхождение здесь дало бы молча неверную разметку.

    Parameters
    ----------
    plane : str
        Вид: ``axial``, ``coronal`` либо ``sagittal``.
    code_root : str | Path | None
        Корень исходного кода FastSurfer.
    weights_root : str | Path | None
        Каталог с файлами весов.

    Returns
    -------
    torch.nn.Module
        Сеть в режиме вывода.
    """
    if plane not in ("axial", "coronal", "sagittal"):
        raise ValueError(f"Неизвестный вид: {plane}")

    root = _code_root(code_root)

    if str(root) not in sys.path:
        sys.path.insert(0, str(root))

    from FastSurferCNN.models.networks import build_model  # noqa: PLC0415
    from FastSurferCNN.utils.load_config import get_config  # noqa: PLC0415

    class _Args:
        cfg_file = str(root / "FastSurferCNN" / "config" / f"FastSurferVINN_{plane}.yaml")
        opts: list[str] = []

    cfg = get_config(_Args())
    model = build_model(cfg)

    checkpoint = torch.load(_weights(plane, weights_root), map_location="cpu", weights_only=False)
    model.load_state_dict(checkpoint["model_state"])
    model.eval()

    return model


def segment(
    volume,
    zooms,
    affine,
    planes: tuple[str, ...] = ("axial",),
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
):
    """Размечает объём сетью FastSurfer и возвращает метки FreeSurfer.

    Повторяет порядок действий из ``run_prediction.py``: приведение к укладке
    LIA, прогон выбранных видов с накоплением вероятностей, выбор класса,
    возврат к исходной укладке и перевод номеров классов в метки FreeSurfer.
    Отличие одно — виды выбираются вызывающим.

    Зачем выбирать. Полная модель складывает три вида, и для этого нужен
    изотропный объём около миллиметра: такого в выборке 12 серий из 58. Ещё 22 —
    аксиальные T1 толщиной больше 4 мм, и для них сложение видов бессмысленно,
    потому что корональный и сагиттальный виды собирались бы из срезов, между
    которыми 5–7 мм пустоты. Остаётся аксиальный вид в одиночку, и это заведомо
    ослабленный режим: помечать его результат надо отдельно.

    Parameters
    ----------
    volume : numpy.ndarray
        Объём в исходной укладке.
    zooms : tuple
        Шаг отсчётов, мм.
    affine : numpy.ndarray
        Матрица перехода к координатам пациента.
    planes : tuple[str, ...]
        Виды, которые складываются.
    code_root : str | Path | None
        Корень исходного кода FastSurfer.
    weights_root : str | Path | None
        Каталог с весами.

    Returns
    -------
    numpy.ndarray
        Метки FreeSurfer в исходной укладке.
    """
    import numpy as np  # noqa: PLC0415

    root = _code_root(code_root)

    if str(root) not in sys.path:
        sys.path.insert(0, str(root))

    from FastSurferCNN.data_loader import data_utils as du  # noqa: PLC0415
    from FastSurferCNN.data_loader.conform import Reorientation  # noqa: PLC0415
    from FastSurferCNN.inference import Inference  # noqa: PLC0415
    from FastSurferCNN.utils.load_config import get_config  # noqa: PLC0415

    lut = du.read_classes_from_lut(root / "FastSurferCNN" / "config" / "FastSurfer_ColorLUT.tsv")
    labels = np.asarray(lut["ID"].values).copy()

    zooms = np.asarray(zooms)
    to_lia = Reorientation.from_target_orientation(affine, "soft LIA", volume.shape, zooms)
    in_lia = to_lia(volume, order=1)
    zoom_in_lia = to_lia.reorder_axes(zooms)

    probabilities = None

    for plane in planes:
        class _Args:
            cfg_file = str(root / "FastSurferCNN" / "config" / f"FastSurferVINN_{plane}.yaml")
            opts: list[str] = []

        engine = Inference(
            get_config(_Args()),
            ckpt=str(_weights(plane, weights_root)),
            device=torch.device("cpu"),
            lut=lut,
        )

        if probabilities is None:
            shape = in_lia.shape + (engine.get_num_classes(),)
            probabilities = torch.zeros(shape, device="cpu", dtype=torch.float16, requires_grad=False)

        probabilities = engine.run(probabilities, "series", in_lia, zoom_in_lia, out=probabilities)

    classes = torch.argmax(probabilities, 3)
    classes = to_lia.inverse(classes, order=0)

    return du.map_label2aparc_aseg(classes, labels).cpu().numpy()


def ventricle_mask(labels):
    """Маска желудочков из разметки FreeSurfer.

    Берутся только метки желудочковой системы: боковые с нижними рогами, III и
    IV. Остальное разметки — кора, белое вещество, подкорковые ядра — для
    линейных показателей не нужно.
    """
    import numpy as np  # noqa: PLC0415

    mask = np.zeros(labels.shape, dtype=np.uint8)

    for label in VENTRICLE_LABELS:
        mask[labels == label] = 1

    return mask
