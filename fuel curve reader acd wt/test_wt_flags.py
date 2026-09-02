"""Exercise the new flag logic against synthetic value records.

A value record ends with <u32 byte-count><payload>, with padding before it —
the layout observed in the real V37 file (464-byte array records with the
marker at 416, 424-byte scalar records).
"""
import os, struct, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wt_reader as wt

PAD = 416

def rec(payload: bytes) -> bytes:
    return b"\x00" * PAD + struct.pack("<I", len(payload)) + payload

def real_array(vals):  return rec(struct.pack("<%df" % len(vals), *vals))
def real_scalar(v):    return rec(struct.pack("<f", v))
def dint(v):           return rec(struct.pack("<i", v))
def bool_(v):          return rec(struct.pack("<i", 1 if v else 0))

CURVE = [0,10,20,30,40,50,60,70,80,90,100]

def make_tags(fresh=None, standby=None, fresh_type=dint, standby_type=dint):
    t = {}
    for f in ("Gas", "Oil"):
        t["AirCharacterizer_%s_Y" % f]        = real_array(CURVE)
        t["FGRCharacterizer_%s_Y" % f]        = real_array(CURVE)
        t["FreshAirCharacterizer_%s_Y" % f]   = real_array(CURVE)
        t["OxygenTrimCharacterizer_%s_Y" % f] = real_array([9,8,7,6,5,4,3,3,3,3,3])
        t["%sCharacterizer_Y" % f]            = real_array(CURVE)
        t["FDFanAirDamperLightoffPosition_%s" % f] = real_scalar(2.0)
        t["FGRDamperLightoff_%s" % f]              = real_scalar(0.0)
        t["FreshAirDamperLightoff_%s" % f]         = real_scalar(100.0)
    t["GasValveLightoff"] = real_scalar(17.0)
    t["OilValveLightoff"] = real_scalar(5.0)
    for n in ("FDFanAirDamperPurgePosition","FGRDamperPurgePosition",
              "FreshAirDamperPurgePosition"):
        t[n] = real_scalar(100.0)
    if fresh   is not None: t["FreshAirLoopEnable"]  = fresh_type(fresh)
    if standby is not None: t["OxygenTrimInStandby"] = standby_type(standby)
    return t

def run(**kw):
    tags = make_tags(**kw)
    return wt.build_watertube(lambda n: tags.get(n), source_file="synthetic.ACD")

def check(label, got, want):
    ok = got == want
    print("%-58s %-28s %s" % (label, got, "ok" if ok else "FAIL want %s" % (want,)))
    return ok

def main():
    fails = 0
    print("== fresh air loop ==")
    d = run(fresh=0, standby=0)
    fails += not check("FreshAirLoopEnable=0 -> columns", d.columns, ["Air","Fuel","FGR","O2"])
    d = run(fresh=1, standby=0)
    fails += not check("FreshAirLoopEnable=1 -> columns", d.columns, ["Air","Fuel","FGR","Fresh Air","O2"])
    d = run(standby=0)
    fails += not check("tag absent -> column kept", d.columns, ["Air","Fuel","FGR","Fresh Air","O2"])

    print()
    print("== O2 trim (inverted: 0 = enabled) ==")
    for sb, want in ((0, True), (1, False)):
        d = run(fresh=1, standby=sb)
        fails += not check("OxygenTrimInStandby=%d -> o2_trim_enabled" % sb, d.o2_trim_enabled, want)
    for t, name in ((dint, "DINT"), (bool_, "BOOL"), (real_scalar, "REAL")):
        d = run(fresh=1, standby=1, standby_type=t)
        fails += not check("standby=1 as %-4s -> disabled" % name, d.o2_trim_enabled, False)
        d = run(fresh=1, standby=0, standby_type=t)
        fails += not check("standby=0 as %-4s -> enabled" % name, d.o2_trim_enabled, True)
    d = run(fresh=1)
    fails += not check("tag absent -> falls back to curve data", d.o2_trim_enabled, True)

    print()
    print("== footer ==")
    d = run(fresh=1, standby=0)
    fails += not check("notes", d.notes, ["Water-tube program.", "O2 trim enabled."])
    d = run(fresh=1, standby=1)
    fails += not check("notes", d.notes, ["Water-tube program.", "O2 trim disabled."])

    print()
    print("FAILURES:", fails)
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())
