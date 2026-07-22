"""
curve_extractor.py
------------------

Reads a Studio 5000 / RSLogix 5000 project (an ``.ACD`` file, or later an
``.L5K`` export) and pulls out the fuel/air *curve* configuration so it can be
laid out as a table.

Each column of the table is driven by one ``FuelAirCurveData`` tag:

    ==========  =================  ==================================
    Column      Tag                Notes
    ==========  =================  ==================================
    Air         DesiredAir
    Fuel Act1   DesiredFuel_A1
    FGR         DesiredFGR
    VFD         DesiredVFD
    O2          DesiredO2          shown only when Cfg.O2Curve == 1
    ==========  =================  ==================================

For every column tag the rows map like this:

    * ``purge`` row  -> ``<tag>.Purge``     (blank for the O2 column)
    * ``LtOff`` row  -> ``<tag>.LightOff``  (blank for the O2 column)
    * rows ``1``..``16`` -> ``<tag>.Curve[0]`` .. ``<tag>.Curve[15]``

Any value that is zero or missing is shown as ``0``.

The module has no GUI code so it can be imported and unit-tested on its own.
"""

from __future__ import annotations

import os
import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from typing import Dict, List, Optional


# How many numbered firing points run down the table (Curve[0]..Curve[15]).
NUMBERED_POINTS = 16
ROW_LABELS = ["purge", "LtOff"] + [str(n) for n in range(1, NUMBERED_POINTS + 1)]

# Column header  ->  tag name that feeds it. Order here is the column order.
COLUMN_TAGS = {
    "Air": "DesiredAir",
    "Fuel Act1": "DesiredFuel_A1",
    "FGR": "DesiredFGR",
    "VFD": "DesiredVFD",
}

# The O2 column is special: it is only shown when DesiredO2.Cfg.O2Curve == 1,
# and its purge / LtOff rows are always left blank.
O2_COLUMN = "O2"
O2_TAG = "DesiredO2"

# Corner / title label shown in the top-left cell.
CORNER_LABEL = "gas"


class ExtractError(Exception):
    """Raised when a file cannot be read or a required tag is missing."""


@dataclass
class TagCurve:
    """Parsed contents of one ``FuelAirCurveData`` tag."""

    name: str
    found: bool = False
    purge: Optional[str] = None
    lightoff: Optional[str] = None
    o2curve: int = 0
    curve: List[str] = field(default_factory=list)


@dataclass
class CurveData:
    """Everything we managed to pull out of one project file."""

    controller_name: Optional[str] = None
    source_file: Optional[str] = None
    file_kind: Optional[str] = None          # "ACD" or "L5K"
    tags: Dict[str, TagCurve] = field(default_factory=dict)
    notes: List[str] = field(default_factory=list)

    @property
    def o2_curve_enabled(self) -> bool:
        o2 = self.tags.get(O2_TAG)
        return bool(o2 and o2.found and o2.o2curve == 1)


# ---------------------------------------------------------------------------
# Value formatting
# ---------------------------------------------------------------------------

def format_value(raw: Optional[str]) -> str:
    """Turn a raw L5X value string into something tidy for the table.

    Numbers are rounded to **one decimal place** (e.g. ``10.387982`` -> ``10.4``).
    Zero or missing values are shown as ``"0"``, and a rounded whole number drops
    the trailing ``.0`` (e.g. ``5.0`` -> ``5``).
    """
    if raw is None:
        return "0"
    try:
        num = float(raw)
    except (TypeError, ValueError):
        return str(raw)
    num = round(num, 1)
    if num == 0:
        return "0"
    if num == int(num):
        return str(int(num))
    return f"{num:.1f}"


# ---------------------------------------------------------------------------
# ACD parsing
# ---------------------------------------------------------------------------

