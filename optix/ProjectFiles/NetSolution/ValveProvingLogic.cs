#region Using directives
using System;
using UAManagedCore;
using FTOptix.Core;
using FTOptix.CoreBase;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.UI;
#endregion

/// <summary>
/// Valve Proving System (VPS) sequence and screen animation.
///
/// Models the startup valve proving performed by a Honeywell 7800 SERIES
/// burner management system on a double-block gas train fitted with
/// Siemens SKP15 (SSOV V1) and SKP25 regulator/actuator (SSOV V2).
///
/// Tags (Model folder):
///   VP1 - Boolean, upstream safety shutoff valve V1 open
///   VP2 - Boolean, downstream safety shutoff valve V2 open
///   VPS - Boolean, valve proving switch input; TRUE = test failed
///   Pilot - Boolean, pilot valve/flame. Only legal during the ignition
///           trial; TRUE at any other time trips a safety lockout.
///
/// Proving sequence (before light-off):
///   1. EVACUATE  - V2 opens, test volume vents to the burner/stack
///   2. TEST V1   - both valves closed; pressure must stay LOW.
///                  A rise means V1 is leaking gas into the test volume.
///   3. FILL      - V1 opens, test volume charges to supply pressure
///   4. TEST V2   - both valves closed; pressure must stay HIGH.
///                  A decay means V2 (or the downstream train) is leaking.
///   5. PROVEN    - prepurge, then pilot trial for ignition. The pilot
///                  (fed from a separate line not shown on the train)
///                  burns while VP1/VP2 stay closed for the whole trial.
///   6. RUN       - both valves open, main flame established
///
/// Both modes run the SAME sequence, steps, lights, and timers:
///   AUTO   - the logic drives VP1/VP2/Pilot itself.
///   MANUAL - a training drill: the operator performs each step with the
///            VP1/VP2/PILOT buttons. Pressing a control that is wrong for
///            the current step fails the VPS immediately, and a step
///            timer elapsing without the required action also fails the
///            VPS. Either way the result is a safety lockout that only
///            STOP/RESET clears (mirrors 7800 SERIES lockout behavior).
/// </summary>
public class ValveProvingLogic : BaseNetLogic
{
    // --- Sequence timing, seconds -------------------------------------
    private const float EvacuateTime = 5.0f;
    private const float TestV1Time = 10.0f;
    private const float FillTime = 5.0f;
    private const float TestV2Time = 10.0f;
    private const float PurgeTime = 10.0f;
    private const float IgnitionTime = 4.0f;
    private const float LightOffTime = 10.0f; // manual RUN establishment window

    // --- Pressure model, inches w.c. ----------------------------------
    private const float LeakRate = 2.2f;       // simulated seat leak, "w.c. per second
    private const float TickSeconds = 0.1f;

    private enum Step
    {
        Standby = 0,
        Evacuate = 1,
        TestV1 = 2,
        Fill = 3,
        TestV2 = 4,
        Purge = 5,
        Ignition = 6,
        Run = 7,
        Lockout = 8
    }

    // Model variables
    private IUAVariable vp1Var, vp2Var, vpsVar, pilotVar;
    private IUAVariable autoModeVar, leakV1Var, leakV2Var, pilotFailVar;
    private IUAVariable chamberPressureVar, supplyPressureVar;
    private IUAVariable stateVar, stateTextVar;

    // Widgets
    private Rectangle bannerRect, pipeSupply, pipeChamber, pipeDownstream;
    private PolyLine valveBody1, valveBody2, flameShape, pilotFlame;
    private Ellipse vpsLed, ledVp1, ledVp2, ledVps;
    private Ellipse[] stepLeds;
    private Label[] stepLabels;
    private Label stateLabel, timerLabel, pressureLabel;
    private Button modeButton, startButton, vp1Button, vp2Button, pilotButton, leak1Button, leak2Button, pilotFailButton;

    private PeriodicTask periodicTask;

    private Step step = Step.Standby;
    private float stepElapsed;
    private float chamberPressure;
    private bool runEstablished;
    private string lockoutReason = "";
    private int failedStepIndex = -1;
    private int flickerCounter;

