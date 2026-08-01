# Valve Proving Honeywell — FactoryTalk Optix Project

A standalone, fully independent copy of the *valve proving with low and high gas
pressure switch* project (own project GUID, own node Ids), extended with a
**4-20 mA firing-rate actuator with HIGH FIRE / LOW FIRE switch proving** and a
**Honeywell 7800 SERIES faceplate** with live lights and phase messages. Open
`ValveProvingHoneywell.optix` in FactoryTalk Optix Studio. All pressures are in
**inches of water column (in. H2O)**.

## Firing rate actuator — 4-20 mA with high/low fire switches

The mod motor's position feedback is a 4-20 mA loop on `Model/FiringRateMA`:
**4 mA = LOW FIRE** (`Model/LowFireSwitch` made at/below 4.5 mA) and
**20 mA = HIGH FIRE** (`Model/HighFireSwitch` made at/above 19.5 mA). The
bottom-left panel shows the live mA bar and readout plus the LOW FIRE and HIGH
FIRE switch lights.

The BMS drives the actuator itself in both AUTO and MANUAL (the mod motor is a
relay-module output on a real 7800, not an operator control):

- **Prepurge at high fire** — after the valves prove, the actuator drives to
  20 mA. The purge timer **only runs while the HIGH FIRE switch is proven**; if
  the switch does not prove inside the 15 s prove window → **safety lockout**
  (`HIGH FIRE SWITCH NOT PROVEN`).
- **Low fire start** — after purge the actuator drives back to 4 mA. The pilot
  trial **waits for the LOW FIRE switch**; no prove inside the window →
  **safety lockout** (`LOW FIRE SWITCH NOT PROVEN`).
- **RUN: released to modulation** — the **RATE − / RATE +** buttons (enabled
  only in RUN) move the target in 2 mA steps between low and high fire.
- **SIM ACTUATOR FAULT** freezes the mA feedback where it is (a seized mod
  motor) so you can demonstrate both prove-failure lockouts.

On a real train, wire `FiringRateMA` to the 4-20 mA analog input and the two
switch tags to the physical end switches, and delete `SimulateActuator()` in
the NetLogic.

## Honeywell 7800 SERIES faceplate (bottom middle)

A vector rendition of the relay-module faceplate — red *Honeywell* wordmark,
"7800 SERIES", a two-line green message display, and the five LEDs:

| LED | Lights when |
|-----|-------------|
| POWER | always (module powered) |
| PILOT | pilot valve energized (amber) |
| FLAME | any flame proven — pilot or main |
| MAIN | main valves open, burner firing |
| ALARM | safety lockout (blinks red) |

The message display tracks every phase like the real keyboard display module:
`STANDBY / SYSTEM READY`, `VALVE PROVE / TEST V1 T-08`, `DRIVE HI FIRE / AWAIT
HF SW 12.4 MA`, `PREPURGE HI-FIRE / T-07 20.0 MA`, `DRIVE LO FIRE`, `PILOT IGN
T-04 / FLAME 4.2 VDC`, `RUN / RATE 045% FLAME 4.2V`, and a blinking `LOCKOUT` +
short reason. The flame signal publishes to `Model/FlameSignal` (VDC).

## What was carried over from the gas-pressure project

- **Inlet pressure gauge — gauge only, no switch** — at the very beginning of the
  piping. It is the **only adjustable gauge**: drag its needle or use the
  **INLET PRESSURE + / −** buttons (2 in. steps, 0–80 in. H2O) to set how much gas
  pressure is entering the train; the value publishes to `Model/SupplyPressure`.
- **LGP gauge + switch** after the inlet gauge, before the SKP15 (V1), drawn to
  mimic the VPS gauge and switch. The switch **makes at 4 in. H2O** and its light
  is on (green) **only when made**. If the burner **tries to start before the LGP
  has made, a lockout occurs**; the LGP dropping out during the sequence or run is
  also a lockout.
