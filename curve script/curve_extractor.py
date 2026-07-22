"""
curve_extractor.py
------------------

Reads a Studio 5000 / RSLogix 5000 project (an ``.ACD`` file, or later an
``.L5K`` export) and pulls out the fuel/air *curve* configuration so it can be
laid out as a table.

Right now the only tag wired up is ``DesiredO2`` (data type ``FuelAirCurveData``).
That single tag drives the "O2" column of the table:

    * ``DesiredO2.Cfg.O2Curve``  -> whether the O2 column is shown at all
    * ``DesiredO2.Curve[0..15]`` -> the numbered rows (1..16) of the O2 column
    * ``DesiredO2.Purge`` / ``DesiredO2.LightOff`` -> shown for reference

The columns for ``Air``, ``Fuel Act1``, ``FGR`` and ``VFD`` are laid out but
left empty until we identify the tags that feed them.

The module has no GUI code so it can be imported and unit-tested on its own.
"""

from __future__ import annotations

import os
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from typing import List, Optional


# The row labels that run down the left edge of the table, in order.
# "gas" is the corner / title cell; then Purge, LtOff, then firing points 1..16.
NUMBERED_POINTS = 16
ROW_LABELS = ["purge", "LtOff"] + [str(n) for n in range(1, NUMBERED_POINTS + 1)]

# The column headers that run across the top of the table, in order.
# "O2" is appended only when DesiredO2.Cfg.O2Curve == 1.
BASE_COLUMNS = ["Air", "Fuel Act1", "FGR", "VFD"]
O2_COLUMN = "O2"

# Corner / title label shown in the top-left cell.
CORNER_LABEL = "gas"


class ExtractError(Exception):
    """Raised when a file cannot be read or the expected tag is missing."""


@dataclass
class CurveData:
    """Everything we managed to pull out of one project file."""

    controller_name: Optional[str] = None
    source_file: Optional[str] = None
    file_kind: Optional[str] = None          # "ACD" or "L5K"

    # DesiredO2 tag ---------------------------------------------------------
    o2_curve_enabled: bool = False           # Cfg.O2Curve == 1
    o2_purge: Optional[str] = None           # DesiredO2.Purge
    o2_lightoff: Optional[str] = None        # DesiredO2.LightOff
    o2_curve: List[str] = field(default_factory=list)   # DesiredO2.Curve[]

    notes: List[str] = field(default_factory=list)


# ---------------------------------------------------------------------------
# Value formatting
# ---------------------------------------------------------------------------

def format_value(raw: Optional[str]) -> str:
    """Turn a raw L5X value string into something tidy for the table.

    A zero of any kind is shown as ``"0"`` (per spec: "if the value ... is zero
    just fill the space in with a zero"). Whole numbers drop the ``.0``; other
    floats keep only the significant decimals.
    """
    if raw is None:
        return ""
    try:
        num = float(raw)
    except (TypeError, ValueError):
        return str(raw)
    if num == 0:
        return "0"
    if num == int(num):
        return str(int(num))
    # Trim trailing zeros on the decimal part but keep it readable.
    return f"{num:.6f}".rstrip("0").rstrip(".")


# ---------------------------------------------------------------------------
# ACD parsing
# ---------------------------------------------------------------------------

def _parse_desired_o2_xml(tag_xml: str) -> dict:
    """Parse the L5X ``<Tag ...>`` block for DesiredO2 into a plain dict."""
    root = ET.fromstring(tag_xml)
    struct = root.find(".//Structure")
    if struct is None:
        raise ExtractError("DesiredO2 tag has no decorated data.")

    out = {"purge": None, "lightoff": None, "o2curve": 0, "curve": []}

    for dvm in struct.findall("./DataValueMember"):
        if dvm.get("Name") == "Purge":
            out["purge"] = dvm.get("Value")
        elif dvm.get("Name") == "LightOff":
            out["lightoff"] = dvm.get("Value")

    curve = struct.find("./ArrayMember[@Name='Curve']")
    if curve is not None:
        out["curve"] = [e.get("Value") for e in curve.findall("./Element")]

    cfg = struct.find("./StructureMember[@Name='Cfg']")
    if cfg is not None:
        o2 = cfg.find("./DataValueMember[@Name='O2Curve']")
        if o2 is not None:
            try:
                out["o2curve"] = int(o2.get("Value"))
            except (TypeError, ValueError):
                out["o2curve"] = 0
    return out


