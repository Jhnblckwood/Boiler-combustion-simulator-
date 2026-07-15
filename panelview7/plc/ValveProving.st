(*
    ValveProving.st  -  Valve Proving System sequence for a Logix PLC
    ============================================================================
    PanelView Plus 7 (FactoryTalk View ME) has no embedded scripting, so the
    proving sequence runs here in the controller and the HMI only displays tags
    and sends button presses. This routine is the exact port of the FactoryTalk
    Optix NetLogic (gas-pressure variant) - same steps, timers, lights, LGP/HGP/
    VPS supervision, pilot handling, auto/manual drill, and leak simulation.

    Target: Studio 5000 Logix Designer, Structured Text.
    Run this routine in a PERIODIC task at 100 ms (TickSeconds below must match
    the task period). All tags are controller-scoped unless noted; see
    tags/ValveProving_PLC_tags.md for the full tag list and data types.

    Honeywell 7800 SERIES proving sequence, Siemens SKP15 (V1) / SKP25 (V2):
      1 EVACUATE  V2 open, test volume vents to burner
      2 TEST V1   both closed, pressure must stay LOW  (rise => V1 leak)
      3 FILL      V1 open, volume charges to supply
      4 TEST V2   both closed, pressure must stay HIGH (decay => V2 leak)
      5 PROVEN    prepurge, then pilot trial for ignition
      6 RUN       both valves open, main flame
    Any fault (VPS trip, LGP dropout, HGP trip, wrong manual control, missed
    step timer, pilot fail) drives Lockout and holds until Cmd_Stop.
============================================================================ *)

(* ---- constants (seconds) ---- *)
EvacuateTime   := 5.0;
TestV1Time     := 10.0;
FillTime       := 5.0;
TestV2Time     := 10.0;
PurgeTime      := 10.0;
IgnitionTime   := 4.0;
LightOffTime   := 10.0;   (* manual RUN establishment window *)
TickSeconds    := 0.1;    (* MUST equal the periodic task period *)
LeakRate       := 2.2;    (* simulated seat leak, in. H2O per second *)
InletStep      := 2.0;
InletMax       := 80.0;
SetpointMax    := 80.0;

(* State values: 0 Standby 1 Evac 2 TestV1 3 Fill 4 TestV2 5 Purge
   6 Ignition 7 Run 8 Lockout *)

(* ==== rising-edge detection for the HMI momentary buttons ==== *)
Start_OS       := Cmd_Start       AND NOT Cmd_Start_Last;
Stop_OS        := Cmd_Stop        AND NOT Cmd_Stop_Last;
Mode_OS        := Cmd_ToggleMode  AND NOT Cmd_ToggleMode_Last;
VP1_OS         := Cmd_VP1         AND NOT Cmd_VP1_Last;
VP2_OS         := Cmd_VP2         AND NOT Cmd_VP2_Last;
Pilot_OS       := Cmd_Pilot       AND NOT Cmd_Pilot_Last;
LeakV1_OS      := Cmd_LeakV1      AND NOT Cmd_LeakV1_Last;
LeakV2_OS      := Cmd_LeakV2      AND NOT Cmd_LeakV2_Last;
PilotFail_OS   := Cmd_PilotFail   AND NOT Cmd_PilotFail_Last;
InletUp_OS     := Cmd_InletUp     AND NOT Cmd_InletUp_Last;
InletDown_OS   := Cmd_InletDown   AND NOT Cmd_InletDown_Last;

Cmd_Start_Last      := Cmd_Start;
Cmd_Stop_Last       := Cmd_Stop;
Cmd_ToggleMode_Last := Cmd_ToggleMode;
Cmd_VP1_Last        := Cmd_VP1;
Cmd_VP2_Last        := Cmd_VP2;
Cmd_Pilot_Last      := Cmd_Pilot;
Cmd_LeakV1_Last     := Cmd_LeakV1;
Cmd_LeakV2_Last     := Cmd_LeakV2;
Cmd_PilotFail_Last  := Cmd_PilotFail;
Cmd_InletUp_Last    := Cmd_InletUp;
Cmd_InletDown_Last  := Cmd_InletDown;

(* ==== setpoint entry clamp (HGP/LGP/VPS numeric inputs, 0..80) ==== *)
IF LGPSetpoint < 0.0 THEN LGPSetpoint := 0.0; END_IF;
IF LGPSetpoint > SetpointMax THEN LGPSetpoint := SetpointMax; END_IF;
IF HGPSetpoint < 0.0 THEN HGPSetpoint := 0.0; END_IF;
IF HGPSetpoint > SetpointMax THEN HGPSetpoint := SetpointMax; END_IF;
IF VPSSetpoint < 0.0 THEN VPSSetpoint := 0.0; END_IF;
IF VPSSetpoint > SetpointMax THEN VPSSetpoint := SetpointMax; END_IF;

