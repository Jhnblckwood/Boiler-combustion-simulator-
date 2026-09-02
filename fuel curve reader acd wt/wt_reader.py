"""
wt_reader.py
------------

Water-tube boiler curve reading.

Firetube programs keep their commissioned curve in ``FA_DataMgmt`` tags
(``ArrayMgmt_F1Air`` and friends). Water-tube programs are laid out completely
differently: each actuator has a **characterizer**, an X/Y pair of ``REAL``
arrays, one pair per fuel:

    AirCharacterizer_Gas_X        AirCharacterizer_Gas_Y
    FGRCharacterizer_Gas_X        FGRCharacterizer_Gas_Y
    FreshAirCharacterizer_Gas_X   FreshAirCharacterizer_Gas_Y
    GasCharacterizer_X            GasCharacterizer_Y
    OxygenTrimCharacterizer_Gas_X OxygenTrimCharacterizer_Gas_Y
    ... and the matching ``_Oil_`` / ``_No2Oil_`` set for #2 oil.

The **X** array is the firing-rate breakpoint axis (0, 10, 20 … 100) and is
ignored — the commissioned positions are the **Y** arrays.

Purge and light-off aren't part of the characterizer here; they're separate
scalar ``REAL`` tags (``FGRDamperPurgePosition``, ``GasValveLightoff`` …), so
they're read individually and shown as the ``purge`` / ``LtOff`` rows to match
the firetube table.

Tag naming varies between integrators (``_Oil_`` vs ``_No2Oil_``, ``Gas`` vs
``NaturalGas``), so every lookup tries a list of aliases.
"""

from __future__ import annotations

import math
import struct

from fuel_curves import ColumnCurve, FuelCurves, MultiFuelData

# ---------------------------------------------------------------------------
# Naming
# ---------------------------------------------------------------------------

# Fuel token aliases, in the order they're tried.
FUEL_ALIASES = {
    "gas": ["Gas", "NaturalGas", "NatGas", "NG"],
    "oil": ["Oil", "No2Oil", "No2_Oil", "NO2Oil", "No2FuelOil", "Oil2", "FuelOil"],
}

# What each fuel block is called in the output.
FUEL_TITLE = {"gas": "Gas", "oil": "Number 2 Oil"}

# Column  ->  (curve-tag templates, purge tag aliases, light-off tag templates)
#   {F} is replaced with each fuel-name alias in turn.
WT_COLUMNS = ["Air", "Fuel", "FGR", "Fresh Air", "O2"]

WT_TAGS = {
    "Air": (
        ["AirCharacterizer_{F}_Y", "FDFanAirCharacterizer_{F}_Y",
         "CombustionAirCharacterizer_{F}_Y"],
        ["FDFanAirDamperPurgePosition", "AirDamperPurgePosition", "PurgePosition"],
        ["FDFanAirDamperLightoffPosition_{F}", "AirDamperLightoffPosition_{F}",
         "AirDamperLightoff_{F}"],
    ),
    "Fuel": (
        ["{F}Characterizer_Y", "{F}ValveCharacterizer_Y",
         "FuelCharacterizer_{F}_Y"],
        [],                                   # no purge position for the fuel valve
        ["{F}ValveLightoff", "{F}ValveLightoffPosition", "FuelValveLightoff_{F}"],
    ),
    "FGR": (
        ["FGRCharacterizer_{F}_Y"],
        ["FGRDamperPurgePosition", "FGRPurgePosition"],
        ["FGRDamperLightoff_{F}", "FGRDamperLightoffPosition_{F}"],
    ),
    "Fresh Air": (
        ["FreshAirCharacterizer_{F}_Y"],
        ["FreshAirDamperPurgePosition", "FreshAirPurgePosition"],
        ["FreshAirDamperLightoff_{F}", "FreshAirDamperLightoffPosition_{F}"],
    ),
    "O2": (
        ["OxygenTrimCharacterizer_{F}_Y", "O2TrimCharacterizer_{F}_Y",
         "OxygenCharacterizer_{F}_Y"],
        [],                                   # O2 purge / light-off left blank
        [],
    ),
}

# Fresh air is optional on these boilers — when the loop is switched off the
# column is dropped rather than shown full of unused positions.
FRESH_AIR_ENABLE_TAGS = ["FreshAirLoopEnable", "FreshAirLoopEnabled",
                         "FreshAirEnable", "FreshAirLoop_Enable"]

# O2 trim state. This tag is inverted: in standby means trim is NOT running,
# so 0 = enabled and 1 = disabled.
O2_STANDBY_TAGS = ["OxygenTrimInStandby", "O2TrimInStandby",
                   "OxygenTrimStandby"]


def _candidates(templates, fuel_key):
    """Expand ``{F}`` in each template across that fuel's name aliases."""
    out = []
    for tpl in templates:
        if "{F}" not in tpl:
            out.append(tpl)
            continue
        for alias in FUEL_ALIASES[fuel_key]:
            out.append(tpl.replace("{F}", alias))
    return out


def _check_curve_templates():
    """Every curve template must read a ``_Y`` array — never an ``_X`` one.

    The ``_X`` arrays are the firing-rate breakpoint axis. Reading one would
    silently produce a plausible-looking 0/10/20…100 ramp instead of the
    commissioned positions, so the tag map is checked rather than trusted.
    """
    for col, (curve_tpl, _, _) in WT_TAGS.items():
        for tpl in curve_tpl:
            if not tpl.endswith("_Y") or "_X" in tpl:
                raise AssertionError(
                    "%s curve template %r must read a _Y array" % (col, tpl))