    // Palette
    private static readonly Color GasYellow = new Color(0xFFFFC400u);
    private static readonly Color PipeEmpty = new Color(0xFF3B4654u);
    private static readonly Color ValveOpen = new Color(0xFF2ECC71u);
    private static readonly Color ValveClosed = new Color(0xFFE74C3Cu);
    private static readonly Color LedOff = new Color(0xFF3B4654u);
    private static readonly Color LedGreen = new Color(0xFF2ECC71u);
    private static readonly Color LedRed = new Color(0xFFE74C3Cu);
    private static readonly Color LedYellow = new Color(0xFFFFC400u);
    private static readonly Color BannerIdle = new Color(0xFF212D3Bu);
    private static readonly Color BannerTest = new Color(0xFF1F4E79u);
    private static readonly Color BannerRun = new Color(0xFF14532Du);
    private static readonly Color BannerAlarm = new Color(0xFF7F1D1Du);
    private static readonly Color TextDim = new Color(0xFF8FA3B8u);
    private static readonly Color TextBright = new Color(0xFFFFFFFFu);
    private static readonly Color FlameA = new Color(0xFFFF8C00u);
    private static readonly Color FlameB = new Color(0xFFFFB000u);

    public override void Start()
    {
        vp1Var = Project.Current.GetVariable("Model/VP1");
        vp2Var = Project.Current.GetVariable("Model/VP2");
        vpsVar = Project.Current.GetVariable("Model/VPS");
        pilotVar = Project.Current.GetVariable("Model/Pilot");
        pilotFailVar = Project.Current.GetVariable("Model/PilotFail");
        autoModeVar = Project.Current.GetVariable("Model/AutoMode");
        leakV1Var = Project.Current.GetVariable("Model/LeakV1");
        leakV2Var = Project.Current.GetVariable("Model/LeakV2");
        chamberPressureVar = Project.Current.GetVariable("Model/ChamberPressure");
        supplyPressureVar = Project.Current.GetVariable("Model/SupplyPressure");
        stateVar = Project.Current.GetVariable("Model/State");
        stateTextVar = Project.Current.GetVariable("Model/StateText");

        bannerRect = Owner.Get<Rectangle>("BannerRect");
        pipeSupply = Owner.Get<Rectangle>("PipeSupply");
        pipeChamber = Owner.Get<Rectangle>("PipeChamber");
        pipeDownstream = Owner.Get<Rectangle>("PipeDownstream");
        valveBody1 = Owner.Get<PolyLine>("ValveBody1");
        valveBody2 = Owner.Get<PolyLine>("ValveBody2");
        flameShape = Owner.Get<PolyLine>("FlameShape");
        pilotFlame = Owner.Get<PolyLine>("PilotFlame");
        vpsLed = Owner.Get<Ellipse>("VpsLed");
        ledVp1 = Owner.Get<Ellipse>("LedVP1");
        ledVp2 = Owner.Get<Ellipse>("LedVP2");
        ledVps = Owner.Get<Ellipse>("LedVPS");
        stateLabel = Owner.Get<Label>("StateLabel");
        timerLabel = Owner.Get<Label>("TimerLabel");
        pressureLabel = Owner.Get<Label>("PressureLabel");
        modeButton = Owner.Get<Button>("ModeButton");
        startButton = Owner.Get<Button>("StartButton");
        vp1Button = Owner.Get<Button>("Vp1Button");
        vp2Button = Owner.Get<Button>("Vp2Button");
        pilotButton = Owner.Get<Button>("PilotButton");
        leak1Button = Owner.Get<Button>("Leak1Button");
        leak2Button = Owner.Get<Button>("Leak2Button");
        pilotFailButton = Owner.Get<Button>("PilotFailButton");

        stepLeds = new Ellipse[6];
        stepLabels = new Label[6];
        for (int i = 0; i < 6; i++)
        {
            stepLeds[i] = Owner.Get<Ellipse>("StepLed" + (i + 1));
            stepLabels[i] = Owner.Get<Label>("StepLabel" + (i + 1));
        }

        chamberPressure = ReadFloat(chamberPressureVar);
        step = Step.Standby;
        stepElapsed = 0f;
        runEstablished = false;

        periodicTask = new PeriodicTask(Tick, 100, LogicObject);
        periodicTask.Start();
    }

    public override void Stop()
    {
        periodicTask?.Cancel();
        periodicTask = null;
    }

    // ------------------------------------------------------------------
    // Step targets: which controls must be ON during each step
    // ------------------------------------------------------------------

    private static bool TargetV1(Step s) => s == Step.Fill || s == Step.Run;
    private static bool TargetV2(Step s) => s == Step.Evacuate || s == Step.Run;
    private static bool TargetPilot(Step s) => s == Step.Ignition;

