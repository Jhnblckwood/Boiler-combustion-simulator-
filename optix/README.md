# Valve Proving System — FactoryTalk Optix Project

A FactoryTalk Optix Studio project that animates a **valve proving test (VPS)** on a
double-block gas train, following the startup proving sequence used by a
**Honeywell 7800 SERIES** burner management system (the request said "7600" — the
Honeywell BMS family is the 7800 SERIES, e.g. RM7800/RM7840, which is what
Cleaver-Brooks uses, so that sequence is modeled here).

The gas train is drawn Siemens-style: **SKP15** actuator on SSOV V1 and an
**SKP25 regulator/actuator** (with spring dome and downstream impulse line) on SSOV V2.

## The three tags (Model folder)

| Tag | Type | Meaning |
|-----|------|---------|
| `VP1` | Boolean | Safety shutoff valve **V1** (upstream) open |
| `VP2` | Boolean | Safety shutoff valve **V2** (downstream) open |
| `VPS` | Boolean | Valve proving switch input — **TRUE = test failed** |
| `Pilot` | Boolean | Pilot valve/flame. Only legal during the ignition trial — TRUE at **any other time** (auto or manual, including from real I/O) trips a safety lockout |
| `Lockout` | Boolean | Output — goes **high on any safety lockout** and clears on STOP / RESET. Attach to I/O (alarm horn, stack light, BMS alarm input) |

Supporting simulation variables also live in `Model`: `AutoMode`, `LeakV1`, `LeakV2`,
`PilotFail`, `ChamberPressure`, `SupplyPressure`, `State`, `StateText`.

## Opening the project

1. Install **FactoryTalk Optix Studio** (built against the 1.4 module set; 1.3+ should
   migrate it on open).
2. Open `ValveProving.optix` from this folder.
3. Studio regenerates `ProjectFiles/NetSolution/ValveProving.references` (it points at
   your local FTOptix install and is intentionally not committed) and compiles
   `ValveProvingLogic.cs`.
4. Run with the play/emulator button. The screen is also served by the
   WebPresentationEngine on port **8666**.

If any node fails to load on your Studio version, the screen can be rebuilt quickly:
everything dynamic is driven by `ValveProvingLogic.cs` through widget **names** —
keep the names (`PipeSupply`, `ValveBody1`, `StepLed1`…) and the logic reconnects.

## Proving sequence (startup, before light-off)

```
START BURNER
  1. EVACUATE  (5 s)  V2 opens; test volume between V1 and V2 vents to the burner
  2. TEST V1   (10 s) both valves closed; pressure must STAY LOW
                      -> a rise means V1 leaks  -> VPS = TRUE -> LOCKOUT
  3. FILL      (5 s)  V1 opens; test volume charges to supply pressure
  4. TEST V2   (10 s) both valves closed; pressure must STAY HIGH
                      -> a decay means V2 leaks -> VPS = TRUE -> LOCKOUT
  5. PROVEN            prepurge (10 s), then pilot trial for ignition (4 s);
                       a small pilot flame burns (fed from a pilot line not
                       shown) while VP1 + VP2 stay closed for the whole trial.
                       No pilot by the end of the countdown -> LOCKOUT
  6. RUN               VP1 + VP2 open, main flame on
```

The VPS pressure switch is evaluated at **50 % of supply pressure** (27.7 in. w.c.
default), the classic mid-point setpoint for a pressure-proving VPS. A `VPS = TRUE`
during either test drives a **safety lockout** that only **STOP / RESET** clears —
mirroring 7800 SERIES lockout behavior. Timings are constants at the top of
`ValveProvingLogic.cs` if you want field-realistic (longer) periods.

## Animation

- **Piping turns yellow wherever gas is present**: the supply header is always live;
  the test volume goes yellow as V1 admits gas; downstream of V2 goes yellow only
  when gas passes to the burner.
- Valve wedges: **green = open, red = closed**; the circular gauge shows test-volume
  pressure from the tap between the valves; the VPS switch LED goes red on a trip.
- During the **ignition trial** a smaller flickering **pilot flame** shows at the
  burner (pilot gas comes from a line not drawn on the train); the main flame only
  appears at RUN, after the trial countdown ends and VP1/VP2 open.
- A six-step checklist mirrors the sequence (yellow = active, green = passed,
  red = failed step), with a banner and countdown timer on top.

## Modes (both included)

Both modes run the **same sequence, steps, lights and timers** — the difference is
who operates the valves.

- **AUTO (BMS SEQUENCE)** — `START BURNER` runs the full proving sequence; the logic
  drives VP1/VP2/Pilot and evaluates VPS. Under **SIMULATION**, use
  **SIM V1 LEAK / SIM V2 LEAK** to make the corresponding test fail naturally (watch
  the gauge drift), or **SIM PILOT FAIL** to keep the pilot from lighting so the
  ignition trial ends in a lockout instead of RUN.
- **MANUAL (OPERATOR DRILL)** — press `START BURNER` and *you* are the BMS: perform
  each step with the VP1 / VP2 / PILOT buttons before its countdown runs out.
  - Step 1: open VP2 to evacuate - Step 2: close VP2 for the V1 hold - Step 3: open
    VP1 to fill - Step 4: close VP1 for the V2 hold - Step 5: light the PILOT during
    the ignition trial - Step 6: open VP1 + VP2, then PILOT off (light-off window).
  - Pressing a control the current step does not call for **fails the VPS
    immediately**; a countdown elapsing without the required action **also fails the
    VPS**. Either way: safety lockout, cleared by STOP / RESET.
  - In standby (before START) VP1/VP2 are free play so you can just watch the gas
    move; the pilot is always supervised and locks out outside the ignition trial.

The dedicated VPS FAIL button was removed — in manual mode the VPS fails from real
causes (wrong control, missed countdown, leaks), and in auto the leak/pilot
simulations exercise it.

## Binding to a real PLC

Delete the leak-simulation pieces if unwanted, add your driver under
**CommDrivers** (e.g. RA EtherNet/IP), then point `Model/VP1`, `Model/VP2` and
`Model/VPS` at the controller tags (drag the PLC tags onto the Model variables or
add DynamicLinks). The screen and sequence display run entirely off those three tags.
