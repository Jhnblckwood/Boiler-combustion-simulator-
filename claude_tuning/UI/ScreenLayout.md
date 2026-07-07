# claude_tuning — UI Build Guide (FT Optix Studio)

Recreates the pid-tuning-trainer layout as an Optix screen. All bindings refer
to the NetLogic variables in `Model/ModelVariables.md`.

## MainWindow layout (two-column RowLayout inside a ScrollView)

```
MainWindow
├─ Header (Rectangle, dark) — Label "PID TUNER // 90° ACTUATED VALVE"
└─ ColumnLayout
   ├─ LeftPanel (Panel, width ~440)
   │  ├─ Label "CURRENT GAINS"
   │  ├─ RowLayout: SpinBox Kp | SpinBox Ki | SpinBox Kd
   │  │    (bind Value ↔ Advisor Kp/Ki/Kd, Read/Write)
   │  ├─ RowLayout: Label ← TiSeconds ("Ti = {0} s") | Label ← TdSeconds
   │  ├─ Label ← HealthSummary   (TextColor amber; the gain sanity strip)
   │  ├─ Label "VALVE / LOOP CONTEXT"
   │  ├─ ComboBox LoopType      (7 entries, SelectedIndex ↔ LoopType)
   │  ├─ ComboBox ActuatorSpeed (4 entries, SelectedIndex ↔ ActuatorSpeed)
   │  ├─ ComboBox ValveChar     (4 entries, SelectedIndex ↔ ValveCharacteristic)
   │  ├─ SpinBox Setpoint       (↔ Simulator Setpoint)
   │  ├─ Label "SELECT SYMPTOM"
   │  ├─ ComboBox or 12 RadioButtons ↔ Symptom (see enum order in Model doc)
   │  ├─ Button "ANALYZE & RECOMMEND"
   │  │    → MouseClick event: Method invocation → TuningAdvisorLogic.Analyze
   │  ├─ Button "LOAD → SIM"
   │  │    → TuningAdvisorLogic.LoadRecommendation
   │  └─ Expander "ADVANCED // PROCESS MODEL"
   │       Sliders: ProcessGain (0.3–3), TimeConstantS (4–80),
   │                DeadTimeS (0–25), NoiseLevel (0–2), OutputClampPct (20–100)
   │
   └─ RightColumn (ColumnLayout)
      ├─ RecommendationPanel (Panel)
      │  ├─ Label ← Headline        (large, Oswald-style font)
      │  ├─ Label ← Warning         (red background; Visible ← Warning != "")
      │  ├─ RowLayout: three value cards
      │  │    "Kp → {NewKp}"  "Ki → {NewKi} (Ti)"  "Kd → {NewKd} (Td)"
      │  ├─ Label ← Reasoning       (WordWrap on)
      │  └─ Label ← FieldNotes      (WordWrap on, multiline)
      │
      └─ SimulatorPanel (Panel)
         ├─ Trend widget
         │    Pens: PV (green), Setpoint (cyan), OutputPct (amber)
         │    Backed by a DataLogger sampling those three variables @250 ms
         ├─ ValveWidget: Image of a butterfly disc with Rotation ← ValveAngleDeg
         │    (or a circular Gauge 0–90°, whichever reads better)
         ├─ RowLayout buttons:
         │    "START" → ValveLoopSimulator.StartSim
         │    "PAUSE" → ValveLoopSimulator.PauseSim
         │    "RESET" → ValveLoopSimulator.ResetSim
         │    "DISTURB" → ValveLoopSimulator.Disturb   (styled red)
         └─ RowLayout metric cards (Labels bound to):
              ErrorValue | OvershootPct | SettleTimeS | IAE | ValveAngleDeg
```

## DataLogger setup (for the Trend)

1. Project view → Loggers → Add DataLogger (`SimLogger`), sampling 250 ms,
   store to the embedded database.
2. Add logged variables: `ValveLoopSimulator/PV`, `ValveLoopSimulator/Setpoint`
   (log the SpinBox-bound variable), `ValveLoopSimulator/OutputPct`.
3. On the Trend widget, add three pens pointing at the logged variables and set
   a 2–5 minute rolling time window. Y-axis fixed 0–100.

## Before-vs-after comparison

The web trainer's "Save Baseline / Compare" overlay maps naturally onto the
Trend's history: run with the original gains, click LOAD → SIM, and the pen
history keeps the old response on screen while the new gains draw ahead of it.
For a hard A/B, add a second DataLogger and a Trend pen pair, or just pause and
screenshot between runs. The `IAE` metric gives the numeric comparison either
way — lower is better for the same test sequence.

## Color specification (assigned)

Dark industrial palette carried over from the web trainer — high contrast for
control-room viewing, amber accent that doesn't clash with alarm-red. Apply
these to the widget properties named below; anything not listed inherits from
its parent panel.

### Base surfaces

| Element | Property | Color |
|---|---|---|
| MainWindow | FillColor | `#0A0D12` |
| Header rectangle | FillColor | `#141B24` |
| All Panels (left, recommendation, simulator) | FillColor | `#141B24` |
| Panel BorderColor (all) | BorderColor, thickness 1 | `#26303D` |
| Metric cards / gain value cards | FillColor | `#0A0D12` |
| Trend widget background | FillColor | `#050709` |
| Expander header | FillColor | `#0A0D12` |

### Text

| Element | Property | Color |
|---|---|---|
| Primary text (values, headline, metric numbers) | TextColor | `#E8EEF5` |
| Secondary text (reasoning, field notes) | TextColor | `#8B95A3` |
| Field labels / captions ("KP", "LOOP TYPE") | TextColor | `#7A8594` |
| Section titles ("CURRENT GAINS", "SYMPTOM") | TextColor | `#F7A531` |
| Ti/Td sub-labels, HealthSummary when OK | TextColor | `#3DD68C` |
| HealthSummary when flagging a risk | TextColor | `#F7A531` |
| Warning banner text | TextColor | `#FFB4B4` on FillColor `#2A1215`, BorderColor `#FF5A5A` |

### Buttons

| Button | FillColor | TextColor | BorderColor |
|---|---|---|---|
| ANALYZE & RECOMMEND (primary) | `#F7A531` | `#1A1206` | `#F7A531` |
| LOAD → SIM, START, PAUSE, RESET | `#1A222D` | `#E8EEF5` | `#3A4655` |
| DISTURB | `#1A222D` | `#FF5A5A` | `#FF5A5A` |

### Trend pens & valve widget

| Item | Color | Width |
|---|---|---|
| PV pen | `#3DD68C` | 2 px |
| Setpoint pen | `#00D4E0` | 1 px |
| Output pen | `#F7A531` | 1 px |
| Trend grid lines | `#1A222D` | 1 px |
| Valve gauge arc / disc image tint | `#F7A531` on `#0D1520` face | — |

### Fonts

Oswald for headers/section titles, JetBrains Mono for numeric values (embed
via Project → Fonts). If embedding is more trouble than it's worth, the
built-in fallbacks that keep the look are **Segoe UI Semibold** for headers
and **Consolas** for values — both ship on Windows targets.
