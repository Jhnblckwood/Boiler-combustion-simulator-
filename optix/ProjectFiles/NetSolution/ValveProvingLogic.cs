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
///
/// Proving sequence (before light-off):
///   1. EVACUATE  - V2 opens, test volume vents to the burner/stack
///   2. TEST V1   - both valves closed; pressure must stay LOW.
///                  A rise means V1 is leaking gas into the test volume.
///   3. FILL      - V1 opens, test volume charges to supply pressure
///   4. TEST V2   - both valves closed; pressure must stay HIGH.
///                  A decay means V2 (or the downstream train) is leaking.
///   5. PROVEN    - prepurge, then pilot trial for ignition
///   6. RUN       - both valves open, flame established
/// Any VPS = TRUE during a test period drives a safety LOCKOUT that is
/// cleared only by STOP/RESET (mirrors 7800 SERIES lockout behavior).
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
    private IUAVariable vp1Var, vp2Var, vpsVar;
    private IUAVariable autoModeVar, leakV1Var, leakV2Var;
    private IUAVariable chamberPressureVar, supplyPressureVar;
    private IUAVariable stateVar, stateTextVar;

    // Widgets
    private Rectangle bannerRect, pipeSupply, pipeChamber, pipeDownstream;
    private PolyLine valveBody1, valveBody2, flameShape;
    private Ellipse vpsLed, ledVp1, ledVp2, ledVps;
    private Ellipse[] stepLeds;
    private Label[] stepLabels;
    private Label stateLabel, timerLabel, pressureLabel;
    private Button modeButton, startButton, vp1Button, vp2Button, leak1Button, leak2Button;

    private PeriodicTask periodicTask;

    private Step step = Step.Standby;
    private float stepElapsed;
    private float chamberPressure;
    private bool vpsForced;
    private string lockoutReason = "";
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
        leak1Button = Owner.Get<Button>("Leak1Button");
        leak2Button = Owner.Get<Button>("Leak2Button");

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
        vpsForced = false;

        periodicTask = new PeriodicTask(Tick, 100, LogicObject);
        periodicTask.Start();
    }

    public override void Stop()
    {
        periodicTask?.Cancel();
        periodicTask = null;
    }

    // ------------------------------------------------------------------
    // Operator commands (wired to screen buttons)
    // ------------------------------------------------------------------

    [ExportMethod]
    public void StartSequence()
    {
        if (!ReadBool(autoModeVar))
            return;
        // Start only from standby; a lockout must be reset first,
        // like a 7800 SERIES relay module.
        if (step != Step.Standby)
            return;

        lockoutReason = "";
        vpsForced = false;
        EnterStep(Step.Evacuate);
    }

    [ExportMethod]
    public void StopReset()
    {
        vp1Var.Value = false;
        vp2Var.Value = false;
        vpsForced = false;
        vpsVar.Value = false;
        lockoutReason = "";
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
        vpsForced = false;
        lockoutReason = "";
        EnterStep(Step.Standby);
    }

    [ExportMethod]
    public void ToggleVP1()
    {
        if (ReadBool(autoModeVar))
            return; // sequence owns the valves in AUTO
        vp1Var.Value = !ReadBool(vp1Var);
    }

    [ExportMethod]
    public void ToggleVP2()
    {
        if (ReadBool(autoModeVar))
            return;
        vp2Var.Value = !ReadBool(vp2Var);
    }

    [ExportMethod]
    public void ToggleVPS()
    {
        if (ReadBool(autoModeVar))
        {
            // In AUTO the VPS input is evaluated by the sequence; forcing it
            // injects a failure exactly as a real switch trip would.
            vpsForced = !vpsForced;
        }
        else
        {
            vpsVar.Value = !ReadBool(vpsVar);
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

    // ------------------------------------------------------------------
    // 100 ms scan
    // ------------------------------------------------------------------

    private void Tick()
    {
        try
        {
            bool auto = ReadBool(autoModeVar);

            SimulatePressure();

            if (auto)
                RunSequence();
            else
                RunManual();

            UpdateGraphics(auto);
        }
        catch (Exception ex)
        {
            Log.Error("ValveProvingLogic", ex.Message);
        }
    }

    private void SimulatePressure()
    {
        float supply = ReadFloat(supplyPressureVar);
        if (supply <= 0f)
            supply = 27.7f;

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

    private void RunSequence()
    {
        float supply = ReadFloat(supplyPressureVar);
        if (supply <= 0f)
            supply = 27.7f;
        float halfTrip = supply * 0.5f; // VPS pressure switch setpoint

        stepElapsed += TickSeconds;

        switch (step)
        {
            case Step.Standby:
            case Step.Lockout:
                vp1Var.Value = false;
                vp2Var.Value = false;
                break;

            case Step.Evacuate:
                vp1Var.Value = false;
                vp2Var.Value = true;
                if (stepElapsed >= EvacuateTime)
                    EnterStep(Step.TestV1);
                break;

            case Step.TestV1:
                vp1Var.Value = false;
                vp2Var.Value = false;
                SetVps(chamberPressure > halfTrip);
                if (ReadBool(vpsVar))
                {
                    lockoutReason = "V1 LEAK DETECTED (PRESSURE ROSE DURING TEST)";
                    EnterStep(Step.Lockout);
                }
                else if (stepElapsed >= TestV1Time)
                {
                    EnterStep(Step.Fill);
                }
                break;

            case Step.Fill:
                vp1Var.Value = true;
                vp2Var.Value = false;
                if (stepElapsed >= FillTime)
                    EnterStep(Step.TestV2);
                break;

            case Step.TestV2:
                vp1Var.Value = false;
                vp2Var.Value = false;
                SetVps(chamberPressure < halfTrip);
                if (ReadBool(vpsVar))
                {
                    lockoutReason = "V2 / DOWNSTREAM LEAK (PRESSURE DECAYED DURING TEST)";
                    EnterStep(Step.Lockout);
                }
                else if (stepElapsed >= TestV2Time)
                {
                    SetVps(false);
                    EnterStep(Step.Purge);
                }
                break;

            case Step.Purge:
                vp1Var.Value = false;
                vp2Var.Value = false;
                if (stepElapsed >= PurgeTime)
                    EnterStep(Step.Ignition);
                break;

            case Step.Ignition:
                vp1Var.Value = false;
                vp2Var.Value = false;
                if (stepElapsed >= IgnitionTime)
                    EnterStep(Step.Run);
                break;

            case Step.Run:
                vp1Var.Value = true;
                vp2Var.Value = true;
                break;
        }

        stateVar.Value = (int)step;
    }

    private void RunManual()
    {
        // Operator owns VP1/VP2/VPS; just report.
        step = Step.Standby;
        stateVar.Value = (int)step;
    }

    private void SetVps(bool simulatedTrip)
    {
        vpsVar.Value = simulatedTrip || vpsForced;
    }

    private void EnterStep(Step next)
    {
        step = next;
        stepElapsed = 0f;
        stateVar.Value = (int)step;
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private void UpdateGraphics(bool auto)
    {
        float supply = ReadFloat(supplyPressureVar);
        if (supply <= 0f)
            supply = 27.7f;

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
        bool vps = ReadBool(vpsVar) || (auto && vpsForced);
        bool chamberHasGas = chamberPressure > supply * 0.1f;
        bool running = auto ? step == Step.Run : (v1 && v2 && chamberHasGas);

        // Piping: yellow wherever gas is present.
        pipeSupply.FillColor = GasYellow; // always live up to V1 inlet
        pipeChamber.FillColor = (chamberHasGas || v1) ? GasYellow : PipeEmpty;
        pipeDownstream.FillColor = (v2 && (chamberHasGas || v1)) ? GasYellow : PipeEmpty;

        // Valve wedges: green open, red closed.
        valveBody1.FillColor = v1 ? ValveOpen : ValveClosed;
        valveBody2.FillColor = v2 ? ValveOpen : ValveClosed;

        // Flame with a small flicker.
        flameShape.Visible = running;
        if (running)
        {
            flickerCounter++;
            flameShape.FillColor = (flickerCounter / 3) % 2 == 0 ? FlameA : FlameB;
        }

        // VPS switch and tag LEDs.
        vpsLed.FillColor = vps ? LedRed : LedGreen;
        ledVp1.FillColor = v1 ? LedGreen : LedOff;
        ledVp2.FillColor = v2 ? LedGreen : LedOff;
        ledVps.FillColor = vps ? LedRed : LedOff;

        // Pressure readout.
        pressureLabel.Text = chamberPressure.ToString("0.0");

        // Buttons.
        modeButton.Text = auto ? "MODE: AUTO (BMS SEQUENCE)" : "MODE: MANUAL (FORCE TAGS)";
        startButton.Enabled = auto && step == Step.Standby;
        vp1Button.Enabled = !auto;
        vp2Button.Enabled = !auto;
        leak1Button.Text = ReadBool(leakV1Var) ? "SIM V1 LEAK: ON" : "SIM V1 LEAK: OFF";
        leak2Button.Text = ReadBool(leakV2Var) ? "SIM V2 LEAK: ON" : "SIM V2 LEAK: OFF";

        // Banner, timer, step list.
        if (!auto)
        {
            bannerRect.FillColor = BannerIdle;
            stateLabel.Text = "MANUAL MODE - OPERATOR CONTROLS VP1 / VP2 / VPS";
            timerLabel.Text = "T- --";
            PaintSteps(-1, false);
            stateTextVar.Value = stateLabel.Text;
            return;
        }

        float remaining = RemainingSeconds();
        timerLabel.Text = remaining >= 0f ? "T-" + Math.Ceiling(remaining).ToString("00") + " S" : "T- --";

        switch (step)
        {
            case Step.Standby:
                bannerRect.FillColor = BannerIdle;
                stateLabel.Text = "STANDBY - VALVES CLOSED - READY TO START";
                PaintSteps(-1, false);
                break;
            case Step.Evacuate:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 1: EVACUATING TEST VOLUME (V2 OPEN)";
                PaintSteps(0, false);
                break;
            case Step.TestV1:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 2: TESTING V1 - PRESSURE MUST STAY LOW";
                PaintSteps(1, false);
                break;
            case Step.Fill:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 3: FILLING TEST VOLUME (V1 OPEN)";
                PaintSteps(2, false);
                break;
            case Step.TestV2:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVE PROVING - STEP 4: TESTING V2 - PRESSURE MUST STAY HIGH";
                PaintSteps(3, false);
                break;
            case Step.Purge:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVES PROVEN - PREPURGE IN PROGRESS";
                PaintSteps(4, false);
                break;
            case Step.Ignition:
                bannerRect.FillColor = BannerTest;
                stateLabel.Text = "VALVES PROVEN - PILOT TRIAL FOR IGNITION (PTFI)";
                PaintSteps(4, false);
                break;
            case Step.Run:
                bannerRect.FillColor = BannerRun;
                stateLabel.Text = "BURNER FIRING - VP1 + VP2 OPEN - VALVE PROVING COMPLETE";
                PaintSteps(5, false);
                break;
            case Step.Lockout:
                bannerRect.FillColor = BannerAlarm;
                stateLabel.Text = "SAFETY LOCKOUT - " + lockoutReason + " - PRESS STOP / RESET";
                PaintSteps(FailedStepIndex(), true);
                break;
        }

        stateTextVar.Value = stateLabel.Text;
    }

    private float RemainingSeconds()
    {
        switch (step)
        {
            case Step.Evacuate: return EvacuateTime - stepElapsed;
            case Step.TestV1: return TestV1Time - stepElapsed;
            case Step.Fill: return FillTime - stepElapsed;
            case Step.TestV2: return TestV2Time - stepElapsed;
            case Step.Purge: return PurgeTime - stepElapsed;
            case Step.Ignition: return IgnitionTime - stepElapsed;
            default: return -1f;
        }
    }

    private int FailedStepIndex()
    {
        if (lockoutReason.StartsWith("V1"))
            return 1;
        if (lockoutReason.StartsWith("V2"))
            return 3;
        return -1;
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
