# Valve Proving with Low and High Gas Pressure Switch — FactoryTalk Optix Project

A standalone copy of the base valve proving project (`optix/`) extended with gas
supply pressure supervision. Open `ValveProvingGasPressure.optix` in FactoryTalk
Optix Studio. All pressures are in **inches of water column (in. H2O)**.

## What was added

- **Inlet pressure gauge (no switch)** at the very beginning of the piping. It reads
  `Model/SupplyPressure` — how much gas pressure is entering the train. Adjust it
  with the **INLET PRESSURE + / −** buttons (2 in. steps, 0–80 in. H2O), or drive
  the tag from real I/O.
- **LGP** — low gas pressure switch before the first valve. It **makes at 4 in.
  H2O**; its light turns green only once made.
- **HGP** — high gas pressure gauge + switch pair, drawn to mimic the VPS gauge and
  switch. Its light stays **off unless the pressure exceeds the 70 in. H2O
  setting**, then goes red.
- **Operating window ("spread"): 4 to 70 in. H2O.** START is blocked outside the
  window, and if the pressure falls below the LGP setpoint or rises above the HGP
  setpoint while the sequence or burner is running, a **safety lockout** occurs
  (`Model/Lockout` goes high, STOP/RESET clears).

## Tags for I/O (Model folder)

| Tag | Type | Meaning |
|-----|------|---------|
| `VP1` / `VP2` | Boolean | Safety shutoff valves open (as in the base project) |
| `VPS` | Boolean | Valve proving switch input — TRUE = test failed |
| `Pilot` | Boolean | Pilot valve/flame (legal only during the ignition trial) |
| `Lockout` | Boolean | Output — high on any safety lockout |
| `LGP` | Boolean | Low gas pressure switch — TRUE = made (≥ 4 in. H2O) |
| `HGP` | Boolean | High gas pressure switch — TRUE = tripped (> 70 in. H2O) |
| `SupplyPressure` | Float | Inlet gas pressure, in. H2O (drives the inlet and HGP gauges) |

In simulation the logic computes `LGP`/`HGP` from `SupplyPressure`; for a real
train, bind the two switch tags (and `SupplyPressure` from a transmitter) to your
controller and the screen behaves the same.

Setpoints are constants at the top of `ProjectFiles/NetSolution/ValveProvingLogic.cs`
(`LgpSetpoint = 4`, `HgpSetpoint = 70`).

Everything else — the Honeywell 7800 SERIES proving sequence, AUTO and MANUAL drill
modes, pilot handling, leak simulation — is identical to the base project; see
`../README.md`.
