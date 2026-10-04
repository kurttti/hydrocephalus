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


#: Размер входа, на который обучена сеть (`HEIGHT`/`WIDTH` в её конфигурации).
MODEL_INPUT_SIZE = 256

#: Число входных каналов: семь соседних срезов (`NUM_CHANNELS`).
MODEL_INPUT_CHANNELS = 7


class _AtOneMillimetre(torch.nn.Module):
    """Сеть с закреплённым единичным масштабом.

    Слой интерполяции VINN вычисляет размер выхода **из коэффициента масштаба
    во время работы**, и граф получается зависимым от данных — так экспорт не
    проходит. Коэффициент закрепляется единицей, и это не упрощение: снимок
    приводится к 1 мм до вывода в любом случае, иначе стопка из семи соседних
    срезов охватывает не семь миллиметров, а тридцать с лишним.

    Отсюда ограничение, которое обязано ехать вместе с файлом: модель в ONNX
    верна только для приведённого входа.
    """

    def __init__(self, inner: torch.nn.Module):
        super().__init__()
        self.inner = inner

    def forward(self, image: torch.Tensor) -> torch.Tensor:
        scale = torch.ones(image.shape[0], 2, dtype=image.dtype, device=image.device)

        return self.inner(image, scale)


def export_onnx(
    destination,
    plane: str = "axial",
    code_root=None,
    weights_root=None,
):
    """Переводит один вид сети в ONNX одним файлом.

    Нужно для поставки: Python в клиническую сборку не входит (ADR 0002), и
    модель попадает туда весами ONNX внутри подписанного пакета (ADR 0004).

    Веса сохраняются внутри файла, а не рядом: пакет подписывается целиком, и
    отдельный файл данных пришлось бы подписывать и проверять отдельно.

    Returns
    -------
    Path
        Путь записанного файла.
    """
    from pathlib import Path as _Path  # noqa: PLC0415

    import onnx  # noqa: PLC0415

    model = _AtOneMillimetre(load_model(plane, code_root, weights_root)).eval()
    destination = _Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)

    image = torch.zeros(
        1, MODEL_INPUT_CHANNELS, MODEL_INPUT_SIZE, MODEL_INPUT_SIZE, dtype=torch.float32
    )

    torch.onnx.export(
        model,
        (image,),
        str(destination),
        dynamo=True,
        input_names=["image"],
        output_names=["logits"],
    )

    # Экспорт кладёт веса в соседний файл; собираем обратно в один.
    whole = onnx.load(str(destination))
    onnx.save(whole, str(destination), save_as_external_data=False)

    for leftover in destination.parent.glob(destination.name + ".data"):
        leftover.unlink()

    return destination


#: Наибольший отрыв первого класса от второго, при котором расхождение метки
#: считается ничьёй, а не ошибкой перевода.
TIE_LOGIT_MARGIN = 1.0


def compare_onnx(
    onnx_path,
    volume,
    plane: str = "axial",
    code_root=None,
    weights_root=None,
    centres=(100, 115, 128, 140, 155),
):
    """Сверяет разметку ONNX с исходной моделью PyTorch на настоящих срезах.

    ADR 0009 называет расхождение здесь блокирующей ошибкой перевода: модель,
    дающая в поставке другие метки, чем проверенная в контуре, обесценивает
    всякую проверку до неё.

    Сверяются **метки**, а не logits: у уверенно размеченного отсчёта отрыв
    первого класса от второго достигает сотен, и расхождение logits в единицы
    на метку не влияет. Считается ошибкой лишь расхождение метки там, где отрыв
    больше :data:`TIE_LOGIT_MARGIN`; при меньшем отрыве метку переворачивает
    любая разница округления, и это ничья, а не ошибка.

    Сверять на случайном шуме недостаточно: он сети чужд, и расхождения на нём
    ни о чём не говорят. Поэтому нужен настоящий объём.

    Parameters
    ----------
    volume : numpy.ndarray
        Приведённый объём, из которого берутся стопки срезов.

    Returns
    -------
    dict
        Доля совпавших меток, число настоящих расхождений и наибольший отрыв
        среди них.
    """
    import numpy as np  # noqa: PLC0415

    import onnxruntime  # noqa: PLC0415

    model = load_model(plane, code_root, weights_root)
    session = onnxruntime.InferenceSession(
        str(onnx_path), providers=["CPUExecutionProvider"]
    )

    agreed = 0
    total = 0
    genuine = 0
    worst_margin = 0.0

    for centre in centres:
        stack = np.stack(
            [
                volume[:, min(max(centre + offset, 0), volume.shape[1] - 1), :]
                for offset in range(-3, 4)
            ],
            axis=0,
        )
        image = torch.from_numpy(stack[None].astype(np.float32))

        with torch.no_grad():
            expected = model(image, torch.ones(1, 2)).numpy()

        actual = session.run(["logits"], {"image": image.numpy()})[0]
        expected_labels = expected.argmax(1)
        actual_labels = actual.argmax(1)

        agreed += int((expected_labels == actual_labels).sum())
        total += int(expected_labels.size)

        for batch, y, x in np.argwhere(expected_labels != actual_labels):
            ranked = np.sort(expected[batch, :, y, x])
            margin = float(ranked[-1] - ranked[-2])

            if margin > TIE_LOGIT_MARGIN:
                genuine += 1
                worst_margin = max(worst_margin, margin)

    return {
        "label_agreement": agreed / total,
        "genuine_disagreements": genuine,
        "worst_genuine_margin": worst_margin,
        "voxels": total,
    }
