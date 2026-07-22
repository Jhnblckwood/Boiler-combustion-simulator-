# Curve Script — multi-fuel

This is the **multi-fuel** version of `curve script`. The original folder
(`curve script`) reads the single *active* curve set; this one reads the
**stored per-fuel curve sets**, so you see every fuel at once.

> The original `curve script` folder is left untouched as a working checkpoint.

## What it does

Drop a Studio 5000 file on the window:

* **`.L5K`** → renders a table **per fuel** (Fuel 1 and Fuel 2), read from the
  `ArrayMgmt_F*` tags.
* **`.ACD`** → tag structure only (the open-source ACD library can't read
  stored values — use `.L5K` for real numbers).

Each fuel table:

```
<fuel name>  Air   Fuel Act1  FGR   VFD   O2
purge        95.1             1.1   100          ← Fuel Act1 + O2 purge blank
LtOff        7.4   17.4       1.1   70.2         ← O2 LtOff blank
1            9.5   21.3       10.4  72.1  7.9
...
16           0     0          0     0     0
```

## Where the data comes from

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
pip install -r requirements.txt      # only needed for .ACD; .L5K is pure-Python
python curve_gui.py
```

Command line:

```bash
python fuel_curves.py path/to/project.L5K
```

## Files

| File | Purpose |
|------|---------|
| `curve_gui.py` | Drag-and-drop GUI. |
| `fuel_curves.py` | Multi-fuel `.L5K` extraction + table building. |
| `curve_extractor.py` | Original single-set extractor (used for `.ACD` + shared helpers). |
| `requirements.txt` | Python dependencies. |