(* supply floor/ceiling *)
IF SupplyPressure < 0.0 THEN SupplyPressure := 0.0; END_IF;
IF SupplyPressure > InletMax THEN SupplyPressure := InletMax; END_IF;

(* ==== operator commands ==== *)

(* mode toggle - always drops to a safe state *)
IF Mode_OS THEN
    AutoMode := NOT AutoMode;
    VP1 := 0; VP2 := 0; Pilot := 0; VPS := 0; Lockout := 0;
    State := 0; StepElapsed := 0.0; RunEstablished := 0; LockReason := 0;
END_IF;

(* stop / reset *)
IF Stop_OS THEN
    VP1 := 0; VP2 := 0; Pilot := 0; VPS := 0; Lockout := 0;
    State := 0; StepElapsed := 0.0; RunEstablished := 0; LockReason := 0;
END_IF;

(* start - both modes; blocked unless closed train and pressure window OK *)
IF Start_OS AND State = 0 THEN
    IF (NOT VP1) AND (NOT VP2) AND (NOT Pilot) THEN
        IF LGP THEN
            VPS := 0; Lockout := 0; LockReason := 0;
            RunEstablished := 0;
            State := 1; StepElapsed := 0.0;   (* -> Evacuate *)
        ELSE
            (* start attempted before LGP made = lockout *)
            LockReason := 4;  (* LGP-not-made *)
            VP1 := 0; VP2 := 0; Pilot := 0; Lockout := 1; State := 8;
        END_IF;
    END_IF;
END_IF;

(* inlet pressure +/- (the only adjustable gauge) *)
IF InletUp_OS THEN
    SupplyPressure := SupplyPressure + InletStep;
    IF SupplyPressure > InletMax THEN SupplyPressure := InletMax; END_IF;
END_IF;
IF InletDown_OS THEN
    SupplyPressure := SupplyPressure - InletStep;
    IF SupplyPressure < 0.0 THEN SupplyPressure := 0.0; END_IF;
END_IF;

(* leak / pilot-fail simulation toggles *)
IF LeakV1_OS THEN LeakV1 := NOT LeakV1; END_IF;
IF LeakV2_OS THEN LeakV2 := NOT LeakV2; END_IF;
IF PilotFail_OS THEN PilotFail := NOT PilotFail; END_IF;

(* manual valve / pilot commands (ignored in AUTO or during lockout) *)
IF (NOT AutoMode) AND State <> 8 THEN
    (* VP1 *)
    IF VP1_OS THEN
        IF State = 7 AND RunEstablished THEN
            LockReason := 7; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
        ELSIF (NOT VP1) AND (State >= 1) AND NOT (State = 3 OR State = 7) THEN
            (* opening V1 when the step does not call for it = VPS fail *)
            LockReason := 6; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
        ELSE
            VP1 := NOT VP1;
        END_IF;
    END_IF;
    (* VP2 *)
    IF VP2_OS AND State <> 8 THEN
        IF State = 7 AND RunEstablished THEN
            LockReason := 7; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
        ELSIF (NOT VP2) AND (State >= 1) AND NOT (State = 1 OR State = 7) THEN
            LockReason := 6; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
        ELSE
            VP2 := NOT VP2;
        END_IF;
    END_IF;
    (* Pilot *)
    IF Pilot_OS AND State <> 8 THEN
        IF Pilot THEN
            Pilot := 0;                 (* shutting the pilot is always safe *)
        ELSIF State = 6 THEN
            Pilot := NOT PilotFail;     (* lights unless failed *)
        ELSIF State = 0 THEN
            LockReason := 5; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSE
            LockReason := 6; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
        END_IF;
    END_IF;
END_IF;

(* ==== pressure simulation (replace with real transmitter when on a train) ==== *)
IF VP1 THEN
    ChamberPressure := ChamberPressure + (SupplyPressure - ChamberPressure) * 0.5;
ELSIF VP2 THEN
    ChamberPressure := ChamberPressure + (0.0 - ChamberPressure) * 0.35;
ELSE
    IF LeakV1 THEN ChamberPressure := ChamberPressure + LeakRate * TickSeconds; END_IF;
    IF LeakV2 THEN ChamberPressure := ChamberPressure - LeakRate * TickSeconds; END_IF;
END_IF;
IF ChamberPressure < 0.0 THEN ChamberPressure := 0.0; END_IF;
IF ChamberPressure > SupplyPressure THEN ChamberPressure := SupplyPressure; END_IF;

(* downstream of V2 sees gas only when V2 passes it *)
IF VP2 THEN DownstreamPressure := ChamberPressure; ELSE DownstreamPressure := 0.0; END_IF;

(* ==== gas pressure switches ==== *)
LGP := (SupplyPressure >= LGPSetpoint);    (* makes at/above setpoint *)
HGP := (DownstreamPressure > HGPSetpoint);  (* must not break *)

