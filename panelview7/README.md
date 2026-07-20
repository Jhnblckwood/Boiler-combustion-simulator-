# Valve Proving — PanelView Plus 7 (FactoryTalk View ME)

The same valve-proving HMI as the FactoryTalk Optix projects under `optix/`,
retargeted to a **PanelView Plus 7** running **FactoryTalk View Studio – Machine
Edition (ME)**. It mirrors the most complete Optix screen — the gas-pressure
variant (VP1/VP2/VPS + LGP/HGP + pilot + adjustable setpoints). All pressures are
in inches of water column (in. H2O).

## Read this first — how PanelView differs from Optix
Optix let the whole state machine live in a C# NetLogic. **PanelView Plus 7 / View
ME has no embedded scripting**, so the design is split the way a real machine is:

- **The PLC runs the proving sequence** — `plc/ValveProving.st` (Structured Text
  for Studio 5000). It is a faithful port of the Optix logic: same steps, timers,
  lights, LGP/HGP/VPS supervision, pilot handling, AUTO and MANUAL drill, and leak
  simulation.
- **The PanelView only displays tags and sends button presses.** Every animation
  is a tag-driven expression on a graphic object.

Because ME projects (`.mer`) are built in the tool and can't be reliably
hand-authored end-to-end (and I couldn't run FT View here to validate), this folder
gives you the parts that *are* portable plus an authoritative build spec:

| File | What it is | Reliability |
|------|-----------|-------------|
| `plc/ValveProving.st` | The sequence, Structured Text | Complete, correct — paste into Studio 5000 |
| `plc/ValveProving_PLC_tags.md` | Controller tag list | Complete |
| `tags/ValveProving_HMI_Tags.csv` | FT View ME tag DB import | Documented ME 6.x format — imports via the wizard |
| `displays/ValveProving_<W>x<H>.xml` | Graphic display **starters, one per panel size** | Best-effort scaffolds; validate/expand in Studio |
| `displays/generate_layouts.py` | Regenerates all size layouts from the 1280×800 master | Edit the master list, re-run, all sizes update |
| this README → **Screen build spec** | Exact objects, colors, animations, actions | Authoritative — build from this if the XML needs work |

## Build order in FactoryTalk View Studio (ME)
1. Create a new ME application sized for your PanelView Plus 7 (e.g. 1280×800).
2. **Tags** — `Tools ▸ Tag Import and Export Wizard ▸ Import`, pick
   `tags/ValveProving_HMI_Tags.csv`. Keep the `;###002 …` header line. These are
   **memory** tags so the screen imports and binds standalone; when you connect the
   PLC, repoint them at the controller tags (same names) through a device shortcut.
3. **PLC** — paste `plc/ValveProving.st` into a routine in a **100 ms periodic
   task**, create the tags in `plc/ValveProving_PLC_tags.md`, download. (For a
   PLC-less demo you can drive the memory bits by hand, but the sequence only
   advances when the ST is running — ME can't run it.)
4. **Display** — pick the XML matching your panel and try importing it
   (`Tools ▸ Import and Export Wizard`). If any object is rejected, build it from
   the spec below and paste in the connection expression the XML shows for it.

   | Your PanelView Plus 7 | Resolution | File |
   |---|---|---|
   | 7" or 9" widescreen | 800×480 | `displays/ValveProving_800x480.xml` |
   | 6.5" or 10.4" | 640×480 | `displays/ValveProving_640x480.xml` |
   | 12.1" widescreen | 1280×800 | `displays/ValveProving_1280x800.xml` |
   | 15" | 1024×768 | `displays/ValveProving_1024x768.xml` |
   | 19" | 1280×1024 | `displays/ValveProving_1280x1024.xml` |

   All five are generated from one master by `displays/generate_layouts.py`
   (uniform scaling, fonts floored at 8 px). The spec table below lists the
   **1280×800 master** coordinates; the XMLs carry the scaled ones. On the
   800×480 and especially the 640×480 panels this dense screen gets tight —
   buttons land around 72–91 px wide. It works with a stylus/finger, but if
   it feels cramped in practice, the clean fix is splitting the controls onto
   a second display and keeping the gas train on the first.

## Screen build spec (authoritative)
Layout matches the Optix screen; pixels on a 1280×800 display.

**Gauges** (ME Gauge object, Min 0 / Max 80, connection = tag):
- Inlet `SupplyPressure` @ (40,150) — **the only operator-adjustable gauge**
- LGP `SupplyPressure` @ (190,150) · Test-volume `ChamberPressure` @ (560,132)
- HGP `DownstreamPressure` @ (1000,150). Numeric display of `SupplyPressure`
  (1 decimal) @ (72,235).

**Piping** (Rectangle, fill-color animation, gray `#3B4654` → yellow `#FFC400`):
- `PipeSupply` @ (40,292,300×18): expr `{SupplyPressure} >= 1`
- `PipeChamber` @ (440,292,380×18): expr `{VP1} OR {ChamberPressure} > 8`
- `PipeDownstream` @ (920,292,220×18): expr `{VP2} AND ({VP1} OR {ChamberPressure} > 8)`

**Valves** (Rectangle, fill red `#E74C3C` → green `#2ECC71`):
- `Valve1` @ (340,271,100×60) expr `{VP1}` · `Valve2` @ (820,271,100×60) expr `{VP2}`

**Switch LEDs** (Ellipse, fill animation):
- `VpsLed` @ (708,231) `{VPS}` off→`#2ECC71` on→`#E74C3C`
- `LgpLed` @ (118,336) `{LGP}` off→`#3B4654` on→`#2ECC71`
- `HgpLed` @ (1018,336) `{HGP}` off→`#3B4654` on→`#E74C3C`
- Tag LEDs for VP1/VP2/VPS along the bottom, same pattern.

**Flames** (Visibility animation): `FlameMain` visible on `{Flame_Main}`;
`FlamePilot` (smaller) visible on `{Flame_Pilot}`.

**Numeric setpoint inputs** (Numeric Input Enable, Min 0 / Max 80 → refuses
letters and out-of-range): `LgpSet`→`{LGPSetpoint}` @ (140,336),
`HgpSet`→`{HGPSetpoint}` @ (966,336), `VpsSet`→`{VPSSetpoint}` @ (700,196).

**Buttons** (Momentary Push Button, Action = Set, momentary — the PLC one-shots each):
| Button | Sets | Pos |
|--------|------|-----|
| START BURNER | `Cmd_Start` | (920,468) |
| STOP / RESET | `Cmd_Stop` | (1075,468) |
| TOGGLE AUTO/MANUAL | `Cmd_ToggleMode` | (920,420) |
| VP1 / VP2 / PILOT | `Cmd_VP1` / `Cmd_VP2` / `Cmd_Pilot` | (920,540)(1075,540)(920,582) |
| SIM V1 LEAK / SIM V2 LEAK | `Cmd_LeakV1` / `Cmd_LeakV2` | (920,646)(1075,646) |
| SIM PILOT FAIL | `Cmd_PilotFail` | (920,690) |
| INLET PRESSURE − / + | `Cmd_InletDown` / `Cmd_InletUp` | (920,730)(1075,730) |

Give MODE/VP1/VP2/PILOT a caption or an indicator tied to `AutoMode` so the
operator sees which are active; disable the manual buttons while `{AutoMode}`.

**Banner** (Multistate Indicator, connection `{State}`), captions + backcolor:
| State | Caption | Back |
|------|---------|------|
| 0 | STANDBY — READY TO START | `#212D3B` |
| 1 | STEP 1: EVACUATE (OPEN V2) | `#1F4E79` |
| 2 | STEP 2: TEST V1 — PRESSURE MUST STAY LOW | `#1F4E79` |
| 3 | STEP 3: FILL (OPEN V1) | `#1F4E79` |
| 4 | STEP 4: TEST V2 — PRESSURE MUST STAY HIGH | `#1F4E79` |
| 5 | PROVEN — PREPURGE | `#1F4E79` |
| 6 | PILOT TRIAL FOR IGNITION | `#1F4E79` |
| 7 | BURNER FIRING — VALVES PROVEN | `#14532D` |
| 8 | SAFETY LOCKOUT — PRESS STOP/RESET | `#7F1D1D` |

A second Multistate Indicator on `{LockReason}` (bottom-left; caption table in
`plc/ValveProving_PLC_tags.md`, tag included in the CSV) spells out *why* it
locked out, and the six-row step checklist is included — each step LED is a
color animation on `{State}` (off / yellow active / green done; expressions are
in the generated XML).

## Sequence behavior
Identical to the Optix gas-pressure variant: Evacuate → Test V1 → Fill → Test V2
→ Purge → Ignition → Run; AUTO drives the valves, MANUAL is an operator drill where
a wrong control or a missed step timer fails the VPS. LGP must be made to start
(and stay made), HGP must not trip with gas past V2, and any fault drives `Lockout`.
See `../optix/valve proving with low and high gas pressure switch/README.md` for the
full narrative — the ST implements the same rules.

## Honest limitations
- The display XML is a **starter**, not a guaranteed drop-in — the ME graphics
  schema is version-specific and was not validated in the tool here. The build spec
  above is the source of truth; the XML gives you every connection expression to
  copy.
- ME cannot run the sequence — the PLC (or a soft controller / Logix Emulate) must
  run `ValveProving.st` for the screen to come alive.
- `LGP`/`HGP` are computed from pressure in the ST for simulation; on a real train,
  delete those two lines and wire the physical switch inputs.
