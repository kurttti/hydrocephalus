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
from collections.abc import Sequence
from pathlib import Path
from typing import TYPE_CHECKING, Any, ClassVar, cast

import torch

if TYPE_CHECKING:
    # Только для подписей: numpy в этом модуле ввозится внутри функций, и
    # переносить ввоз на уровень модуля ради подписей значило бы менять то,
    # когда он происходит. `from __future__ import annotations` делает подписи
    # строками, поэтому во время работы этот ввоз не нужен.
    #
    # Тип отсчётов не уточняется: сюда приходит и целочисленная разметка, и
    # вещественный объём, а сузить подпись значило бы объявить ограничение,
    # которого в коде нет.
    from numpy.typing import NDArray

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
            "Не задан путь к коду FastSurfer: переменная FASTSURFER_CODE или аргумент code_root."
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
        opts: ClassVar[list[str]] = []

    cfg = get_config(_Args())
    model = build_model(cfg)

    checkpoint = torch.load(_weights(plane, weights_root), map_location="cpu", weights_only=False)
    model.load_state_dict(checkpoint["model_state"])
    model.eval()

    # Сборщик сети типов не несёт, и его результат приходит как Any. Приведение
    # здесь — не утверждение о типе вместо проверки, а то, что проверить
    # нечем: кода FastSurfer в репозитории нет и в CI он не появляется.
    return cast("torch.nn.Module", model)


def segment(
    volume: NDArray[Any],
    zooms: Sequence[float],
    affine: NDArray[Any],
    planes: tuple[str, ...] = ("axial",),
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
) -> NDArray[Any]:
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
    # Проверяется первым, до чтения путей и до ввоза чужого кода: пустой
    # перечень видов — ошибка вызова, и сказать о ней надо раньше, чем
    # потребуется установленный FastSurfer.
    if not planes:
        raise ValueError("Ни один вид не задан: складывать нечего.")

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

    spacing = np.asarray(zooms)
    to_lia = Reorientation.from_target_orientation(affine, "soft LIA", volume.shape, spacing)
    in_lia = to_lia(volume, order=1)
    zoom_in_lia = to_lia.reorder_axes(spacing)

    probabilities = None

    for plane in planes:

        class _Args:
            cfg_file = str(root / "FastSurferCNN" / "config" / f"FastSurferVINN_{plane}.yaml")
            opts: ClassVar[list[str]] = []

        engine = Inference(
            get_config(_Args()),
            ckpt=str(_weights(plane, weights_root)),
            device=torch.device("cpu"),
            lut=lut,
        )

        if probabilities is None:
            shape = (*in_lia.shape, engine.get_num_classes())
            probabilities = torch.zeros(
                shape, device="cpu", dtype=torch.float16, requires_grad=False
            )

        probabilities = engine.run(probabilities, "series", in_lia, zoom_in_lia, out=probabilities)

    # Перечень видов непуст, значит цикл выполнился и вероятности собраны.
    # Проверка нужна подписи: без неё argmax принимал `Tensor | None` и на
    # пустом перечне падал невнятно — на складывании, а не на его отсутствии.
    assert probabilities is not None

    classes = torch.argmax(probabilities, 3)
    classes = to_lia.inverse(classes, order=0)

    # Перевод номеров классов в метки FreeSurfer делает код FastSurfer, и типов
    # он не несёт.
    return cast("NDArray[Any]", du.map_label2aparc_aseg(classes, labels).cpu().numpy())


def ventricle_mask(labels: NDArray[Any]) -> NDArray[Any]:
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

        return cast("torch.Tensor", self.inner(image, scale))


def export_onnx(
    destination: str | Path,
    plane: str = "axial",
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
) -> Path:
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
    import onnx  # noqa: PLC0415

    model = _AtOneMillimetre(load_model(plane, code_root, weights_root)).eval()
    target = Path(destination)
    target.parent.mkdir(parents=True, exist_ok=True)

    image = torch.zeros(
        1, MODEL_INPUT_CHANNELS, MODEL_INPUT_SIZE, MODEL_INPUT_SIZE, dtype=torch.float32
    )

    # Пачка закреплена единицей, и это вынужденно. `torch.export` спотыкается о
    # слой интерполяции — тот вычисляет размер выхода из коэффициента масштаба
    # во время работы, — экспорт уходит в запасной путь через TorchScript, а он
    # переменные оси не принимает и запекает форму целиком. Обойти интерполяцию
    # значило бы менять саму сеть, то есть поставлять не то, что проверено.
    # Движок поэтому считает срез за срезом.
    torch.onnx.export(
        model,
        (image,),
        str(target),
        dynamo=True,
        input_names=["image"],
        output_names=["logits"],
    )

    # Экспорт кладёт веса в соседний файл; собираем обратно в один.
    whole = onnx.load(str(target))
    onnx.save(whole, str(target), save_as_external_data=False)

    for leftover in target.parent.glob(target.name + ".data"):
        leftover.unlink()

    return target