(* ==== global supervision (any running state) ==== *)
(* HGP trip - any time it is above setpoint with gas past V2 *)
IF HGP AND State <> 8 THEN
    LockReason := 3; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
END_IF;

(* pilot present outside the trial = lockout (covers real I/O forcing it) *)
IF Pilot AND (State <> 6) AND (State <> 8) AND NOT (State = 7 AND NOT RunEstablished) THEN
    LockReason := 5;
    IF NOT AutoMode THEN VPS := 1; END_IF;
    VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
END_IF;

(* ==== sequence state machine ==== *)
CASE State OF

    0: (* Standby *)
        IF AutoMode THEN VP1 := 0; VP2 := 0; END_IF;

    8: (* Lockout - hold until Stop/Reset *)
        VP1 := 0; VP2 := 0; Pilot := 0; Lockout := 1;

    1: (* EVACUATE *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN VP1 := 0; VP2 := 1; END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSIF AutoMode THEN
            IF StepElapsed >= EvacuateTime THEN State := 2; StepElapsed := 0.0; END_IF;
        ELSE
            IF StepElapsed >= EvacuateTime THEN
                IF VP2 AND (NOT VP1) THEN State := 2; StepElapsed := 0.0;
                ELSE LockReason := 8; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8; END_IF;
            END_IF;
        END_IF;

    2: (* TEST V1 - pressure must stay low *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN VP1 := 0; VP2 := 0; END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSE
            VPS := (ChamberPressure > VPSSetpoint);
            IF VPS THEN
                LockReason := 10; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;  (* V1 leak *)
            ELSIF StepElapsed >= TestV1Time THEN
                IF AutoMode OR ((NOT VP1) AND (NOT VP2)) THEN State := 3; StepElapsed := 0.0;
                ELSE LockReason := 9; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8; END_IF;
            END_IF;
        END_IF;

    3: (* FILL *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN VP1 := 1; VP2 := 0; END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSIF AutoMode THEN
            IF StepElapsed >= FillTime THEN State := 4; StepElapsed := 0.0; END_IF;
        ELSE
            IF StepElapsed >= FillTime THEN
                IF VP1 AND (NOT VP2) THEN State := 4; StepElapsed := 0.0;
                ELSE LockReason := 8; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8; END_IF;
            END_IF;
        END_IF;

    4: (* TEST V2 - pressure must stay high *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN VP1 := 0; VP2 := 0; END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSE
            VPS := (ChamberPressure < (SupplyPressure - VPSSetpoint));
            IF VPS THEN
                LockReason := 11; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;  (* V2 leak *)
            ELSIF StepElapsed >= TestV2Time THEN
                IF AutoMode OR ((NOT VP1) AND (NOT VP2)) THEN
                    VPS := 0; State := 5; StepElapsed := 0.0;
                ELSE LockReason := 9; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8; END_IF;
            END_IF;
        END_IF;

    5: (* PURGE *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN VP1 := 0; VP2 := 0; END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSIF StepElapsed >= PurgeTime THEN
            State := 6; StepElapsed := 0.0;
        END_IF;

    6: (* IGNITION trial - pilot only, main valves closed *)
        StepElapsed := StepElapsed + TickSeconds;
        IF AutoMode THEN
            VP1 := 0; VP2 := 0;
            Pilot := NOT PilotFail;
        END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        ELSIF StepElapsed >= IgnitionTime THEN
            IF Pilot THEN
                Pilot := 0; State := 7; StepElapsed := 0.0;   (* -> Run *)
                RunEstablished := 0;
            ELSE
                LockReason := 12; VP1:=0; VP2:=0; Pilot:=0;
                IF NOT AutoMode THEN VPS := 1; END_IF;
                Lockout := 1; State := 8;                     (* pilot failed to light *)
            END_IF;
        END_IF;

    7: (* RUN *)
        IF AutoMode THEN
            VP1 := 1; VP2 := 1;
        ELSE
            StepElapsed := StepElapsed + TickSeconds;
            IF (NOT RunEstablished) AND (StepElapsed >= LightOffTime) THEN
                IF VP1 AND VP2 AND (NOT Pilot) THEN RunEstablished := 1;
                ELSE LockReason := 13; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8; END_IF;
            END_IF;
            IF RunEstablished AND NOT (VP1 AND VP2) THEN
                LockReason := 7; VP1:=0; VP2:=0; Pilot:=0; VPS:=1; Lockout:=1; State:=8;
            END_IF;
        END_IF;
        IF NOT LGP THEN
            LockReason := 2; VP1:=0; VP2:=0; Pilot:=0; Lockout:=1; State:=8;
        END_IF;

END_CASE;

(* Flame_Main is on only in RUN with both valves open; Flame_Pilot follows Pilot.
   The HMI uses these two bits plus the pipe/valve/LED bits for animation. *)
Flame_Main  := (State = 7) AND VP1 AND VP2;
Flame_Pilot := Pilot;
