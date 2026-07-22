"""
acd_reader.py
-------------

Read the per-fuel curve sets **directly out of a binary `.ACD`** — including the
real commissioned values — with **no third-party libraries** (Python stdlib
only). This makes it work across ACD versions (verified on V20 and V35);
the open-source `acd-tools` library is not used because it (a) writes zeros for
tag values and (b) fails to parse older V20 projects.

How it works:

  1. Unzip the ACD container and gzip-decompress the ``Comps.Dat`` stream.
  2. Walk the ``Comps.Dat`` records. The record header shrank by 4 bytes
     between V20 and V35, so the tag-name offset is auto-detected; in every
     layout ``object_id = name-8`` and ``parent_id = name-4``.
  3. Each tag definition record holds a 4-byte pointer to its **value record**
     (a child of the tag-value collection).
  4. ``DesiredX`` value records are clean ``FuelAirCurveData`` blobs (used for
     the ``O2Curve`` enable bit); ``ArrayMgmt_F<n>X`` records store the curve as
     ``Ref_Data`` + an identical working copy, found by that double signature.

Verified: V35 (RH800) matches its L5K export exactly; V20 (RH250) cross-checks
internally (active ``Desired*`` == ``ArrayMgmt_F1*``, Cfg bits Air=1/Fuel=2/VFD=4).
"""

from __future__ import annotations

import gzip
import math
import os
import struct

from fuel_curves import (
    MultiFuelData, FuelCurves, ColumnCurve, COLUMN_SUFFIX, FUEL_NUMBERS,
)
from curve_extractor import ExtractError

O2CURVE_BIT = 3
_NAME_OFFSETS = (30, 26, 34, 38, 22)   # V35, V20, and room for other versions


def _u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


def _u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


# ---------------------------------------------------------------------------
# ACD container + comps database
# ---------------------------------------------------------------------------

def _find_stream(raw, wanted):
    """Return the (still-compressed) bytes of one embedded ACD stream."""
    size = len(raw)
    no_files = _u32(raw, size - 8)
    p = size - no_files * 528 - 8
    for _ in range(no_files):
        q = p
        name = b""
        while True:
            c = raw[q:q + 2]
            q += 2
            if c == b"\x00\x00":
                break
            name += c
        flen, foff = struct.unpack_from("<II", raw, p + 520)
        if name.decode("utf-16-le") == wanted:
            return raw[foff:foff + flen]
        p += 528
    return None


def _walk_start(b):
    prr = _u32(b, _u32(b, 12) + 18)      # pointer-records region (5th u32)
    return prr + _u32(b, prr + 2)        # + record-header length


def _detect_name_offset(b, start, no):
    best, best_score = 30, -1
    for off in _NAME_OFFSETS:
        p, ok, seen = start, 0, 0
        for _ in range(no):
            if seen >= 400 or p + off + 4 >= len(b):
                break
            outer = _u32(b, p + 2)
            if outer < 10 or p + outer > len(b) + 8:
                break
            if _u16(b, p) == 0xFAFA:
                seen += 1
                c0 = b[p + off]
                if b[p + off + 1] == 0 and (
                        65 <= c0 <= 90 or 97 <= c0 <= 122 or c0 in (36, 95)):
                    ok += 1
            p += outer
        if seen > 10 and ok > best_score:
            best_score, best = ok, off
    return best


def _walk_comps(b):
    """Return {name: (object_id, parent_id, buffer)} and {oid: (name, parent, buffer)}."""
    no = _u32(b, 20)
    start = _walk_start(b)
    name_off = _detect_name_offset(b, start, no)
    comps, by_id = {}, {}
    p, count = start, 0
    while count < no and p + 6 <= len(b):
        outer = _u32(b, p + 2)
        if outer < 10 or p + outer > len(b) + 8:
            break
        if _u16(b, p) == 0xFAFA and p + name_off + 124 <= len(b):
            name_abs = p + name_off
            object_id = _u32(b, name_abs - 8)
            parent_id = _u32(b, name_abs - 4)
            end = name_abs
            while end < name_abs + 124 and b[end:end + 2] != b"\x00\x00":
                end += 2
            name = b[name_abs:end].decode("utf-16-le", "replace")
            buffer = b[name_abs + 124:p + outer]
            comps[name] = (object_id, parent_id, buffer)
            by_id[object_id] = (name, parent_id, buffer)
        p += outer
        count += 1
    return comps, by_id


# ---------------------------------------------------------------------------
# Value-record decoding
# ---------------------------------------------------------------------------