    private static bool InSequence(Step s) => s != Step.Standby && s != Step.Lockout;

    private static int ChecklistIndex(Step s)
    {
        switch (s)
        {
            case Step.Evacuate: return 0;
            case Step.TestV1: return 1;
            case Step.Fill: return 2;
            case Step.TestV2: return 3;
            case Step.Purge: return 4;
            case Step.Ignition: return 4;
            case Step.Run: return 5;
            default: return -1;
        }
    }

    // ------------------------------------------------------------------
    // Operator commands (wired to screen buttons)
    // ------------------------------------------------------------------

    [ExportMethod]
    public void StartSequence()
    {
        // Works in BOTH modes. Proving always starts from a closed train;
        // a lockout must be reset first, like a 7800 SERIES relay module.
        if (step != Step.Standby)
            return;
        if (ReadBool(vp1Var) || ReadBool(vp2Var) || ReadBool(pilotVar))
            return;

        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        vpsVar.Value = false;
        EnterStep(Step.Evacuate);
    }

    [ExportMethod]
    public void StopReset()
    {
        vp1Var.Value = false;
        vp2Var.Value = false;
        pilotVar.Value = false;
        vpsVar.Value = false;
        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        EnterStep(Step.Standby);
    }

    [ExportMethod]
    public void ToggleMode()
    {
        bool auto = !ReadBool(autoModeVar);
        autoModeVar.Value = auto;
        // Changing mode always brings the train to a safe state.
        vp1Var.Value = false;
        vp2Var.Value = false;
        pilotVar.Value = false;
        vpsVar.Value = false;
        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        EnterStep(Step.Standby);
    }

    [ExportMethod]
    public void ToggleVP1()
    {
        if (ReadBool(autoModeVar) || step == Step.Lockout)
            return; // sequence owns the valves in AUTO
        HandleManualValveToggle(vp1Var, TargetV1(step));
    }

    [ExportMethod]
    public void ToggleVP2()
    {
        if (ReadBool(autoModeVar) || step == Step.Lockout)
            return;
        HandleManualValveToggle(vp2Var, TargetV2(step));
    }

