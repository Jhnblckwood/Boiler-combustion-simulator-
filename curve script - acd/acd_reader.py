"""
acd_reader.py
-------------

Read the per-fuel curve sets **directly out of a binary `.ACD`** — including the
real commissioned values.

Why this is non-trivial: the open-source `acd-tools` library reconstructs each
tag's *structure* but writes zeros for the stored values (it doesn't decode the
initial-value buffer). So this module goes a step further — it uses `acd-tools`
only to decompress the ACD's internal databases into SQLite, then reads the tag
*values* straight from the raw binary records:

  1. Each tag's definition record contains a 4-byte reference to its **value
     record** (a child of the ``RxDataCollection`` component).
  2. A ``DesiredX`` value record is a clean ``FuelAirCurveData`` blob:
     ``Name(24) | Purge(4) | LightOff(4) | Curve[16](64) | ... | Cfg``.
  3. An ``ArrayMgmt_F<n>X`` value record is an AOI backing tag where the curve
     is stored as ``Ref_Data`` followed by an identical working copy; the curve
     is located by that "double curve" signature preceded by the
     ``[purge,purge,lightoff,lightoff]`` pair block.

Verified against RH800_27 to match the Studio-5000 L5K export exactly.
"""

from __future__ import annotations

import math
import os
import sqlite3
import struct
import tempfile

from fuel_curves import (
    MultiFuelData, FuelCurves, ColumnCurve, COLUMN_SUFFIX, FUEL_NUMBERS,
)
from curve_extractor import ExtractError

O2CURVE_BIT = 3   # DesiredO2.Cfg.O2Curve is bit 3 of the packed BOOL config


def _f(b, off):
    return struct.unpack_from("<f", b, off)[0]


def _u32(b, off):
    return struct.unpack_from("<I", b, off)[0]


def _find_desired_facd(rec):
    """Decode a clean FuelAirCurveData value record (DesiredX tags).

    Layout is Name-anchored: a STRING_20 (DINT length + up to 20 ASCII chars),
    then Purge, LightOff, Curve[16], ConfigSP, PosnActual, Display(16 bytes),
    Sts, Sts, Err, then Cfg (packed BOOLs, MinIncDecPct, StuckBttnDelay).
    """
    for off in range(0, len(rec) - 92):
        ln = _u32(rec, off)
        if not (1 <= ln <= 20):
            continue
        s = rec[off + 4:off + 4 + ln]
        if not (all(32 <= c < 127 for c in s) and s[:1].isalpha()):
            continue
        curve = list(struct.unpack_from("<16f", rec, off + 32))
        if not all(math.isfinite(x) and -1e5 < x < 1e5 for x in curve):
            continue
        cfg_off = off + 32 + 64 + 4 + 4 + 16 + 4 + 4 + 4
        packed = struct.unpack_from("<i", rec, cfg_off)[0]
        return {
            "name": s.decode(errors="replace"),
            "purge": _f(rec, off + 24),
            "lightoff": _f(rec, off + 28),
            "curve": curve,
            "cfg": packed,
        }
    return None


def _find_arraymgmt_curve(rec):
    """Locate the curve in an ArrayMgmt (FA_DataMgmt) value record.

    The curve appears twice back-to-back (Ref_Data + working copy) and is
    preceded by ``[purge, purge, lightoff, lightoff]``. Several byte offsets can
    coincidentally satisfy the shape, so all candidates are scored by how many
    real (non-trivial) curve points they carry and the best is chosen.
    """
    best = None
    best_score = -1
    n = len(rec)
    for i in range(16, n - 128):
        c1 = struct.unpack_from("<16f", rec, i)
        if not (math.isfinite(c1[0]) and abs(c1[0]) > 0.05):
            continue
        if not all(math.isfinite(x) and -1e5 < x < 1e5 for x in c1):
            continue
        c2 = struct.unpack_from("<16f", rec, i + 64)
        if not all(abs(a - b) < 1e-3 for a, b in zip(c1, c2)):
            continue
        p0, p1 = _f(rec, i - 16), _f(rec, i - 12)
        l0, l1 = _f(rec, i - 8), _f(rec, i - 4)
        if not (abs(p0 - p1) < 1e-3 and abs(l0 - l1) < 1e-3):
            continue
        score = sum(1 for x in c1 if abs(x) > 0.1)
        if score > best_score:
            best_score = score
            best = {"purge": p0, "lightoff": l0, "curve": list(c1)}
    return best