def _decode_desired(buf):
    """Clean FuelAirCurveData blob: Name, Purge, LightOff, Curve[16], ..., Cfg."""
    n = len(buf)
    for off in range(0, n - 136):
        ln = _u32(buf, off)
        if not (1 <= ln <= 20):
            continue
        s = buf[off + 4:off + 4 + ln]
        if not all(32 <= c < 127 for c in s):
            continue
        c0 = s[0]
        if not (65 <= c0 <= 90 or 97 <= c0 <= 122):
            continue
        curve = list(struct.unpack_from("<16f", buf, off + 32))
        if not all(math.isfinite(x) and -1e5 < x < 1e5 for x in curve):
            continue
        return {
            "purge": struct.unpack_from("<f", buf, off + 24)[0],
            "lightoff": struct.unpack_from("<f", buf, off + 28)[0],
            "curve": curve,
            "cfg": struct.unpack_from("<i", buf, off + 132)[0],
        }
    return None


def _decode_array(buf):
    """FA_DataMgmt: curve stored as Ref_Data + identical working copy."""
    n = len(buf)
    best, best_score = None, -1
    for i in range(16, n - 128):
        c0 = struct.unpack_from("<f", buf, i)[0]
        if not (math.isfinite(c0) and abs(c0) > 0.05):
            continue
        c1 = struct.unpack_from("<16f", buf, i)
        if not all(math.isfinite(x) and -1e5 < x < 1e5 for x in c1):
            continue
        c2 = struct.unpack_from("<16f", buf, i + 64)
        if not all(abs(a - b) < 1e-3 for a, b in zip(c1, c2)):
            continue
        p0, p1 = struct.unpack_from("<ff", buf, i - 16)
        l0, l1 = struct.unpack_from("<ff", buf, i - 8)
        if not (abs(p0 - p1) < 1e-3 and abs(l0 - l1) < 1e-3):
            continue
        score = sum(1 for x in c1 if abs(x) > 0.1)
        if score > best_score:
            best_score = score
            best = {"purge": p0, "lightoff": l0, "curve": list(c1)}
    return best


def _refs_of(comps, by_id, tag):
    entry = comps.get(tag)
    if not entry:
        return []
    buf = entry[2]
    out, seen = [], set()
    for o in range(0, len(buf) - 4):
        oid = _u32(buf, o)
        if oid in by_id and oid not in seen:
            seen.add(oid)
            out.append(oid)
    return out


def _fuel_type_names(comps, by_id, value_rec):
    import re
    rec = value_rec("FuelTypeNames")
    names = []
    if rec:
        txt = "".join(chr(c) if 32 <= c < 127 else "\n" for c in rec)
        for m in re.finditer(r"[A-Za-z0-9#][A-Za-z0-9 #]{2,19}", txt):
            names.append(m.group(0))
    return names


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def extract_multifuel_acd(path, progress=None) -> MultiFuelData:
    """Read both fuels' stored curve sets straight from a binary ``.ACD``."""
    if progress:
        progress("Decrypting ACD…")
    try:
        raw = open(path, "rb").read()
    except OSError as exc:
        raise ExtractError(f"Could not read ACD file:\n{exc}") from exc

    comps_stream = _find_stream(raw, "Comps.Dat")
    if comps_stream is None:
        raise ExtractError("Not a valid ACD (no Comps.Dat stream).")
    if comps_stream[:2] == b"\x1f\x8b":
        comps_stream = gzip.decompress(comps_stream)

    comps, by_id = _walk_comps(comps_stream)
    if "DesiredO2" not in comps and "ArrayMgmt_F1FGR" not in comps:
        raise ExtractError("Could not locate curve tags in this ACD.")

    # Value-record collection parent + DesiredO2 config.
    vp, o2dec = None, None
    for oid in _refs_of(comps, by_id, "DesiredO2"):
        dec = _decode_desired(by_id[oid][2])
        if dec and abs(dec["curve"][0]) > 0.01:
            vp, o2dec = by_id[oid][1], dec
            break

    def value_rec(tag):
        for oid in _refs_of(comps, by_id, tag):
            if vp is None or by_id[oid][1] == vp:
                return by_id[oid][2]
        return None

    data = MultiFuelData(source_file=os.path.basename(str(path)))
    controller = comps.get("Controller")
    data.controller_name = None
    data.o2_trim_enabled = bool(o2dec and (o2dec["cfg"] >> O2CURVE_BIT) & 1)

    type_names = _fuel_type_names(comps, by_id, value_rec)

    for n in FUEL_NUMBERS:
        fuel_name = type_names[n - 1] if len(type_names) >= n else f"Fuel {n}"
        fuel = FuelCurves(number=n, name=fuel_name)
        for col, suffix in COLUMN_SUFFIX.items():
            rec = value_rec(f"ArrayMgmt_F{n}{suffix}")
            dec = _decode_array(rec) if rec else None
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


if __name__ == "__main__":
    import sys
    from fuel_curves import render_fuel_tables_text

    if len(sys.argv) < 2:
        print("usage: python acd_reader.py <file.ACD>")
        raise SystemExit(1)
    result = extract_multifuel_acd(sys.argv[1], progress=lambda m: print("[", m, "]"))
    for note in result.notes:
        print(f"Note       : {note}")
    print()
    print(render_fuel_tables_text(result))
