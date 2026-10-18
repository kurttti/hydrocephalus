"""Проверка книги регистра, которую заполняет нейрорадиолог.

Проверяется не вид книги, а те её свойства, из-за которых она вообще собирается:
индекс считается формулой, а не человеком; шкалы закрыты проверкой ввода;
заполненные заранее ячейки отличимы от ждущих ввода; пропуск остаётся пропуском,
а не превращается в ноль.
"""

from __future__ import annotations

from pathlib import Path

import pytest
from openpyxl import load_workbook

from hydrocephalus_ml.reader_register import (
    COMPUTED,
    SKULL_COLUMN,
    WIDTH_COLUMN,
    RegisterError,
    build_register,
)

HEADER = (
    "код исследования",
    "код специалиста",
    "тип чтения (1 первое; 2 повторное)",
    "возраст, полных лет",
    "пол",
    WIDTH_COLUMN,
    SKULL_COLUMN,
    "MTA справа",
    "пустое турецкое седло (0/1)",
    "комментарий",
)


def write_csv(path: Path, header: tuple[str, ...] = HEADER, rows: int = 2) -> Path:
    lines = [";".join(f'"{name}"' for name in header)]

    for number in range(1, rows + 1):
        values = [""] * len(header)
        values[0] = f"S-{number:04d}"

        if "возраст, полных лет" in header:
            values[header.index("возраст, полных лет")] = "74"
            values[header.index("пол")] = "F"

        lines.append(";".join(values))

    path.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")

    return path


def test_index_is_a_formula_not_a_number(tmp_path: Path) -> None:
    source = write_csv(tmp_path / "регистр.csv")

    summary = build_register(source, tmp_path / "книга.xlsx")

    sheet = load_workbook(summary.path)["Регистр"]
    assert sheet.cell(row=1, column=sheet.max_column).value == COMPUTED
    formula = sheet.cell(row=2, column=sheet.max_column).value
    assert isinstance(formula, str)
    assert formula.startswith("=IF(")
    assert "ROUND(F2/G2,3)" in formula


def test_prefilled_cells_carry_age_and_sex(tmp_path: Path) -> None:
    source = write_csv(tmp_path / "регистр.csv")

    summary = build_register(source, tmp_path / "книга.xlsx")

    sheet = load_workbook(summary.path)["Регистр"]
    assert sheet.cell(row=2, column=1).value == "S-0001"
    assert sheet.cell(row=2, column=4).value == "74"
    assert sheet.cell(row=2, column=5).value == "F"


def test_an_empty_cell_stays_empty(tmp_path: Path) -> None:
    # Ноль вместо пропуска означал бы «признака нет» там, где признак не
    # оценивали: эти две вещи в анализе нельзя смешивать.
    source = write_csv(tmp_path / "регистр.csv")

    summary = build_register(source, tmp_path / "книга.xlsx")

    sheet = load_workbook(summary.path)["Регистр"]
    assert sheet.cell(row=2, column=6).value is None
    assert sheet.cell(row=2, column=8).value is None


def test_closed_scales_are_restricted(tmp_path: Path) -> None:
    source = write_csv(tmp_path / "регистр.csv")

    summary = build_register(source, tmp_path / "книга.xlsx")

    sheet = load_workbook(summary.path)["Регистр"]
    ranges = {
        str(next(iter(rule.sqref.ranges))): rule.formula1
        for rule in sheet.data_validations.dataValidation
    }
    assert ranges["C2:C3"] == '"1,2"'
    assert ranges["H2:H3"] == '"0,1,2,3,4"'
    assert ranges["I2:I3"] == '"0,1"'


def test_three_sheets_including_the_reasons(tmp_path: Path) -> None:
    source = write_csv(tmp_path / "регистр.csv")

    summary = build_register(source, tmp_path / "книга.xlsx")

    book = load_workbook(summary.path)
    assert book.sheetnames == ["Регистр", "Контроль качества", "Как заполнять"]
    assert book["Контроль качества"]["F4"].value is not None


def test_summary_counts_editable_columns(tmp_path: Path) -> None:
    source = write_csv(tmp_path / "регистр.csv", rows=5)

    summary = build_register(source, tmp_path / "книга.xlsx")

    assert summary.rows == 5
    assert summary.columns == len(HEADER) + 1
    # Из столбцов не заполняются три: код, возраст и пол, — и ещё считаемый.
    assert summary.editable == len(HEADER) - 3


def test_a_table_without_the_two_segments_is_refused(tmp_path: Path) -> None:
    header = tuple(name for name in HEADER if name != WIDTH_COLUMN)
    source = write_csv(tmp_path / "регистр.csv", header=header)

    with pytest.raises(RegisterError, match="ширина передних рогов"):
        build_register(source, tmp_path / "книга.xlsx")


def test_an_empty_table_is_refused(tmp_path: Path) -> None:
    source = tmp_path / "регистр.csv"
    source.write_text("", encoding="utf-8-sig")

    with pytest.raises(RegisterError, match="пуста"):
        build_register(source, tmp_path / "книга.xlsx")
