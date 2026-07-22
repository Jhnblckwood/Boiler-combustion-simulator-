# Curve Script

A small desktop tool that reads a Rockwell **Studio 5000 / RSLogix 5000**
project and lays its fuel/air **curve** configuration out as a table.

Drag a `.ACD` file (or, later, an `.L5K` export) onto the window and it renders:

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

## What's wired up so far

Only one tag drives the table right now — **`DesiredO2`** (data type
`FuelAirCurveData`) — and it controls the **O2** column:

| Condition | Result |
|-----------|--------|
| `DesiredO2.Cfg.O2Curve = 0` | The **O2** column is **omitted** entirely. |
| `DesiredO2.Cfg.O2Curve = 1` | The **O2** column is shown. `purge` and `LtOff` are left blank; rows `1`–`16` are filled from `DesiredO2.Curve[0]`…`Curve[15]`. A value of zero is shown as `0`. |

The `Air`, `Fuel Act1`, `FGR` and `VFD` columns are laid out but left empty —
we still need to identify the tags/arrays that feed them.

> The sample file used to build this (`…RH150_29.ACD`) has
> `DesiredO2.Cfg.O2Curve = 0` and an all-zero curve — a blank template — so the
> tool correctly omits the O2 column for it.

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
python curve_extractor.py path/to/project.ACD
```

## Files

| File | Purpose |
|------|---------|
| `curve_gui.py` | The drag-and-drop GUI (tkinter). |
| `curve_extractor.py` | All the parsing/table logic — no GUI, importable & testable. |
| `requirements.txt` | Python dependencies. |

## Open questions / next steps

1. **`.L5K` support** — not implemented yet; drop a sample export in and the
   parser gets added (the `.ACD` path is fully working).
2. **Slot 16 of the O2 column** — the spec described filling points `1`–`15`
   from `Curve[0]`…`Curve[14]`, but the `Curve` array actually has **16**
   elements (`[0]`–`[15]`). Right now point `16` is filled from `Curve[15]`.
   Say the word if point `16` should instead be left blank.
3. **The other columns** — need the tags/arrays that feed `Air`, `Fuel Act1`,
   `FGR` and `VFD`.
