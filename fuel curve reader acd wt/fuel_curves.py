"""
fuel_curves.py
--------------

Multi-fuel curve extraction from an ``.L5K`` export.

The earlier version read the single *active* ``Desired*`` curve set. This one
reads the **stored per-fuel** curve sets, so you can see every fuel at once —
not just whichever fuel is currently loaded.

Each fuel keeps its curves in ``ArrayMgmt_F<n><col>`` tags (data type
``FA_DataMgmt``), where ``<n>`` is the fuel number and ``<col>`` is the
actuator/loop:

    ArrayMgmt_F1Air, ArrayMgmt_F1A1, ArrayMgmt_F1FGR, ArrayMgmt_F1VFD, ArrayMgmt_F1O2
    ArrayMgmt_F2Air, ArrayMgmt_F2A1, ArrayMgmt_F2FGR, ArrayMgmt_F2VFD, ArrayMgmt_F2O2

Inside each ``FA_DataMgmt`` tag the curve values live in the ``Ref_Data``
FuelAirCurveData member. In the L5K backing-tag layout that flattens to:

    ... Purge(2), LightOff(2), Curve(16), ...

i.e. the curve is the first 16-element array, and Purge / LightOff are the two
2-element arrays immediately before it.

The ``ArrayMgmt_F1B*`` / ``ArrayMgmt_F2B*`` ("B") tags are the burner-B / backup
copies and are intentionally ignored.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass, field
from typing import Dict, List, Optional

from curve_extractor import (
    NUMBERED_POINTS,
    ROW_LABELS,
    format_value,
    _l5k_parse_initializer,
    _l5k_parse_curve_tag,
    O2_TAG,
    ExtractError,
)


# Table column header  ->  the ArrayMgmt tag suffix that feeds it.
COLUMN_SUFFIX = {
    "Air": "Air",
    "Fuel Act1": "A1",
    "FGR": "FGR",
    "VFD": "VFD",
    "O2": "O2",
}
O2_COLUMN = "O2"
FUEL_COLUMN = "Fuel Act1"      # firetube fuel column
FUEL_COLUMN_WT = "Fuel"        # water-tube fuel column (fuel valve characterizer)

# Fuel numbers we read (the non-"B" sets).
FUEL_NUMBERS = (1, 2)


class ColumnCurve:
    """Purge / LightOff / Curve for one column of one fuel."""

    def __init__(self, found=False, purge=None, lightoff=None, curve=None):
        self.found = found
        self.purge = purge
        self.lightoff = lightoff
        self.curve = curve or []

    @property
    def has_data(self) -> bool:
        return any(_nonzero(v) for v in self.curve)


@dataclass
class FuelCurves:
    """All columns of curve data for a single fuel."""

    number: int
    name: str
    columns: Dict[str, ColumnCurve] = field(default_factory=dict)


@dataclass
class MultiFuelData:
    source_file: Optional[str] = None
    controller_name: Optional[str] = None
    o2_trim_enabled: bool = False
    fuels: List[FuelCurves] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)
    # Firetube programs always have 16 curve points and the Air/Fuel Act1/FGR/
    # VFD/O2 column set. Water-tube programs use characterizer arrays whose
    # length and column set differ, so both are overridable.
    column_order: Optional[List[str]] = None
    point_count: Optional[int] = None

    @property
    def columns(self) -> List[str]:
        return self.column_order or CANONICAL_COLUMNS

    @property
    def row_labels(self) -> List[str]:
        if not self.point_count:
            return ROW_LABELS
        return ["purge", "LtOff"] + [str(n) for n in range(1, self.point_count + 1)]


def _nonzero(raw) -> bool:
    try:
        return float(raw) != 0.0
    except (TypeError, ValueError):
        return False


# ---------------------------------------------------------------------------
# L5K reading
# ---------------------------------------------------------------------------

def _extract_balanced(txt: str, tag_name: str) -> Optional[str]:
    """Return the balanced ``[...]`` initializer for a FA_DataMgmt tag."""
    m = re.search(r"\b" + re.escape(tag_name) + r"\s*:\s*FA_DataMgmt\s*:=", txt)
    if not m:
        return None
    i = m.end()
    n = len(txt)
    while i < n and txt[i] != "[":
        i += 1
    start = i
    depth = 0
    in_str = False
    while i < n:
        c = txt[i]
        if in_str:
            if c == "$":
                i += 2
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
                return txt[start:i + 1]
        i += 1
    return None


def _is_numeric_list(x, length=None) -> bool:
    if not isinstance(x, list):
        return False
    if length is not None and len(x) != length:
        return False
    return all(isinstance(e, str) and re.match(r"^-?[\d.]", e) for e in x)


def _parse_arraymgmt(txt: str, tag_name: str) -> ColumnCurve:
    """Parse Purge / LightOff / Curve out of one ArrayMgmt tag."""
    raw = _extract_balanced(txt, tag_name)
    if raw is None:
        return ColumnCurve(found=False)

    parsed = _l5k_parse_initializer(raw)
    if not isinstance(parsed, list):
        return ColumnCurve(found=False)

    # The curve is the first 16-element numeric array.
    curve_idx = None
    for idx, el in enumerate(parsed):
        if _is_numeric_list(el, NUMBERED_POINTS):
            curve_idx = idx
            break
    if curve_idx is None:
        return ColumnCurve(found=False)

    curve = list(parsed[curve_idx])

    # Purge / LightOff are the two 2-element arrays right before the curve.
    purge = lightoff = None
    if curve_idx >= 2 and _is_numeric_list(parsed[curve_idx - 2]):
        purge = parsed[curve_idx - 2][0]
    if curve_idx >= 1 and _is_numeric_list(parsed[curve_idx - 1]):
        lightoff = parsed[curve_idx - 1][0]

    return ColumnCurve(found=True, purge=purge, lightoff=lightoff, curve=curve)


def _read_fuel_type_names(txt: str) -> Dict[int, str]:
    """Read FuelTypeNames[0..] so fuel 1/2 can be labelled with a real name."""
    names: Dict[int, str] = {}
    m = re.search(r"FuelTypeNames\s*:\s*String_20\[\d+\]\s*\(", txt)
    if not m:
        return names
    # The decorated values follow ") := [ [LEN,'name'], [LEN,'name'], ... ]".
    j = txt.find(") :=", m.start())
    if j == -1:
        return names
    end = txt.find(";", j)
    block = txt[j:end]
    for i, sm in enumerate(re.finditer(r"\[\s*\d+\s*,\s*'([^']*)'", block)):
        raw = sm.group(1)
        names[i] = raw.split("$00")[0].strip()
    return names


def extract_multifuel_l5k(path: str) -> MultiFuelData:
    """Read both fuels' stored curve sets from an L5K export."""
    try:
        txt = open(path, encoding="utf-8-sig", errors="replace").read()
    except OSError as exc:
        raise ExtractError(f"Could not read L5K file:\n{exc}") from exc

    data = MultiFuelData(source_file=os.path.basename(path))

    m = re.search(r"CONTROLLER\s+(\w+)", txt)
    if m:
        data.controller_name = m.group(1)

    # O2 trim enable comes from DesiredO2.Cfg.O2Curve (bit 3) — the same flag
    # the single-set reader uses. The per-fuel FA_DataMgmt tags don't expose
    # their Cfg bits cleanly, so this active-config flag gates the O2 column.
    desired_o2 = _l5k_parse_curve_tag(txt, O2_TAG)
    data.o2_trim_enabled = bool(desired_o2.found and desired_o2.o2curve == 1)

    # Fuel 1 -> FuelTypeNames[0], Fuel 2 -> FuelTypeNames[1]
    # (from the burner fuel-selection config: 0 = Gas, 1 = #2 Oil).
    type_names = _read_fuel_type_names(txt)

    for n in FUEL_NUMBERS:
        fuel_name = type_names.get(n - 1, f"Fuel {n}")
        fuel = FuelCurves(number=n, name=fuel_name)
        for col, suffix in COLUMN_SUFFIX.items():
            tag = f"ArrayMgmt_F{n}{suffix}"
            fuel.columns[col] = _parse_arraymgmt(txt, tag)
        data.fuels.append(fuel)

    data.notes.append(
        "Fuel 1 = " + data.fuels[0].name + ", Fuel 2 = " + data.fuels[1].name
        + "  (from the file's fuel-selection config)."
    )
    if data.o2_trim_enabled:
        data.notes.append("O2 trim is enabled (DesiredO2.Cfg.O2Curve = 1) — O2 column shown.")
    else:
        data.notes.append("O2 trim is disabled (DesiredO2.Cfg.O2Curve = 0) — O2 column omitted.")
    return data


