# Valve Proving with Low and High Gas Pressure Switch — FactoryTalk Optix Project

A standalone copy of the base valve proving project (`optix/`) extended with gas
supply pressure supervision. Open `ValveProvingGasPressure.optix` in FactoryTalk
Optix Studio. All pressures are in **inches of water column (in. H2O)**.

## What was added

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
