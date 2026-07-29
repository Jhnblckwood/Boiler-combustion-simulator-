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


def _scalar(lookup, names):
    for nm in names:
        vals = decode_reals(lookup(nm))
        if vals:
            return vals[0]
    return None


def _array(lookup, names):
    best = None
    for nm in names:
        vals = decode_reals(lookup(nm))
        if vals and len(vals) > 1 and (best is None or len(vals) > len(best)):
            best = vals
    return best


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def build_watertube(lookup, source_file=None, controller_name=None) -> MultiFuelData:
    """Assemble the two-fuel table model.

    ``lookup(tag_name)`` returns that tag's raw value-record bytes, or ``None``.
    """
    data = MultiFuelData(source_file=source_file, controller_name=controller_name)
    data.column_order = WT_COLUMNS

    points = 0
    o2_has_data = False

    for number, key in enumerate(("gas", "oil"), start=1):
        fuel = FuelCurves(number=number, name=FUEL_TITLE[key])
        for col in WT_COLUMNS:
            curve_tpl, purge_tpl, ltoff_tpl = WT_TAGS[col]
            curve = _array(lookup, _candidates(curve_tpl, key))
            if curve is None:
                fuel.columns[col] = ColumnCurve(found=False)
                continue
            points = max(points, len(curve))
            if col == "O2" and any(abs(v) > 1e-6 for v in curve):
                o2_has_data = True
            fuel.columns[col] = ColumnCurve(
                found=True,
                purge=_scalar(lookup, _candidates(purge_tpl, key)),
                lightoff=_scalar(lookup, _candidates(ltoff_tpl, key)),
                curve=curve,
            )
        data.fuels.append(fuel)

    data.point_count = points or None
    # There's no single Cfg bit here the way the firetube DesiredO2 tag has, so
    # the O2 column is shown whenever an O2 characterizer actually holds data.
    data.o2_trim_enabled = o2_has_data

    data.notes.append(
        "Water-tube program — curves read from the "
        "<actuator>Characterizer_<fuel>_Y arrays (the _X breakpoint arrays are "
        "the firing-rate axis and are not shown).")
    data.notes.append(
        "O2 trim characterizer %s — O2 column %s."
        % (("has data" if o2_has_data else "is empty"),
           ("shown" if o2_has_data else "omitted")))
    return data