def _parse_curve_tag_xml(tag_xml: str, name: str) -> TagCurve:
    """Parse an L5X ``<Tag ...>`` block for a FuelAirCurveData tag."""
    root = ET.fromstring(tag_xml)
    struct = root.find(".//Structure")
    if struct is None:
        return TagCurve(name=name, found=False)

    tc = TagCurve(name=name, found=True)

    for dvm in struct.findall("./DataValueMember"):
        if dvm.get("Name") == "Purge":
            tc.purge = dvm.get("Value")
        elif dvm.get("Name") == "LightOff":
            tc.lightoff = dvm.get("Value")

    curve = struct.find("./ArrayMember[@Name='Curve']")
    if curve is not None:
        tc.curve = [e.get("Value") for e in curve.findall("./Element")]

    cfg = struct.find("./StructureMember[@Name='Cfg']")
    if cfg is not None:
        o2 = cfg.find("./DataValueMember[@Name='O2Curve']")
        if o2 is not None:
            try:
                tc.o2curve = int(o2.get("Value"))
            except (TypeError, ValueError):
                tc.o2curve = 0
    return tc


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

    # Index the controller tags we care about by name.
    wanted = set(COLUMN_TAGS.values()) | {O2_TAG}
    by_name = {}
    for t in controller.tags:
        n = getattr(t, "name", "") or ""
        if n in wanted:
            by_name[n] = t

    for tag_name in wanted:
        tag = by_name.get(tag_name)
        if tag is None:
            data.tags[tag_name] = TagCurve(name=tag_name, found=False)
            data.notes.append(f"Tag '{tag_name}' was not found in this project.")
            continue
        xml_obj = tag.to_xml()
        tag_xml = xml_obj if isinstance(xml_obj, str) else xml_obj.toxml()
        data.tags[tag_name] = _parse_curve_tag_xml(tag_xml, tag_name)

    if data.o2_curve_enabled:
        data.notes.append(
            "DesiredO2.Cfg.O2Curve = 1  ->  the O2 column is shown."
        )
    else:
        data.notes.append(
            "DesiredO2.Cfg.O2Curve = 0  ->  the O2 column is omitted."
        )
    return data


# ---------------------------------------------------------------------------
# L5K parsing (placeholder — awaiting a sample .L5K file)
# ---------------------------------------------------------------------------

# A FuelAirCurveData initializer in an L5K export is a positional, nested
# bracket list. The members we care about sit at these fixed positions:
#
#   [ [LEN,'Name'],   # 0  Name (STRING_20)
#     Purge,          # 1
#     LightOff,       # 2
#     [Curve x16],    # 3
#     ConfigSP,       # 4
#     PosnActual,     # 5
#     [Display x4],   # 6
#     Sts_ConfigSaved,# 7
#     Sts_CurveSize,  # 8
#     Err_DataNotValid,#9
#     [Cfg] ]         # last: [packedBOOLs, MinIncDecPct, StuckBttnDelay]
#
# The seven Cfg BOOLs pack into one integer, so O2Curve is bit 3.
_L5K_PURGE_IDX = 1
_L5K_LIGHTOFF_IDX = 2
_L5K_CURVE_IDX = 3
_O2CURVE_BIT = 3


def _l5k_parse_initializer(text: str):
    """Parse an L5K initializer (``[...]``) into nested Python lists.

    Scalars stay as their raw token strings; quoted ``'...'`` strings are
    returned as-is (with L5K ``$`` escapes left intact).
    """
    idx = 0
    n = len(text)

    def skip_ws():
        nonlocal idx
        while idx < n and text[idx] in " \t\r\n":
            idx += 1

    def parse_value():
        skip_ws()
        c = text[idx]
        if c == "[":
            return parse_list()
        if c == "'":
            return parse_string()
        return parse_scalar()

    def parse_list():
        nonlocal idx
        idx += 1  # consume '['
        items = []
        skip_ws()
        if idx < n and text[idx] == "]":
            idx += 1
            return items
        while idx < n:
            items.append(parse_value())
            skip_ws()
            if idx < n and text[idx] == ",":
                idx += 1
                continue
            if idx < n and text[idx] == "]":
                idx += 1
                break
            break
        return items

    def parse_string():
        nonlocal idx
        idx += 1  # consume opening quote
        start = idx
        while idx < n:
            if text[idx] == "$":     # L5K escape — skip next char
                idx += 2
                continue
            if text[idx] == "'":
                s = text[start:idx]
                idx += 1
                return s
            idx += 1
        return text[start:idx]

    def parse_scalar():
        nonlocal idx
        start = idx
        while idx < n and text[idx] not in ",[]' \t\r\n":
            idx += 1
        return text[start:idx]

    return parse_value()