- **HGP gauge + switch** after the SKP25 (V2), on the downstream pipe. Its gauge
  reads `Model/DownstreamPressure` — it sees gas only when V2 is passing it. The
  switch **must not break**: its light stays off unless downstream pressure exceeds
  the **70 in. H2O** setting, and **any trip is an immediate lockout**
  (`Model/Lockout` goes high, STOP/RESET clears).
- **Numeric spin-box inputs** next to the LGP, HGP, and VPS switches. They accept
  **numbers only** (numeric keypad — letters are impossible to enter) and enforce
  **0–80 in. H2O** at the widget, with the logic clamping again as a backstop:
  - **LGP** — trip point on inlet pressure: below it the switch drops out (light
    off) and a lockout occurs (or on a start attempt before it makes).
  - **HGP** — trip point on downstream pressure: exceeded with the valves open
    (gas passing V2) = lockout.
  - **VPS** — the **allowed differential** during each hold test: the V1 test
    fails if the evacuated volume gains more than this, the V2 test fails if the
    charged volume decays more than this below supply. Default 14 matches the old
    fixed mid-point behavior.
  Settings publish to `Model/LGPSetpoint`, `Model/HGPSetpoint`, `Model/VPSSetpoint`.
- **Gauges read live pressures, gated by the valves before them**: the LGP gauge
  always sees the incoming pressure; the test-volume gauge sees gas only via V1;
  the HGP gauge sees gas only when V2 passes it. These three are **read-only
  displays** (not draggable) driven by the logic at runtime — press Play/emulator
  in Studio; values do not change in the designer.

## Tags for I/O (Model folder)

| Tag | Type | Meaning |
|-----|------|---------|
| `VP1` / `VP2` | Boolean | Safety shutoff valves open (as in the base project) |
| `VPS` | Boolean | Valve proving switch input — TRUE = test failed |
| `Pilot` | Boolean | Pilot valve/flame (legal only during the ignition trial) |
| `Lockout` | Boolean | Output — high on any safety lockout |
| `LGP` | Boolean | Low gas pressure switch (inlet line, before V1) — TRUE = made (at/above its setpoint, default 4 in. H2O) |
| `HGP` | Boolean | High gas pressure switch (downstream of V2) — TRUE = tripped (above its setpoint, default 70 in. H2O); must not break |
| `SupplyPressure` | Float | Inlet gas pressure, in. H2O (drives the inlet and LGP gauges) |
| `DownstreamPressure` | Float | Pressure after the SKP25/V2, in. H2O (drives the HGP gauge) |
| `LGPSetpoint` | Float | LGP trip point, set with its numeric input (0–80 in. H2O, default 4) |
| `HGPSetpoint` | Float | HGP trip point, set with its numeric input (0–80 in. H2O, default 70) |
| `VPSSetpoint` | Float | VPS allowed differential per hold test, set with its numeric input (0–80 in. H2O, default 14) |
| `FiringRateMA` | Float | Firing-rate actuator position feedback, 4–20 mA (wire to the analog input on a real train) |
| `LowFireSwitch` | Boolean | LOW FIRE end switch — made at/below 4.5 mA; ignition waits on it |
| `HighFireSwitch` | Boolean | HIGH FIRE end switch — made at/above 19.5 mA; purge timer waits on it |
| `ActuatorFault` | Boolean | Simulation: freeze the mA feedback (seized mod motor) |
| `FlameSignal` | Float | Flame amplifier signal, VDC, shown on the faceplate display |

In simulation the logic computes `LGP` from `SupplyPressure` and `HGP` from
`DownstreamPressure` against the spin-box settings; for a real train, bind the two
switch tags (and `SupplyPressure` from a transmitter) to your controller and the
screen behaves the same.

The old fixed C# setpoint constants are gone — the trip settings are entered in
the numeric spin boxes on the screen (numbers only, 0–80 in. H2O enforced by the
widget and re-clamped by the logic) and are published to the three `*Setpoint`
Model tags every scan.

Everything else — the Honeywell 7800 SERIES proving sequence, AUTO and MANUAL drill
modes, pilot handling, leak simulation — is identical to the base project; see
`../README.md`.
