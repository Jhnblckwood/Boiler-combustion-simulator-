# Curve Script

A small desktop tool that reads a Rockwell **Studio 5000 / RSLogix 5000**
project and lays its fuel/air **curve** configuration out as a table.

Drag a `.ACD` file or an `.L5K` export onto the window and it renders:

```
gas    | Air | Fuel Act1 | FGR | VFD | O2
purge  |     |           |     |     |
LtOff  |     |           |     |     |
1      |     |           |     |     | Curve[0]
2      |     |           |     |     | Curve[1]
...    |     |           |     |     | ...
16     |     |           |     |     | Curve[15]
```

* **Rows** run down the left edge: the corner label `gas`, then `purge`,
  `LtOff`, then firing points `1`–`16`.
* **Columns** run across the top: `Air`, `Fuel Act1`, `FGR`, `VFD`, and `O2`.

## What's wired up

Each column is driven by one `FuelAirCurveData` tag:

| Column | Tag | `purge` row | `LtOff` row | Rows `1`–`16` |
|--------|-----|-------------|-------------|----------------|
| Air | `DesiredAir` | `.Purge` | `.LightOff` | `.Curve[0]`…`.Curve[15]` |
| Fuel Act1 | `DesiredFuel_A1` | `.Purge` | `.LightOff` | `.Curve[0]`…`.Curve[15]` |
| FGR | `DesiredFGR` | `.Purge` | `.LightOff` | `.Curve[0]`…`.Curve[15]` |
| VFD | `DesiredVFD` | `.Purge` | `.LightOff` | `.Curve[0]`…`.Curve[15]` |
| O2 | `DesiredO2` | *(blank)* | *(blank)* | `.Curve[0]`…`.Curve[15]` |

Rules:

* Values are **rounded to one decimal place** (e.g. `10.387982` → `10.4`).
* Any value that is **zero or missing is shown as `0`**.
* The **O2** column is a special case — its `purge` and `LtOff` rows are always
  left blank, and the whole column is shown **only when
  `DesiredO2.Cfg.O2Curve = 1`**. When it's `0` the O2 column is omitted.

### A note on `.ACD` vs `.L5K` values

**`.L5K` exports carry the real, commissioned values** and are fully parsed —
purge, light-off, all curves, and the `Cfg` bit-packed flags (`O2Curve` is
bit 3 of the packed config integer).

**`.ACD` files are read for tag *structure* only.** The open-source ACD library
this tool uses reconstructs each tag's layout but emits zero placeholders for
the stored values — it does not read a tag's initial-value buffer. So an `.ACD`
will show the correct rows/columns but zeros for the numbers. **For real values,
use the `.L5K` export.** (An `.ACD` that was never populated — e.g. an offline
template like `…RH150_29.ACD` — is genuinely all-zero anyway.)

## Requirements

* Python **3.8+**
* `tkinter` — ships with most Python installs. On some Linux distros:
  `sudo apt install python3-tk`
* The Python packages in `requirements.txt`

```bash
pip install -r requirements.txt
```

## Running

```bash
python curve_gui.py
```

Then drag a `.ACD` file onto the window (or click the drop zone to browse).

### Command line

The extractor also runs on its own and prints the table as text:

```bash
python curve_extractor.py path/to/project.L5K
```

## Files

| File | Purpose |
|------|---------|
| `curve_gui.py` | The drag-and-drop GUI (tkinter). |
| `curve_extractor.py` | All the parsing/table logic — no GUI, importable & testable. |
| `requirements.txt` | Python dependencies (only needed for `.ACD`; `.L5K` is pure-Python). |