_check_curve_templates()


def has_characterizers(comps) -> bool:
    """True when this project uses the water-tube characterizer layout."""
    return any(k.endswith("_Y") and "haracterizer" in k for k in comps)


# ---------------------------------------------------------------------------
# Value decoding
# ---------------------------------------------------------------------------

def decode_reals(buf):
    """Pull the REAL payload off the end of a tag value record.

    The record ends with ``<u32 byte-count><that many bytes of little-endian
    float32>``, so the payload is found by locating the length word that
    exactly accounts for the remaining bytes. Works for both a scalar (4 bytes)
    and an array (4 x N).
    """
    if buf is None:
        return None
    n = len(buf)
    for k in range(0, n - 4):
        ln = struct.unpack_from("<I", buf, k)[0]
        if ln != n - k - 4 or ln < 4 or ln % 4:
            continue
        vals = list(struct.unpack_from("<%df" % (ln // 4), buf, k + 4))
        if all(math.isfinite(v) and -1e9 < v < 1e9 for v in vals):
            return vals
    return None


def decode_flag(buf):
    """Read a 4-byte scalar as an on/off flag — ``True``, ``False`` or ``None``.

    The raw bytes are compared against zero rather than decoded to a number,
    so this works whichever type the tag happens to be: a ``BOOL``, ``DINT`` or
    ``REAL`` is zero exactly when all four bytes are zero. (Decoding as float32
    would be wrong for an integer tag — a ``DINT`` of 1 reads as 1.4e-45.)
    """
    if buf is None:
        return None
    n = len(buf)
    for k in range(0, n - 4):
        ln = struct.unpack_from("<I", buf, k)[0]
        if ln != n - k - 4:
            continue
        if ln != 4:                       # not a scalar payload
            return None
        return struct.unpack_from("<i", buf, k + 4)[0] != 0
    return None


def _flag(lookup, names):
    """First name that resolves to a scalar flag wins; ``(value, tag_name)``."""
    for nm in names:
        val = decode_flag(lookup(nm))
        if val is not None:
            return val, nm
    return None, None


def _scalar(lookup, names):
    """First name that resolves wins; returns ``(value, tag_name)``."""
    for nm in names:
        vals = decode_reals(lookup(nm))
        if vals:
            return vals[0], nm
    return None, None


def _array(lookup, names):
    """First name that resolves to a real array wins.

    The alias lists are priority-ordered, so a project that happens to carry
    both ``_Oil_`` and ``_No2Oil_`` tags resolves to the canonical name rather
    than to whichever array happens to be longer.
    """
    for nm in names:
        vals = decode_reals(lookup(nm))
        if vals and len(vals) > 1:
            return vals, nm
    return None, None


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def build_watertube(lookup, source_file=None, controller_name=None) -> MultiFuelData:
    """Assemble the two-fuel table model.

    ``lookup(tag_name)`` returns that tag's raw value-record bytes, or ``None``.
    """
    data = MultiFuelData(source_file=source_file, controller_name=controller_name)

    # Fresh air loop off -> drop the column entirely. If the tag can't be read
    # the column stays: hiding a commissioned curve is worse than showing one
    # the boiler doesn't use.
    fresh_air_on, fa_tag = _flag(lookup, FRESH_AIR_ENABLE_TAGS)
    columns = [c for c in WT_COLUMNS
               if not (c == "Fresh Air" and fresh_air_on is False)]
    data.column_order = columns
    data.fresh_air_on = fresh_air_on
    data.fresh_air_tag = fa_tag

    points = 0
    o2_tags = []          # per-fuel O2 trim source, for `--tags`

    for number, key in enumerate(("gas", "oil"), start=1):
        fuel = FuelCurves(number=number, name=FUEL_TITLE[key])
        for col in columns:
            curve_tpl, purge_tpl, ltoff_tpl = WT_TAGS[col]
            curve, tag = _array(lookup, _candidates(curve_tpl, key))
            if curve is None:
                fuel.columns[col] = ColumnCurve(found=False)
                continue
            points = max(points, len(curve))
            # Each fuel trims off its own characterizer, so record which tag
            # actually supplied it rather than assuming the two fuels match.
            if col == "O2" and any(abs(v) > 1e-6 for v in curve):
                o2_tags.append((FUEL_TITLE[key], tag))
            purge, purge_tag = _scalar(lookup, _candidates(purge_tpl, key))
            ltoff, ltoff_tag = _scalar(lookup, _candidates(ltoff_tpl, key))
            cc = ColumnCurve(found=True, purge=purge, lightoff=ltoff, curve=curve)
            # Which tags actually fed this column, for `--tags`.
            cc.source_tag = tag
            cc.purge_tag = purge_tag
            cc.lightoff_tag = ltoff_tag
            fuel.columns[col] = cc
        data.fuels.append(fuel)

    data.point_count = points or None

    # O2 trim comes from the standby tag, which is inverted: 0 = enabled.
    # Falls back to "is there any characterizer data" if the tag is missing.
    standby, o2_tag = _flag(lookup, O2_STANDBY_TAGS)
    data.o2_trim_enabled = (not standby) if standby is not None else bool(o2_tags)
    data.o2_source_tag = o2_tag
    data.o2_curve_tags = o2_tags

    data.notes.append("Water-tube program.")
    data.notes.append(
        "O2 trim " + ("enabled." if data.o2_trim_enabled else "disabled."))
    return data