    [ExportMethod]
    public void TogglePilot()
    {
        if (ReadBool(autoModeVar) || step == Step.Lockout)
            return; // in AUTO only the ignition trial may light the pilot

        bool turningOn = !ReadBool(pilotVar);
        if (!turningOn)
        {
            pilotVar.Value = false; // shutting the pilot is always safe
            return;
        }

        if (step == Step.Ignition)
        {
            // Lights unless the pilot is simulated (or really) failed.
            pilotVar.Value = !ReadBool(pilotFailVar);
        }
        else if (step == Step.Standby)
        {
            Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: false);
        }
        else
        {
            VpsFailLockout("VPS FAIL - INVALID CONTROL FOR CURRENT STEP (PILOT)");
        }
    }

    [ExportMethod]
    public void ToggleLeakV1()
    {
        leakV1Var.Value = !ReadBool(leakV1Var);
    }

    [ExportMethod]
    public void ToggleLeakV2()
    {
        leakV2Var.Value = !ReadBool(leakV2Var);
    }

    [ExportMethod]
    public void TogglePilotFail()
    {
        pilotFailVar.Value = !ReadBool(pilotFailVar);
    }

    /// <summary>
    /// Manual VP1/VP2 press. Standby is free play (watch the gas move);
    /// once running established, any valve change is invalid; during the
    /// sequence, opening a valve the step does not call for fails the VPS.
    /// </summary>
    private void HandleManualValveToggle(IUAVariable valveVar, bool targetOn)
    {
        if (step == Step.Run && runEstablished)
        {
            VpsFailLockout("VPS FAIL - CONTROL CHANGED DURING RUN");
            return;
        }

        bool turningOn = !ReadBool(valveVar);
        if (turningOn && InSequence(step) && !targetOn)
        {
            VpsFailLockout("VPS FAIL - INVALID CONTROL FOR CURRENT STEP");
            return;
        }

        valveVar.Value = turningOn;
    }

    // ------------------------------------------------------------------
    // 100 ms scan
    // ------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            bool auto = ReadBool(autoModeVar);

            SimulatePressure();
            RunSequence(auto);
            UpdateGraphics(auto);
        }
        catch (Exception ex)
        {
            Log.Error("ValveProvingLogic", ex.Message);
        }
    }

    private void SimulatePressure()
    {
        float supply = SupplyPressure();

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
        bool leak1 = ReadBool(leakV1Var);
        bool leak2 = ReadBool(leakV2Var);

        if (v1)
        {
            // Charging from supply dominates everything else.
            chamberPressure += (supply - chamberPressure) * 0.5f;
        }
        else if (v2)
        {
            // Venting through the burner side.
            chamberPressure += (0f - chamberPressure) * 0.35f;
        }
        else
        {
            // Both valves closed: only seat leakage moves the pressure.
            if (leak1)
                chamberPressure += LeakRate * TickSeconds;
            if (leak2)
                chamberPressure -= LeakRate * TickSeconds;
        }

        if (chamberPressure < 0f) chamberPressure = 0f;
        if (chamberPressure > supply) chamberPressure = supply;

        chamberPressureVar.Value = chamberPressure;
    }

    private void RunSequence(bool auto)
    {
        float halfTrip = SupplyPressure() * 0.5f; // VPS pressure switch setpoint

        if (step == Step.Lockout)
        {
            vp1Var.Value = false;
            vp2Var.Value = false;
            pilotVar.Value = false;
            stateVar.Value = (int)step;
            return;
        }

        if (step == Step.Standby)
        {
            if (auto)
            {
                vp1Var.Value = false;
                vp2Var.Value = false;
            }
            // Pilot supervision applies in standby too (stuck pilot valve
            // on real I/O, or an operator forcing it in manual mode).
            if (ReadBool(pilotVar))
                Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: false);
            stateVar.Value = (int)step;
            return;
        }

        stepElapsed += TickSeconds;

        if (auto)
        {
            // The logic is the BMS: drive the step's target state.
            vp1Var.Value = TargetV1(step);
            vp2Var.Value = TargetV2(step);
            pilotVar.Value = step == Step.Ignition && !ReadBool(pilotFailVar);
        }

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
        bool pilot = ReadBool(pilotVar);

        // Pilot supervision during the sequence (both modes; in manual the
        // pilot button already blocks this, but real I/O could force it).
        if (pilot && !TargetPilot(step) && !(step == Step.Run && !runEstablished))
        {
            Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: !auto);
            return;
        }

        // VPS pressure switch evaluation during the hold tests (both modes).
        if (step == Step.TestV1)
        {
            vpsVar.Value = chamberPressure > halfTrip;
            if (ReadBool(vpsVar))
            {
                VpsFailLockout("V1 LEAK DETECTED (PRESSURE ROSE DURING TEST)");
                return;
            }
        }
        else if (step == Step.TestV2)
        {
            vpsVar.Value = chamberPressure < halfTrip;
            if (ReadBool(vpsVar))
            {
                VpsFailLockout("V2 / DOWNSTREAM LEAK (PRESSURE DECAYED DURING TEST)");
                return;
            }
        }

        // RUN: no timer in AUTO; in MANUAL the operator has a light-off
        // window to open both valves and shut the pilot.
        if (step == Step.Run)
        {
            if (!auto && !runEstablished && stepElapsed >= LightOffTime)
            {
                if (v1 && v2 && !pilot)
                    runEstablished = true;
                else
                {
                    VpsFailLockout("VPS FAIL - LIGHT-OFF NOT COMPLETED IN TIME");
                    return;
                }
            }
            if (!auto && runEstablished && !(v1 && v2))
            {
                VpsFailLockout("VPS FAIL - CONTROL CHANGED DURING RUN");
                return;
            }
            stateVar.Value = (int)step;
            return;
        }

        // Step completion on timer elapse.
        if (stepElapsed >= StepDuration(step))
        {
            if (step == Step.Ignition)
            {
                if (pilot)
                {
                    pilotVar.Value = false; // interrupted pilot: off at RUN
                    EnterStep(Step.Run);
                }
                else if (auto)
                {
                    Lockout("PILOT FAILED TO LIGHT DURING IGNITION TRIAL", assertVps: false);
                }
                else
                {
                    VpsFailLockout("PILOT FAILED TO LIGHT DURING IGNITION TRIAL");
                }
            }
            else if (auto || StateMatchesTarget(v1, v2, pilot))
            {
                EnterStep(NextStep(step));
            }
            else
            {
                VpsFailLockout("VPS FAIL - REQUIRED ACTION NOT COMPLETED IN TIME");
            }
        }

        stateVar.Value = (int)step;
    }

    private bool StateMatchesTarget(bool v1, bool v2, bool pilot)
    {
        return v1 == TargetV1(step) && v2 == TargetV2(step) && pilot == TargetPilot(step);
    }

    private static Step NextStep(Step s)
    {
        switch (s)
        {
            case Step.Evacuate: return Step.TestV1;
            case Step.TestV1: return Step.Fill;
            case Step.Fill: return Step.TestV2;
            case Step.TestV2: return Step.Purge;
            case Step.Purge: return Step.Ignition;
            case Step.Ignition: return Step.Run;
            default: return Step.Standby;
        }
    }

    private static float StepDuration(Step s)
    {
        switch (s)
        {
            case Step.Evacuate: return EvacuateTime;
            case Step.TestV1: return TestV1Time;
            case Step.Fill: return FillTime;
            case Step.TestV2: return TestV2Time;
            case Step.Purge: return PurgeTime;
            case Step.Ignition: return IgnitionTime;
            default: return float.MaxValue;
        }
    }

    private void Lockout(string reason, bool assertVps)
    {
        lockoutReason = reason;
        failedStepIndex = ChecklistIndex(step);
        vp1Var.Value = false;
        vp2Var.Value = false;
        pilotVar.Value = false;
        if (assertVps)
            vpsVar.Value = true;
        EnterStep(Step.Lockout);
    }

    private void VpsFailLockout(string reason)
    {
        Lockout(reason, assertVps: true);
    }

    private void EnterStep(Step next)
    {
        step = next;
        stepElapsed = 0f;
        if (next == Step.Run)
            runEstablished = false;
        stateVar.Value = (int)step;
    }

    private float SupplyPressure()
    {
        float supply = ReadFloat(supplyPressureVar);
        return supply > 0f ? supply : 27.7f;
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private void UpdateGraphics(bool auto)
    {
        float supply = SupplyPressure();

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
        bool vps = ReadBool(vpsVar);
        bool chamberHasGas = chamberPressure > supply * 0.1f;
        bool running = step == Step.Run && v1 && v2;

        // Piping: yellow wherever gas is present.
        pipeSupply.FillColor = GasYellow; // always live up to V1 inlet
        pipeChamber.FillColor = (chamberHasGas || v1) ? GasYellow : PipeEmpty;
        pipeDownstream.FillColor = (v2 && (chamberHasGas || v1)) ? GasYellow : PipeEmpty;

        // Valve wedges: green open, red closed.
        valveBody1.FillColor = v1 ? ValveOpen : ValveClosed;
        valveBody2.FillColor = v2 ? ValveOpen : ValveClosed;

        // Flames with a small flicker. The pilot flame follows the Pilot
        // tag directly (so real I/O drives it too); it is fed from a pilot
        // line not shown on the train, and the main valves stay closed
        // until the ignition trial countdown completes.
        bool pilotLit = ReadBool(pilotVar);
        flameShape.Visible = running;
        pilotFlame.Visible = pilotLit;
        if (running || pilotLit)
        {
            flickerCounter++;
            Color flicker = (flickerCounter / 3) % 2 == 0 ? FlameA : FlameB;
            if (running)
                flameShape.FillColor = flicker;
            if (pilotLit)
                pilotFlame.FillColor = flicker;
        }

        // VPS switch and tag LEDs.
        vpsLed.FillColor = vps ? LedRed : LedGreen;
        ledVp1.FillColor = v1 ? LedGreen : LedOff;
        ledVp2.FillColor = v2 ? LedGreen : LedOff;
        ledVps.FillColor = vps ? LedRed : LedOff;

        // Pressure readout.
        pressureLabel.Text = chamberPressure.ToString("0.0");

        // Buttons.
        modeButton.Text = auto ? "MODE: AUTO (BMS SEQUENCE)" : "MODE: MANUAL (OPERATOR DRILL)";
        startButton.Enabled = step == Step.Standby && !v1 && !v2 && !pilotLit;
        vp1Button.Enabled = !auto;
        vp2Button.Enabled = !auto;
        pilotButton.Enabled = !auto;
        leak1Button.Text = ReadBool(leakV1Var) ? "SIM V1 LEAK: ON" : "SIM V1 LEAK: OFF";
        leak2Button.Text = ReadBool(leakV2Var) ? "SIM V2 LEAK: ON" : "SIM V2 LEAK: OFF";
        pilotFailButton.Text = ReadBool(pilotFailVar) ? "SIM PILOT FAIL: ON" : "SIM PILOT FAIL: OFF";

        // Banner, timer, and step list are the same in both modes; only
        // standby and run texts differ so the operator knows what to do.
        float remaining = RemainingSeconds(auto);
        timerLabel.Text = remaining >= 0f ? "T-" + Math.Ceiling(remaining).ToString("00") + " S" : "T- --";

        switch (step)
        {
            case Step.Standby:
                bannerRect.FillColor = BannerIdle;
                stateLabel.Text = auto
                    ? "STANDBY - VALVES CLOSED - READY TO START"
                    : "MANUAL DRILL - PRESS START BURNER, THEN WORK THE CONTROLS AT EACH STEP";
                PaintSteps(-1, false);
                break;
            case Step.Evacuate:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 1: EVACUATE TEST VOLUME (OPEN V2)";
                PaintSteps(0, false);
                break;
            case Step.TestV1:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 2: TESTING V1 (ALL VALVES CLOSED) - PRESSURE MUST STAY LOW";
                PaintSteps(1, false);
                break;
            case Step.Fill:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 3: FILL TEST VOLUME (OPEN V1)";
                PaintSteps(2, false);
                break;
            case Step.TestV2:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 4: TESTING V2 (ALL VALVES CLOSED) - PRESSURE MUST STAY HIGH";
                PaintSteps(3, false);
                break;
            case Step.Purge:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVES PROVEN - PREPURGE IN PROGRESS (ALL VALVES CLOSED)";
                PaintSteps(4, false);
                break;
            case Step.Ignition:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = pilotLit
                    ? "PILOT TRIAL FOR IGNITION (PTFI) - PILOT LIT, MAIN VALVES CLOSED"
                    : "PILOT TRIAL FOR IGNITION (PTFI) - AWAITING PILOT FLAME";
                PaintSteps(4, false);
                break;
            case Step.Run:
                if (!auto && !runEstablished)
                {
                    bannerRect.FillColor = BannerTest;
                    stateLabel.Text = "LIGHT-OFF - OPEN VP1 + VP2, THEN PILOT OFF";
                }
                else
                {
                    bannerRect.FillColor = BannerRun;
                    stateLabel.Text = "BURNER FIRING - VP1 + VP2 OPEN - VALVE PROVING COMPLETE";
                }
                PaintSteps(5, false);
                break;
            case Step.Lockout:
                bannerRect.FillColor = BannerAlarm;
                stateLabel.Text = "SAFETY LOCKOUT - " + lockoutReason + " - PRESS STOP / RESET";
                PaintSteps(failedStepIndex, true);
                break;
        }

        stateTextVar.Value = stateLabel.Text;
    }

    private float RemainingSeconds(bool auto)
    {
        switch (step)
        {
            case Step.Evacuate: return EvacuateTime - stepElapsed;
            case Step.TestV1: return TestV1Time - stepElapsed;
            case Step.Fill: return FillTime - stepElapsed;
            case Step.TestV2: return TestV2Time - stepElapsed;
            case Step.Purge: return PurgeTime - stepElapsed;
            case Step.Ignition: return IgnitionTime - stepElapsed;
            case Step.Run:
                if (!auto && !runEstablished)
                    return LightOffTime - stepElapsed;
                return -1f;
            default: return -1f;
        }
    }

    /// <summary>
    /// activeIndex: current step (0..5), earlier steps show complete.
    /// failed: paint the active step red instead of yellow.
    /// </summary>
    private void PaintSteps(int activeIndex, bool failed)
    {
        for (int i = 0; i < 6; i++)
        {
            if (activeIndex < 0)
            {
                stepLeds[i].FillColor = LedOff;
                stepLabels[i].TextColor = TextDim;
            }
            else if (i < activeIndex)
            {
                stepLeds[i].FillColor = LedGreen;
                stepLabels[i].TextColor = TextDim;
            }
            else if (i == activeIndex)
            {
                stepLeds[i].FillColor = failed ? LedRed : (i == 5 ? LedGreen : LedYellow);
                stepLabels[i].TextColor = TextBright;
            }
            else
            {
                stepLeds[i].FillColor = LedOff;
                stepLabels[i].TextColor = TextDim;
            }
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool ReadBool(IUAVariable variable)
    {
        return variable != null && (bool)variable.Value;
    }

    private static float ReadFloat(IUAVariable variable)
    {
        return variable == null ? 0f : (float)variable.Value;
    }
}
