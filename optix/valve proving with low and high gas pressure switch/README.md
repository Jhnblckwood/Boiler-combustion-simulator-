# Valve Proving with Low and High Gas Pressure Switch — FactoryTalk Optix Project

A standalone copy of the base valve proving project (`optix/`) extended with gas
supply pressure supervision. Open `ValveProvingGasPressure.optix` in FactoryTalk
Optix Studio. All pressures are in **inches of water column (in. H2O)**.

## What was added

- **Inlet pressure gauge — gauge only, no switch** — at the very beginning of the
  piping. It reads `Model/SupplyPressure` — how much gas pressure is entering the
  train. Adjust it with the **INLET PRESSURE + / −** buttons (2 in. steps,
  0–80 in. H2O), or drive the tag from real I/O.
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
- **Operating spread: 4 to 70 in. H2O.**

## Tags for I/O (Model folder)

| Tag | Type | Meaning |
|-----|------|---------|
| `VP1` / `VP2` | Boolean | Safety shutoff valves open (as in the base project) |
| `VPS` | Boolean | Valve proving switch input — TRUE = test failed |
| `Pilot` | Boolean | Pilot valve/flame (legal only during the ignition trial) |
| `Lockout` | Boolean | Output — high on any safety lockout |
| `LGP` | Boolean | Low gas pressure switch (inlet line, before V1) — TRUE = made (≥ 4 in. H2O) |
| `HGP` | Boolean | High gas pressure switch (downstream of V2) — TRUE = tripped (> 70 in. H2O); must not break |
| `SupplyPressure` | Float | Inlet gas pressure, in. H2O (drives the inlet and LGP gauges) |
| `DownstreamPressure` | Float | Pressure after the SKP25/V2, in. H2O (drives the HGP gauge) |

In simulation the logic computes `LGP`/`HGP` from `SupplyPressure`; for a real
train, bind the two switch tags (and `SupplyPressure` from a transmitter) to your
controller and the screen behaves the same.

Setpoints are constants at the top of `ProjectFiles/NetSolution/ValveProvingLogic.cs`
(`LgpSetpoint = 4`, `HgpSetpoint = 70`).

Everything else — the Honeywell 7800 SERIES proving sequence, AUTO and MANUAL drill
modes, pilot handling, leak simulation — is identical to the base project; see
`../README.md`.
