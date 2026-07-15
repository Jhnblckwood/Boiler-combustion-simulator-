# Controller tags for ValveProving.st

Create these in the Logix controller (Studio 5000). Names match the HMI tag CSV
1:1, so the PanelView Plus 7 can reference them through a device shortcut.

## Booleans (BOOL)
| Tag | Dir | Meaning |
|-----|-----|---------|
| `VP1`, `VP2` | out | Safety shutoff valves open (V1 = SKP15, V2 = SKP25) |
| `VPS` | out | Valve proving switch, TRUE = test failed |
| `Pilot` | out | Pilot valve/flame (legal only during ignition trial) |
| `Lockout` | out | Safety lockout active (wire to horn / BMS alarm input) |
| `LGP` | out* | Low gas pressure switch, TRUE = made (>= setpoint) |
| `HGP` | out* | High gas pressure switch, TRUE = tripped (> setpoint) |
| `AutoMode` | i/o | AUTO (1) / MANUAL (0) |
| `LeakV1`, `LeakV2`, `PilotFail` | in | Simulation toggles (remove for a real train) |
| `RunEstablished`, `Flame_Main`, `Flame_Pilot` | internal | Sequence/animation helpers |
| `Cmd_Start`, `Cmd_Stop`, `Cmd_ToggleMode`, `Cmd_VP1`, `Cmd_VP2`, `Cmd_Pilot`, `Cmd_LeakV1`, `Cmd_LeakV2`, `Cmd_PilotFail`, `Cmd_InletUp`, `Cmd_InletDown` | in | Momentary HMI button bits |
| `Cmd_*_Last` (one per Cmd_ above) | internal | Rising-edge memory for the one-shots |

\* On a real train `LGP`/`HGP` are physical switch inputs — delete the two lines
in the ST that compute them from pressure and wire the field switches instead.

## Reals (REAL)
| Tag | Meaning |
|-----|---------|
| `ChamberPressure` | Test-volume pressure, in. H2O (from transmitter on a real train) |
| `SupplyPressure` | Inlet gas pressure, in. H2O |
| `DownstreamPressure` | Pressure after V2, in. H2O |
| `LGPSetpoint`, `HGPSetpoint`, `VPSSetpoint` | Trip settings, in. H2O (0–80) |
| `StepElapsed` | Internal step timer accumulator |

## DINT
| Tag | Meaning |
|-----|---------|
| `State` | 0 Standby · 1 Evacuate · 2 TestV1 · 3 Fill · 4 TestV2 · 5 Purge · 6 Ignition · 7 Run · 8 Lockout |
| `LockReason` | 0 none · 2 LGP dropout · 3 HGP trip · 4 LGP-not-made-at-start · 5 pilot-outside-trial · 6 invalid manual control · 7 control-changed-in-run · 8 required action not done in time · 9 valves not closed for hold · 10 V1 leak · 11 V2 leak · 12 pilot failed to light · 13 light-off timeout |

## Constants (declare as REAL, set once)
`EvacuateTime 5, TestV1Time 10, FillTime 5, TestV2Time 10, PurgeTime 10,`
`IgnitionTime 4, LightOffTime 10, TickSeconds 0.1, LeakRate 2.2, InletStep 2,`
`InletMax 80, SetpointMax 80`. Put `ValveProving.st` in a **periodic task at
100 ms** so `TickSeconds` (0.1) matches the scan period.
