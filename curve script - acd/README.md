# Curve Script — ACD + L5K

This version reads **both `.ACD` and `.L5K`** files and shows the **stored
per-fuel curve sets** (Fuel 1 and Fuel 2) with real commissioned values.

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

The open-source `acd-tools` library rebuilds tag *structure* but writes zeros for
the values. So `acd_reader.py` goes further: it uses the library only to
decompress the ACD, then reads the values from the raw binary records —

1. each tag definition record holds a 4-byte pointer to its **value record**
   (a child of `RxDataCollection`);
2. `DesiredX` value records are clean `FuelAirCurveData` blobs (used for the
   `O2Curve` enable bit);
3. `ArrayMgmt_F<n>X` value records store the curve as `Ref_Data` + an identical
   working copy — located by that "double curve" signature.

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
python fuel_curves.py path/to/project.L5K     # L5K
python acd_reader.py  path/to/project.ACD     # ACD
```

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
| `acd_reader.py` | Reads real curve values straight from a binary `.ACD`. |
| `fuel_curves.py` | `.L5K` extraction + shared table building. |
| `curve_extractor.py` | Shared helpers / original single-set extractor. |
| `Fuel Curve Reader.html` | No-install single-file reader (`.L5K` only). |
| `requirements.txt` | Python dependencies (needed for `.ACD`). |