def capture_network_input(
    volume: NDArray[Any],
    zooms: Sequence[float],
    affine: NDArray[Any],
    plane: str = "axial",
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
) -> tuple[torch.Tensor, torch.Tensor]:
    """Перехватывает тензор, который в действительности приходит в сеть.

    Нужен сверке. Подавать сети срезы напрямую — значит проверять её как
    функцию, а не тот вход, который придётся воспроизводить в движке: путь
    данных FastSurfer делит значения на 255, собирает по семь соседних срезов и
    складывает их пачками, и всё это между объёмом и сетью. Сверка на сыром
    срезе этого не захватывает, а числа там в 255 раз больше настоящих.

    Returns
    -------
    tuple
        Тензор входа (N, 7, 256, 256) и коэффициент масштаба.
    """
    root = _code_root(code_root)

    if str(root) not in sys.path:
        sys.path.insert(0, str(root))

    from FastSurferCNN.inference import Inference  # noqa: PLC0415

    grabbed: list[tuple[torch.Tensor, torch.Tensor]] = []
    original = Inference.run

    # Подписи обёртки повторяют подпись `Inference.run`, а она типов не несёт:
    # ставить здесь что-то кроме Any значило бы объявить о чужом методе больше,
    # чем о нём известно.
    def patched(
        self: Any,
        init_pred: Any,
        name: Any,
        data: Any,
        zoom: Any,
        out: Any = None,
        out_res: Any = None,
        batch_size: Any = None,
    ) -> Any:
        # Подпись задана PyTorch: у forward-pre-hook два параметра, и модуль
        # здесь не нужен — нужен только вход.
        def hook(_module: Any, inputs: tuple[torch.Tensor, ...]) -> None:
            if not grabbed:
                grabbed.append((inputs[0].detach().clone(), inputs[1].detach().clone()))

        handle = self.model.register_forward_pre_hook(hook)

        try:
            return original(
                self,
                init_pred,
                name,
                data,
                zoom,
                out=out,
                out_res=out_res,
                batch_size=batch_size,
            )
        finally:
            handle.remove()

    Inference.run = patched

    try:
        segment(
            volume,
            zooms,
            affine,
            planes=(plane,),
            code_root=code_root,
            weights_root=weights_root,
        )
    finally:
        Inference.run = original

    if not grabbed:
        raise RuntimeError("Вход сети не перехвачен.")

    return grabbed[0]


#: Наибольший отрыв первого класса от второго, при котором расхождение метки
#: считается ничьёй, а не ошибкой перевода.
TIE_LOGIT_MARGIN = 1.0


def compare_onnx(
    onnx_path: str | Path,
    network_input: torch.Tensor,
    scale: torch.Tensor,
    plane: str = "axial",
    code_root: str | Path | None = None,
    weights_root: str | Path | None = None,
) -> dict[str, float]:
    """Сверяет разметку ONNX с исходной моделью на перехваченном входе.

    ADR 0009 называет расхождение здесь блокирующей ошибкой перевода: модель,
    дающая в поставке другие метки, чем проверенная в контуре, обесценивает
    всякую проверку до неё.

    Сверяются **метки**, а не logits: у уверенно размеченного отсчёта отрыв
    первого класса от второго достигает сотен, и расхождение logits в единицы
    на метку не влияет. Ошибкой считается расхождение метки там, где отрыв
    больше :data:`TIE_LOGIT_MARGIN`; при меньшем метку переворачивает любая
    разница округления, и это ничья.

    Вход берётся из :func:`capture_network_input`, а не собирается вручную.

    Returns
    -------
    dict
        Доля совпавших меток, число настоящих расхождений, наибольшее
        расхождение logits и число отсчётов.
    """
    import numpy as np  # noqa: PLC0415
    import onnxruntime  # noqa: PLC0415

    model = load_model(plane, code_root, weights_root)
    session = onnxruntime.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])

    data = network_input.numpy() if torch.is_tensor(network_input) else np.asarray(network_input)
    agreed = 0
    total = 0
    genuine = 0
    worst = 0.0

    # Срез за срезом: в экспортированной модели пачка закреплена единицей.
    for index in range(data.shape[0]):
        one = data[index : index + 1].astype(np.float32)

        with torch.no_grad():
            expected = model(torch.from_numpy(one), scale[:1]).numpy()

        actual = session.run(["logits"], {"image": one})[0]
        worst = max(worst, float(np.abs(expected - actual).max()))
        expected_labels = expected.argmax(1)
        actual_labels = actual.argmax(1)
        agreed += int((expected_labels == actual_labels).sum())
        total += int(expected_labels.size)

        for batch, y, x in np.argwhere(expected_labels != actual_labels):
            ranked = np.sort(expected[batch, :, y, x])

            if float(ranked[-1] - ranked[-2]) > TIE_LOGIT_MARGIN:
                genuine += 1

    return {
        "label_agreement": agreed / total,
        "genuine_disagreements": genuine,
        "max_logit_difference": worst,
        "voxels": total,
    }