def _l5k_extract_initializer(txt: str, tag_name: str):
    """Return the raw ``[...]`` initializer text for one FuelAirCurveData tag."""
    m = re.search(
        r"\b" + re.escape(tag_name) + r"\s*:\s*FuelAirCurveData\s*:=",
        txt,
    )
    if not m:
        return None
    idx = m.end()
    n = len(txt)
    # Skip to the opening bracket, then track bracket depth (ignoring quoted
    # strings) until the matching close.
    while idx < n and txt[idx] != "[":
        idx += 1
    start = idx
    depth = 0
    in_str = False
    while idx < n:
        c = txt[idx]
        if in_str:
            if c == "$":
                idx += 2
                continue
            if c == "'":
                in_str = False
        elif c == "'":
            in_str = True
        elif c == "[":
            depth += 1
        elif c == "]":
            depth -= 1
            if depth == 0:
                return txt[start:idx + 1]
        idx += 1
    return None


def _l5k_parse_curve_tag(txt: str, tag_name: str) -> TagCurve:
    """Parse one FuelAirCurveData tag out of the L5K text."""
    raw = _l5k_extract_initializer(txt, tag_name)
    if raw is None:
        return TagCurve(name=tag_name, found=False)

    parsed = _l5k_parse_initializer(raw)
    tc = TagCurve(name=tag_name, found=True)

    if isinstance(parsed, list) and len(parsed) > _L5K_CURVE_IDX:
        tc.purge = parsed[_L5K_PURGE_IDX]
        tc.lightoff = parsed[_L5K_LIGHTOFF_IDX]
        curve = parsed[_L5K_CURVE_IDX]
        if isinstance(curve, list):
            tc.curve = list(curve)
        # Cfg is the last bracketed group; its first element packs the BOOLs.
        cfg = parsed[-1]
        if isinstance(cfg, list) and cfg:
            try:
                packed = int(float(cfg[0]))
                tc.o2curve = (packed >> _O2CURVE_BIT) & 1
            except (TypeError, ValueError):
                tc.o2curve = 0
    return tc


def extract_from_l5k(path: str) -> CurveData:
    """Extract curve data from an ``.L5K`` text export."""
    try:
        txt = open(path, encoding="utf-8-sig", errors="replace").read()
    except OSError as exc:
        raise ExtractError(f"Could not read L5K file:\n{exc}") from exc

    data = CurveData(source_file=os.path.basename(path), file_kind="L5K")

    m = re.search(r"CONTROLLER\s+(\w+)", txt)
    if m:
        data.controller_name = m.group(1)

    for tag_name in set(COLUMN_TAGS.values()) | {O2_TAG}:
        tc = _l5k_parse_curve_tag(txt, tag_name)
        data.tags[tag_name] = tc
        if not tc.found:
            data.notes.append(f"Tag '{tag_name}' was not found in this export.")

    if data.o2_curve_enabled:
        data.notes.append("DesiredO2.Cfg.O2Curve = 1  ->  the O2 column is shown.")
    else:
        data.notes.append("DesiredO2.Cfg.O2Curve = 0  ->  the O2 column is omitted.")
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


def _cell_value(tc: Optional[TagCurve], label: str) -> str:
    """Value for one cell of a normal (non-O2) column."""
    if tc is None or not tc.found:
        return ""
    if label == "purge":
        return format_value(tc.purge)
    if label == "LtOff":
        return format_value(tc.lightoff)
    # numbered point N -> Curve[N-1]; missing/zero -> "0"
    idx = int(label) - 1
    raw = tc.curve[idx] if 0 <= idx < len(tc.curve) else None
    return format_value(raw)


def _o2_cell_value(tc: Optional[TagCurve], label: str) -> str:
    """Value for one cell of the O2 column (purge / LtOff blank)."""
    if label in ("purge", "LtOff"):
        return ""
    if tc is None or not tc.found:
        return ""
    idx = int(label) - 1
    raw = tc.curve[idx] if 0 <= idx < len(tc.curve) else None
    return format_value(raw)


def build_table(data: CurveData) -> dict:
    """Turn a CurveData into a simple table model the GUI can render.

    Returns a dict with:
        corner  : the top-left label ("gas")
        columns : list of column headers
        rows    : list of {"label": str, "cells": {column: str}}
    """
    columns = list(COLUMN_TAGS.keys())
    if data.o2_curve_enabled:
        columns.append(O2_COLUMN)

    rows = []
    for label in ROW_LABELS:
        cells = {}
        for col in COLUMN_TAGS:
            cells[col] = _cell_value(data.tags.get(COLUMN_TAGS[col]), label)
        if O2_COLUMN in columns:
            cells[O2_COLUMN] = _o2_cell_value(data.tags.get(O2_TAG), label)
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