# ---------------------------------------------------------------------------
# Table building
# ---------------------------------------------------------------------------

def build_fuel_table(fuel: FuelCurves, o2_enabled: bool = False,
                     column_order=None, row_labels=None) -> dict:
    """Build a table model for one fuel.

    Rows: purge, LtOff, then one row per curve point.  Columns default to the
    firetube set (Air, Fuel Act1, FGR, VFD) plus O2 when O2 trim is enabled;
    ``column_order`` overrides that for water-tube programs.

    Blank cells (per spec):
        * the fuel column -> purge blank
        * O2              -> purge and LtOff blank
    """
    order = column_order or CANONICAL_COLUMNS
    columns = [c for c in order if c != O2_COLUMN]
    if o2_enabled and O2_COLUMN in order:
        columns.append(O2_COLUMN)

    rows = []
    for label in (row_labels or ROW_LABELS):
        cells = {}
        for col in columns:
            cc = fuel.columns.get(col)
            cells[col] = _cell(col, label, cc)
        rows.append({"label": label, "cells": cells})

    # The corner cell stays empty: the label column holds purge / LtOff / the
    # numbered points, so the fuel name doesn't belong over it. Callers print
    # the fuel name as a banner above the table instead.
    return {"corner": "", "columns": columns, "rows": rows}