def extract_from_acd(path: str) -> CurveData:
    """Extract curve data from a binary ``.ACD`` project file."""
    try:
        from acd.api import load_acd
    except ImportError as exc:  # pragma: no cover - dependency hint
        raise ExtractError(
            "The 'acd-tools' library is required to read .ACD files.\n"
            "Install it with:  pip install -r requirements.txt"
        ) from exc

    try:
        project = load_acd(path)
    except Exception as exc:  # noqa: BLE001 - surface any parse failure cleanly
        raise ExtractError(f"Could not parse ACD file:\n{exc}") from exc

    controller = project.controller
    data = CurveData(
        controller_name=getattr(controller, "_name", None),
        source_file=os.path.basename(path),
        file_kind="ACD",
    )

    tag = None
    for t in controller.tags:
        if (getattr(t, "name", "") or "") == "DesiredO2":
            tag = t
            break
    if tag is None:
        raise ExtractError("No 'DesiredO2' tag found in this project.")

    xml_obj = tag.to_xml()
    tag_xml = xml_obj if isinstance(xml_obj, str) else xml_obj.toxml()
    parsed = _parse_desired_o2_xml(tag_xml)

    data.o2_curve_enabled = parsed["o2curve"] == 1
    data.o2_purge = parsed["purge"]
    data.o2_lightoff = parsed["lightoff"]
    data.o2_curve = parsed["curve"]

    if not data.o2_curve_enabled:
        data.notes.append(
            "DesiredO2.Cfg.O2Curve = 0  ->  the O2 column is omitted."
        )
    else:
        data.notes.append(
            "DesiredO2.Cfg.O2Curve = 1  ->  the O2 column is shown."
        )
    return data


# ---------------------------------------------------------------------------
# L5K parsing (placeholder — awaiting a sample .L5K file)
# ---------------------------------------------------------------------------

def extract_from_l5k(path: str) -> CurveData:
    """Extract curve data from an ``.L5K`` text export.

    L5K support is not wired up yet — we need a sample export to confirm the
    exact text layout of the DesiredO2 initializer. This returns a clearly
    labelled placeholder rather than guessing at the format.
    """
    data = CurveData(
        source_file=os.path.basename(path),
        file_kind="L5K",
    )
    data.notes.append(
        ".L5K parsing is not implemented yet. Drop a sample .L5K export in and "
        "we'll add the parser (the .ACD path is fully working)."
    )
    return data


# ---------------------------------------------------------------------------
# Dispatch + table building
# ---------------------------------------------------------------------------

def extract(path: str) -> CurveData:
    """Extract curve data from a file, picking the parser by extension."""
    ext = os.path.splitext(path)[1].lower()
    if ext == ".acd":
        return extract_from_acd(path)
    if ext == ".l5k":
        return extract_from_l5k(path)
    raise ExtractError(f"Unsupported file type '{ext}'. Expected .ACD or .L5K.")


def build_table(data: CurveData) -> dict:
    """Turn a CurveData into a simple table model the GUI can render.

    Returns a dict with:
        corner  : the top-left label ("gas")
        columns : list of column headers
        rows    : list of {"label": str, "cells": {column: str}}
    """
    columns = list(BASE_COLUMNS)
    if data.o2_curve_enabled:
        columns.append(O2_COLUMN)

    rows = []
    for label in ROW_LABELS:
        cells = {col: "" for col in columns}

        if data.o2_curve_enabled and O2_COLUMN in columns:
            if label in ("purge", "LtOff"):
                # Per spec: leave purge and LtOff blank in the O2 column.
                cells[O2_COLUMN] = ""
            else:
                # Numbered rows: point N -> Curve[N-1].
                point = int(label)
                idx = point - 1
                if 0 <= idx < len(data.o2_curve):
                    cells[O2_COLUMN] = format_value(data.o2_curve[idx])
                else:
                    cells[O2_COLUMN] = ""

        rows.append({"label": label, "cells": cells})

    return {"corner": CORNER_LABEL, "columns": columns, "rows": rows}


def render_text_table(data: CurveData) -> str:
    """Render the table as monospaced text (handy for logs / CLI use)."""
    table = build_table(data)
    headers = [table["corner"]] + table["columns"]

    widths = [len(h) for h in headers]
    for row in table["rows"]:
        widths[0] = max(widths[0], len(row["label"]))
        for i, col in enumerate(table["columns"], start=1):
            widths[i] = max(widths[i], len(row["cells"][col]))

    def fmt(cells):
        return "  ".join(str(c).ljust(widths[i]) for i, c in enumerate(cells))

    lines = [fmt(headers), fmt(["-" * w for w in widths])]
    for row in table["rows"]:
        cells = [row["label"]] + [row["cells"][c] for c in table["columns"]]
        lines.append(fmt(cells))
    return "\n".join(lines)


if __name__ == "__main__":
    import sys

    if len(sys.argv) < 2:
        print("usage: python curve_extractor.py <file.ACD | file.L5K>")
        raise SystemExit(1)

    result = extract(sys.argv[1])
    print(f"Controller : {result.controller_name}")
    print(f"Source     : {result.source_file} ({result.file_kind})")
    for note in result.notes:
        print(f"Note       : {note}")
    print()
    print(render_text_table(result))
