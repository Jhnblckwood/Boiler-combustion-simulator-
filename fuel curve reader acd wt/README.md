# Curve Script — ACD + L5K (Water Tube Boilers)

This version reads **both `.ACD` and `.L5K`** files and shows the **stored
per-fuel curve sets** (Fuel 1 and Fuel 2) with real commissioned values for
**water tube boiler applications**.

> **Note:** The `curve script - acd` folder contains the corresponding **firetube boiler** version.
> The earlier `curve script` and `curve script - multi-fuel` folders are left
> untouched as working checkpoints.

## Python version (reads ACD and L5K)

Run `python curve_gui.py` and drop a Studio 5000 file on the window — it
**auto-detects** the type:

* **`.L5K`** → parses the text export directly.
* **`.ACD`** → shows a **"Decrypting ACD…"** popup while it unpacks the file,
  then reads the real values straight out of the binary and shows the same
  tables. (It's not literally decrypting — it's decompressing the ACD's internal
  databases and decoding the tag value records.)

Either way you get the identical two-fuel output — verified byte-for-byte
against the same project's ACD and L5K.

### How ACD reading works (the hard part)

`acd_reader.py` is **pure Python standard library** — no third-party packages.
(The open-source `acd-tools` library only rebuilds tag *structure*, writes zeros
for the values, and fails to parse older V20 projects, so it isn't used.)

1. Unzip the ACD container and gzip-decompress the `Comps.Dat` stream.
2. Walk the `Comps.Dat` records. The record header shrank by 4 bytes between
   V20 and V35, so the tag-name offset is **auto-detected**; in every layout
   `object_id = name-8` and `parent_id = name-4`.
3. Each tag definition record holds a 4-byte pointer to its **value record**.
4. `DesiredX` value records are clean `FuelAirCurveData` blobs (used for the
   `O2Curve` enable bit); `ArrayMgmt_F<n>X` records store the curve as
   `Ref_Data` + an identical working copy — located by that "double curve"
   signature.

**Versions:** verified on **V20** (RH250), **V35** (RH800) and **V37**
(CCS_230021, water tube). The auto-detection is built to carry across the
versions in between and newer ones; if a future file reads wrong, it's usually
one more name-offset to add.

### The `.L5K` path

Reads each fuel's curves from the `ArrayMgmt_F*` tags in the text export.

Each fuel table:

```
<fuel name>  Air   Fuel Act1  FGR   VFD   O2
purge        95.1             1.1   100          ← Fuel Act1 + O2 purge blank
LtOff        7.4   17.4       1.1   70.2         ← O2 LtOff blank
1            9.5   21.3       10.4  72.1  7.9
...
16           0     0          0     0     0
```

## Water-tube programs (characterizer arrays)

Water-tube programs don't use `FA_DataMgmt` tags at all. Every actuator gets a
**characterizer** — an X/Y pair of `REAL` arrays, one pair per fuel:

```
AirCharacterizer_Gas_X          AirCharacterizer_Gas_Y
FGRCharacterizer_Gas_X          FGRCharacterizer_Gas_Y
FreshAirCharacterizer_Gas_X     FreshAirCharacterizer_Gas_Y
GasCharacterizer_X              GasCharacterizer_Y
OxygenTrimCharacterizer_Gas_X   OxygenTrimCharacterizer_Gas_Y
```

…plus the matching `_Oil_` / `_No2Oil_` set for #2 oil.

The **`_X` array is the firing-rate breakpoint axis** (0, 10, 20 … 100) and is
**ignored** — the commissioned positions are the **`_Y`** arrays.

| Column | Curve tag (fuel *f*) | purge | LtOff |
|--------|----------------------|-------|-------|
| Air | `AirCharacterizer_<f>_Y` | `FDFanAirDamperPurgePosition` | `FDFanAirDamperLightoffPosition_<f>` |
| Fuel | `GasCharacterizer_Y` / `OilCharacterizer_Y` | *(blank)* | `GasValveLightoff` / `OilValveLightoff` |
| FGR | `FGRCharacterizer_<f>_Y` | `FGRDamperPurgePosition` | `FGRDamperLightoff_<f>` |
| Fresh Air | `FreshAirCharacterizer_<f>_Y` | `FreshAirDamperPurgePosition` | `FreshAirDamperLightoff_<f>` |
| O2 | `OxygenTrimCharacterizer_<f>_Y` | *(blank)* | *(blank)* |

Purge and light-off aren't part of the characterizer here — they're separate
scalar `REAL` tags, read individually so the table keeps the same
`purge` / `LtOff` / numbered-point shape as the firetube output.

Fuel blocks print as **Gas** and **Number 2 Oil**. Tag naming varies between
integrators (`_Oil_` vs `_No2Oil_`, `Gas` vs `NaturalGas`), so every lookup
tries a list of aliases.

**Row count is taken from the file** — the sample water-tube project
(`CCS_230021`, V37) has 11 curve points rather than the firetube's 16.

### O2 trim

Each fuel trims off **its own** characterizer — `OxygenTrimCharacterizer_Gas_Y`
for gas, `OxygenTrimCharacterizer_Oil_Y` for #2 oil — and the note under the
table names the tag each fuel's column actually came from.

The column is shown when an O2 characterizer holds data. (Unlike firetube,
there's no `DesiredO2.Cfg.O2Curve` bit to gate on — the water-tube
`OxygenTrimEnableDisable` tag is a `REAL` setpoint, not an enable flag.)

> The two fuels having identical O2 numbers is normal in an uncommissioned
> file; they're read from separate tags, so real differences do show up.

### Auto-detection

Drop either kind of file in and the right reader runs: if the project has
`*Characterizer_*_Y` tags it's read as **water tube**, otherwise it falls back
to the **firetube** `ArrayMgmt_F*` path. A project with neither reports
*"Current boiler is configured with linkage, not actuators. No curve stored"*.

## Firetube: where the data comes from

Each fuel stores its curves in `ArrayMgmt_F<n><col>` tags (type `FA_DataMgmt`):

| Column | Tag (fuel *n*) | Source |
|--------|----------------|--------|
| Air | `ArrayMgmt_F<n>Air` | `Ref_Data.Curve[0..15]` |
| Fuel Act1 | `ArrayMgmt_F<n>A1` | `Ref_Data.Curve[0..15]` |
| FGR | `ArrayMgmt_F<n>FGR` | `Ref_Data.Curve[0..15]` |
| VFD | `ArrayMgmt_F<n>VFD` | `Ref_Data.Curve[0..15]` |
| O2 | `ArrayMgmt_F<n>O2` | `Ref_Data.Curve[0..15]` |

In the L5K backing-tag layout the curve is the first 16-element array, with
`Purge` and `LightOff` as the two 2-element arrays just before it. The
`ArrayMgmt_F<n>B*` ("B" / backup) tags are ignored.

### Rules

* Values rounded to **one decimal place**; zero/missing shows as `0`.
* **Fuel Act1** — purge cell left blank.
* **O2** — purge and LtOff left blank; the O2 column only appears for a fuel
  that actually has O2-trim data.

### Fuel names

Fuel 1 and Fuel 2 are labelled from the file's fuel config
(`FuelTypeNames` + the burner fuel-selection enum, where `0 = Gas`,
`1 = #2 Oil`). In the sample export:

* **Fuel 1 = Natural Gas**  ← this is what the active `Desired*` curves match
* **Fuel 2 = #2 Fuel Oil**

> Note: the active `Desired*` set in this file matches **Fuel 1 (Natural Gas)**.
> If your F1/F2 numbering is meant to be the other way round, say so and the
> labels flip.

## Running

```bash
pip install -r requirements.txt      # optional — only for real drag-and-drop
python curve_gui.py
```

Command line:

```bash
python fuel_curves.py path/to/project.L5K            # L5K
python acd_reader.py  path/to/project.ACD            # ACD
python acd_reader.py --tags path/to/project.ACD      # + which tag fed each column
```

`--tags` prints the resolved tag for every column of every fuel. Worth a look
on a file whose naming hasn't been seen before — it's how you confirm the
reader latched onto the right arrays:

```
Fuel 1 — Gas
  Air        curve  AirCharacterizer_Gas_Y
             purge  FDFanAirDamperPurgePosition = 100
             lightoff FDFanAirDamperLightoffPosition_Gas = 2
  Fuel       curve  GasCharacterizer_Y
             lightoff GasValveLightoff = 17
  FGR        curve  FGRCharacterizer_Gas_Y
             purge  FGRDamperPurgePosition = 100
             lightoff FGRDamperLightoff_Gas = 0
  Fresh Air  curve  FreshAirCharacterizer_Gas_Y
             purge  FreshAirDamperPurgePosition = 100
             lightoff FreshAirDamperLightoff_Gas = 100
  O2         curve  OxygenTrimCharacterizer_Gas_Y
Fuel 2 — Number 2 Oil
  Air        curve  AirCharacterizer_Oil_Y
  ...
```

Oil is the gas tag set with the fuel token swapped, and every column reads a
**`_Y`** array. That's enforced, not just intended — `wt_reader` refuses to
start if any curve template points at an `_X` array, since reading one would
quietly yield a plausible 0/10/20…100 ramp instead of real positions.

## The HTML version (`Fuel Curve Reader.html`) — now reads ACD too

A single double-click file that needs **no install** — and it now reads **both
`.ACD` and `.L5K`**. Open it in a modern browser (Chrome, Edge, Firefox) and
drop either file in. It unzips and decodes the binary ACD **entirely in the
browser** (using the built-in gzip decompressor), so nothing is uploaded
anywhere and no Python is needed. Verified in-browser against RH800_27 — the
ACD and L5K produce the identical table.

## Files

| File | Purpose |
|------|---------|
| `curve_gui.py` | Drag-and-drop GUI; auto-detects `.ACD` / `.L5K`, shows the "Decrypting ACD…" popup. |
| `acd_reader.py` | Reads real curve values straight from a binary `.ACD`; picks the water-tube or firetube path. |
| `wt_reader.py` | Water-tube characterizer decoding (tag map, aliases, `REAL` array payloads). |
| `fuel_curves.py` | `.L5K` extraction + shared table building (column set and point count are per-file). |
| `curve_extractor.py` | Shared helpers / original single-set extractor. |
| `Fuel Curve Reader.html` | No-install single-file reader — `.ACD` + `.L5K`, water tube + firetube. |
| `requirements.txt` | Optional dep (`tkinterdnd2`) for drag-and-drop; readers need nothing. |