def _cell(col: str, label: str, cc: Optional[ColumnCurve]) -> str:
    if cc is None or not cc.found:
        return ""
    if label == "purge":
        # Fuel + O2 purge are left blank, as is any column with no purge tag.
        if col in (FUEL_COLUMN, FUEL_COLUMN_WT, O2_COLUMN) or cc.purge is None:
            return ""
        return format_value(cc.purge)
    if label == "LtOff":
        if col == O2_COLUMN or cc.lightoff is None:   # O2 light-off left blank
            return ""
        return format_value(cc.lightoff)
    # numbered point N -> Curve[N-1]; zero/missing -> "0"
    idx = int(label) - 1
    raw = cc.curve[idx] if 0 <= idx < len(cc.curve) else None
    return format_value(raw)


CANONICAL_COLUMNS = ["Air", FUEL_COLUMN, "FGR", "VFD", O2_COLUMN]


def build_combined_table(data: MultiFuelData) -> dict:
    """Stack both fuels into one table model for the GUI.

    ``columns`` is the union of every fuel's columns (canonical order). Each
    fuel contributes a header row (``is_header=True``) carrying its name, then
    its purge / LtOff / 1..16 rows.
    """
    order, labels = data.columns, data.row_labels
    per_fuel = [(f, build_fuel_table(f, data.o2_trim_enabled, order, labels))
                for f in data.fuels]
    present = set()
    for _f, t in per_fuel:
        present.update(t["columns"])
    columns = [c for c in order if c in present]

    banner_col = columns[0] if columns else None   # the Air column
    rows = []
    for fuel, table in per_fuel:
        # Each fuel block is self-contained:
        #   1. fuel-name banner over the Air column
        #   2. a column-header row (Air, Fuel Act1, FGR, VFD, O2)
        #   3. the data rows (purge / LtOff / 1..16)
        banner = {c: "" for c in columns}
        if banner_col:
            banner[banner_col] = f"Fuel {fuel.number} — {fuel.name}"
        rows.append({"label": "", "kind": "banner", "cells": banner})

        rows.append({"label": "", "kind": "colheader",
                     "cells": {c: c for c in columns}})

        by_col = {c: {r["label"]: r["cells"][c] for r in table["rows"]}
                  for c in table["columns"]}
        for label in labels:
            cells = {c: by_col.get(c, {}).get(label, "") for c in columns}
            rows.append({"label": label, "kind": "data", "cells": cells})

    return {"corner": "", "columns": columns, "rows": rows}


def render_fuel_tables_text(data: MultiFuelData) -> str:
    out = []
    for fuel in data.fuels:
        table = build_fuel_table(fuel, data.o2_trim_enabled,
                                 data.columns, data.row_labels)
        out.append(f"Fuel {fuel.number} — {fuel.name}")
        out.append(_render_one(table))
        out.append("")
    return "\n".join(out)


def _render_one(table: dict) -> str:
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
        print("usage: python fuel_curves.py <file.L5K>")
        raise SystemExit(1)

    result = extract_multifuel_l5k(sys.argv[1])
    print(f"Controller : {result.controller_name}")
    print(f"Source     : {result.source_file}")
    for note in result.notes:
        print(f"Note       : {note}")
    print()
    print(render_fuel_tables_text(result))
