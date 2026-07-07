# claude_tuning — Model Variable Map

Add these as **child variables of each NetLogic object** in FT Optix Studio
(select the NetLogic in Project view → Variables pane → Add). Names must match
exactly — the C# reads them with `LogicObject.GetVariable("<Name>")`.

## TuningAdvisorLogic variables

### Inputs (bind to UI entry widgets)

| Variable | Data type | Default | Notes |
|---|---|---|---|
| `Kp` | Double | 2.0 | Current proportional gain |
| `Ki` | Double | 0.6 | Current integral gain (parallel form) |
| `Kd` | Double | 0.2 | Current derivative gain (parallel form) |
| `LoopType` | Int32 | 4 | 0=Flow 1=Pressure 2=Level 3=Temperature 4=Fuel 5=CombustionAir 6=FGR |
| `ActuatorSpeed` | Int32 | 1 | 0=Fast(<15s) 1=Medium(15–30s) 2=Slow(30–60s) 3=VerySlow(>60s) |
| `ValveCharacteristic` | Int32 | 1 | 0=Linear 1=EqualPercent 2=QuickOpen 3=Butterfly |
| `Symptom` | Int32 | 0 | 0=Slow 1=Overshoot 2=Oscillate 3=Offset 4=Aggressive 5=SlowOpen 6=SlowClose 7=Noisy 8=Windup 9=Spiky 10=LowFire 11=DistRej |

### Outputs (bind to UI display widgets)

| Variable | Data type | Notes |
|---|---|---|
| `NewKp` | Double | Recommended proportional gain |
| `NewKi` | Double | Recommended integral gain |
| `NewKd` | Double | Recommended derivative gain |
| `Headline` | String | One-line recommendation summary |
| `Reasoning` | String | Explanation paragraph |
| `FieldNotes` | String | Bulleted practical notes (newline-separated) |
| `Warning` | String | Safety/caution banner text; empty when none |
| `TiSeconds` | Double | Standard-form integral time Kp/Ki (0 = no integral) |
| `TdSeconds` | Double | Standard-form derivative time Kd/Kp |
| `HealthSummary` | String | Gain-ratio sanity flags |
| `HasRecommendation` | Boolean | True after Analyze(); gates LoadRecommendation() |

## ValveLoopSimulator variables

### Inputs

| Variable | Data type | Default | Notes |
|---|---|---|---|
| `Kp` | Double | 2.0 | Bind (or NodePointer) to the advisor's `Kp` so both stay in sync |
| `Ki` | Double | 0.6 | Same |
| `Kd` | Double | 0.2 | Same |
| `Setpoint` | Double | 60.0 | Process setpoint (0–100 engineering scale) |
| `ValveCharacteristic` | Int32 | 1 | Bind to advisor's `ValveCharacteristic` |
| `ProcessGain` | Double | 1.0 | FOPDT gain K |
| `TimeConstantS` | Double | 18.0 | FOPDT time constant τ |
| `DeadTimeS` | Double | 2.0 | FOPDT dead time L |
| `NoiseLevel` | Double | 0.3 | Gaussian PV noise strength |
| `OutputClampPct` | Double | 100.0 | Output saturation limit (use <100 to demo windup) |

### Outputs

| Variable | Data type | Notes |
|---|---|---|
| `PV` | Double | Process variable — log with DataLogger for the Trend |
| `OutputPct` | Double | Controller output 0–100% — log for the Trend |
| `ValveAngleDeg` | Double | 0–90°, bind to a rotated image or gauge |
| `ErrorValue` | Double | SP − PV |
| `OvershootPct` | Double | Peak overshoot vs SP |
| `SettleTimeS` | Double | Time to enter and hold 2% band |
| `IAE` | Double | Integral of absolute error — compare tunes numerically |
| `SimStatus` | String | Idle / Running / Paused |

## Sharing gains between the two NetLogics

Simplest: create the gain variables once under `TuningAdvisorLogic` and make the
simulator's `Kp`/`Ki`/`Kd`/`ValveCharacteristic` **dynamic links** to them
(right-click variable → Dynamic link → point at the advisor's variable,
mode Read/Write). Then `LoadRecommendation()` instantly retunes the running sim.