class _AcdDb:
    """Thin helper over the SQLite DB that acd-tools builds from an ACD."""

    def __init__(self, db_path):
        self.con = sqlite3.connect(db_path)
        self.cur = self.con.cursor()
        # RxDataCollection is the parent of every tag value record; its id
        # differs per file, so look it up rather than hard-coding.
        self.cur.execute(
            "SELECT object_id FROM comps WHERE comp_name='RxDataCollection' LIMIT 1")
        row = self.cur.fetchone()
        coll_id = row[0] if row else None
        self.valrecs = {}
        if coll_id is not None:
            self.cur.execute(
                "SELECT object_id, record FROM comps WHERE parent_id=?", (coll_id,))
            for oid, rec in self.cur.fetchall():
                self.valrecs[oid] = rec.encode("latin1") if isinstance(rec, str) else rec

    def _def_record(self, tag_name):
        self.cur.execute(
            "SELECT record FROM comps WHERE comp_name=? LIMIT 1", (tag_name,))
        r = self.cur.fetchone()
        if not r:
            return None
        return r[0].encode("latin1") if isinstance(r[0], str) else r[0]

    def value_record(self, tag_name):
        """Return the value record bytes a tag definition points at."""
        d = self._def_record(tag_name)
        if d is None:
            return None
        for off in range(0, len(d) - 4):
            oid = _u32(d, off)
            if oid in self.valrecs:
                return self.valrecs[oid]
        return None

    def fuel_type_names(self):
        import re
        rec = self.value_record("FuelTypeNames")
        names = []
        if rec:
            for m in re.finditer(rb"[A-Za-z0-9#][A-Za-z0-9 #]{2,19}", rec):
                names.append(m.group(0).decode())
        return names


def extract_multifuel_acd(path, temp_dir=None, progress=None) -> MultiFuelData:
    """Read both fuels' stored curve sets straight from a binary ``.ACD``."""
    try:
        from acd.api import load_acd
    except ImportError as exc:  # pragma: no cover
        raise ExtractError(
            "The 'acd-tools' library is required to read .ACD files.\n"
            "Install it with:  pip install -r requirements.txt") from exc

    cleanup = temp_dir is None
    temp_dir = temp_dir or tempfile.mkdtemp(prefix="acd_curve_")
    try:
        if progress:
            progress("Decrypting ACD…")
        try:
            project = load_acd(str(path), temp_dir=temp_dir)
        except Exception as exc:  # noqa: BLE001
            raise ExtractError(f"Could not parse ACD file:\n{exc}") from exc

        db = _AcdDb(os.path.join(temp_dir, "acd.db"))
        if not db.valrecs:
            raise ExtractError("No tag value records found in this ACD.")

        data = MultiFuelData(source_file=os.path.basename(str(path)))
        data.controller_name = getattr(project.controller, "_name", None)

        # O2-trim enable + active-fuel Cfg come from the clean DesiredO2 record.
        o2_rec = db.value_record("DesiredO2")
        o2_dec = _find_desired_facd(o2_rec) if o2_rec else None
        data.o2_trim_enabled = bool(o2_dec and (o2_dec["cfg"] >> O2CURVE_BIT) & 1)

        type_names = db.fuel_type_names()

        for n in FUEL_NUMBERS:
            fuel_name = type_names[n - 1] if len(type_names) >= n else f"Fuel {n}"
            fuel = FuelCurves(number=n, name=fuel_name)
            for col, suffix in COLUMN_SUFFIX.items():
                rec = db.value_record(f"ArrayMgmt_F{n}{suffix}")
                dec = _find_arraymgmt_curve(rec) if rec else None
                if dec:
                    fuel.columns[col] = ColumnCurve(
                        found=True,
                        purge="%.8e" % dec["purge"],
                        lightoff="%.8e" % dec["lightoff"],
                        curve=["%.8e" % x for x in dec["curve"]],
                    )
                else:
                    fuel.columns[col] = ColumnCurve(found=False)
            data.fuels.append(fuel)

        data.notes.append(
            "Fuel 1 = " + data.fuels[0].name + ", Fuel 2 = " + data.fuels[1].name
            + "  (read directly from the ACD).")
        data.notes.append(
            "O2 trim is %s (DesiredO2.Cfg.O2Curve = %d)."
            % (("enabled" if data.o2_trim_enabled else "disabled"),
               1 if data.o2_trim_enabled else 0))
        return data
    finally:
        if cleanup:
            import shutil
            shutil.rmtree(temp_dir, ignore_errors=True)


if __name__ == "__main__":
    import sys
    from fuel_curves import render_fuel_tables_text

    if len(sys.argv) < 2:
        print("usage: python acd_reader.py <file.ACD>")
        raise SystemExit(1)
    result = extract_multifuel_acd(sys.argv[1], progress=lambda m: print("[", m, "]"))
    print(f"Controller : {result.controller_name}")
    for note in result.notes:
        print(f"Note       : {note}")
    print()
    print(render_fuel_tables_text(result))
