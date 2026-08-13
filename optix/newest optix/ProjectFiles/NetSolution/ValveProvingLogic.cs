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
///   Interlock - Boolean INPUT, the burner interlock string (limits,
///         airflow, high-limit, etc). Nothing may begin unless it is
///         CLOSED (its light is green); starting with it open is a
///         lockout, and if it opens at ANY point during the sequence
///         or run the burner locks out immediately.
///   Lockout - Boolean output, TRUE whenever any safety lockout is
///             active; clears on STOP/RESET. Wire to I/O (horn, BMS
///             alarm input, stack light) as needed.
///   LGP - Boolean, low gas pressure switch on the inlet line (after
///         the inlet gauge, before the SKP15/V1): MAKES at/above
///         4 in. H2O and its light is on only when made. Trying to
///         start the burner before it makes is a lockout, and dropping
///         out during the sequence or run is a lockout.
///   HGP - Boolean, high gas pressure switch downstream of the SKP25
///         (V2). It must never break: it TRIPS above 70 in. H2O of
///         downstream pressure and any trip is an immediate lockout.
///
/// The inlet gauge (gauge only, no switch) at the start of the train
/// reads Model/SupplyPressure - the gas pressure entering the piping,
/// in inches of water column. Adjust it with the INLET PRESSURE +/-
/// buttons (or drive the tag from real I/O). Model/DownstreamPressure
/// feeds the HGP gauge: it sees gas only when V2 is passing it.
///
/// The LGP, HGP, and VPS settings are typed into the number input
/// boxes next to each switch (0-70 in. H2O; higher entries snap back
/// to 70) and published to Model/LGPSetpoint, Model/HGPSetpoint, and
/// Model/VPSSetpoint. The inlet gauge is the only adjustable gauge -
/// drag its needle or use the INLET +/- buttons; every other gauge is
/// a read-only display driven by the logic. LGP/HGP are trip points on their gauges'
/// pressure; the VPS setting is the ALLOWED DIFFERENTIAL during each
/// hold test: the V1 test fails if the evacuated volume gains more
/// than that, the V2 test fails if the charged volume loses more than
/// that below supply.
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
///
/// Mod motor (Honeywell Modutrol, Series 90 / 135 ohm potentiometer)
/// with HIGH FIRE / LOW FIRE end-switch inputs:
///   The controller commands the motor over the R-W-B bus: reducing the
///   R-to-W resistance (Model/ModMotorW) drives the motor CLOSED toward
///   low fire; reducing R-to-B (Model/ModMotorB) drives it OPEN toward
///   high fire (the two legs always sum to 135 ohms). Model/ModMotorR is
///   the feedback wiper - actual position in ohms, 0 = low fire, 135 =
///   high fire. Model/LowFireSwitch (made at/below 5 ohms) and
///   Model/HighFireSwitch (made at/above 130 ohms) are switch INPUTS -
///   simulated here, wired to the physical end switches on a real train.
///   The BMS drives the motor itself in both modes: high fire for the
///   prepurge, back to low fire for the pilot trial. Each drive runs a
///   10 SECOND COUNTDOWN - if the end switch has not proven when it
///   expires, the burner locks out (codes 95/96). In RUN the rate is
///   released to modulation with the RATE +/- buttons (shown as firing
///   rate % of stroke). SIM MOD MOTOR FAULT freezes the wiper to
///   demonstrate both prove-failure lockouts. On a real train, wire
///   ModMotorR/W/B and the two switch inputs (delete SimulateActuator).
///
/// The Honeywell faceplate (bottom middle) is drawn from the RM7838B,C
/// manual (66-1094-08, Fig. 10) and the user's layout sketch: blue module
/// face, red Honeywell logo block + BURNER CONTROL header, a full-width
/// two-line VFD (line 1 = phase + mm:ss like "PILOT IGN 00:04"; line 2 =
/// "*selectable" messages - flame signal / firing rate % - or
/// "(preemptive)" messages like "(HI FIRE T-09  67 OHM)"), the
/// sequence-status LED stack (POWER green; PILOT/FLAME/MAIN amber; ALARM
/// red, blinking on lockout), SCROLL/MODE/<> keys, and a working RESET
/// pushbutton (wired to StopReset). Lockouts show "LOCKOUT <fault code>"
/// + reason.
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

    // --- Gas pressure settings, inches H2O (water column) ---------------
    // LGP / HGP / VPS trip points are set at runtime with the spin boxes
    // next to each switch (defaults come from the Model setpoint tags).
    private const float SetpointMin = 0.0f;
    private const float SetpointMax = 80.0f; // entries outside 0-80 are rejected
    private const float InletMax = 80.0f;

    // --- Pressure model, inches w.c. ----------------------------------
    private const float TickSeconds = 0.1f;

    // --- Mod motor: Honeywell Modutrol, Series 90 (135 ohm pot) ---------
    // The firing-rate motor is commanded over the three-wire R-W-B bus of
    // a 135 ohm potentiometer. REDUCING the R-to-W resistance drives the
    // motor CLOSED (low fire); reducing R-to-B drives it OPEN (high fire).
    // Model/ModMotorW and Model/ModMotorB are the two command legs (they
    // always sum to 135); Model/ModMotorR is the feedback wiper - the
    // motor's actual position in ohms, 0 = low fire, 135 = high fire.
    // The LOW FIRE switch input makes at/below 5 ohms and the HIGH FIRE
    // switch input at/above 130 ohms. Driving to high fire (purge) and
    // back to low fire (ignition) each run a 10 second countdown - if the
    // switch has not proven when it expires, the burner locks out.
    private const float PotOhms = 135.0f;
    private const float LowFireMakeOhms = 5.0f;    // LF switch made at/below
    private const float HighFireMakeOhms = 130.0f; // HF switch made at/above
    private const float MotorOhmsPerSec = 17.0f;   // ~8 s full stroke
    private const float ProveWindow = 240.0f;      // 4 minutes to prove HF/LF

    // --- Flame amplifier signal, VDC -----------------------------------
    // Steady 5.0 V with flame proven; every 2 minutes it dips to a random
    // value at/above 3.5 V for half a second, then returns to 5.0 V.
    private const float FlameNominal = 5.0f;
    private const float FlameDipMin = 3.5f;
    private const float FlameDipInterval = 120.0f;
    private const float FlameDipLength = 0.5f;

    private enum Step
    {
        Standby = 0,
        PurgeHoldHigh = 1,   // drive to high fire, waiting on the HF switch
        Purge = 2,           // purge at high fire, timer counts UP to 10 s
        Evacuate = 3,        // valve proving starts here
        TestV1 = 4,
        Fill = 5,
        TestV2 = 6,
        PurgeHoldLow = 7,    // drive back to low fire, waiting on the LF switch
        Ignition = 8,        // pilot trial
        Run = 9,
        Lockout = 10
    }

    // Model variables
    private IUAVariable vp1Var, vp2Var, vpsVar, pilotVar, lockoutVar;
    private IUAVariable lgpVar, hgpVar, interlockVar, runInterlockVar, runIntlkFaultVar;
    private IUAVariable lgpSetVar, hgpSetVar, vpsSetVar;
    private IUAVariable autoModeVar;
    private IUAVariable chamberPressureVar, supplyPressureVar, downstreamPressureVar;
    private IUAVariable stateVar, stateTextVar;
    private IUAVariable modMotorRVar, modMotorWVar, modMotorBVar;
    private IUAVariable lowFireVar, highFireVar, flameSignalVar;
    private IUAVariable rateSetpointVar, firingRatePercentVar;
    private IUAVariable ratePotValueVar;   // the pot widget's own Value variable

    // Widgets
    private Rectangle bannerRect, pipeSupply, pipeChamber, pipeDownstream;
    private PolyLine valveBody1, valveBody2, flameShape, pilotFlame;
    private Ellipse vpsLed, ledVp1, ledVp2, ledVps;
    private Ellipse lgpLed, hgpLed, ledInterlock, ledRunInterlock;
    private Ellipse[] stepLeds;
    private Label[] stepLabels;
    private Label stateLabel, timerLabel, pressureLabel, inletReadout;
    private Button modeButton, startButton, vp1Button, vp2Button, pilotButton;
    private Button interlockButton, runInterlockButton;
    private Button lowFireSwButton, highFireSwButton;
    private bool manualLowFire, manualHighFire; // MANUAL-mode end switches
    private Label tagLabel4, tagLabel5;
    private CircularGauge ratePot;
    private SpinBox lgpSetInput, hgpSetInput, vpsSetInput;
    private CircularGauge inletGauge, lgpGauge, hgpGauge, pressGauge;

    // Firing rate panel + Honeywell faceplate widgets
    private Rectangle frBarFill;
    private Label frReadout;
    private bool restartPending; // running-interlock fault: auto-restart when it recloses
    private Ellipse loFireLed, hiFireLed;
    private Label hwLcdLine1, hwLcdLine2;
    private Ellipse hwPowerLed, hwPilotLed, hwFlameLed, hwMainLed, hwAlarmLed;

    private PeriodicTask periodicTask;

    private Step step = Step.Standby;
    private float stepElapsed;
    private float chamberPressure;
    private float lgpSet = 4.0f, hgpSet = 70.0f, vpsSet = 14.0f;
    private bool runEstablished;
    private string lockoutReason = "";
    private int lockoutCode;                // fault code shown on the KDM display
    private int failedStepIndex = -1;
    private int flickerCounter;
    private float motorOhms;                // mod motor position feedback, ohms (0-135)
    private float modTarget;                // operator modulation target in RUN, ohms
    private float proveElapsed;             // time on the HF/LF prove countdown
    private float flameSignal;              // flame amplifier signal, VDC
    private float flameDipTimer;            // seconds since the last dip
    private float flameDipRemaining;        // seconds left in the current dip
    private float flameDipValue = 5.0f;     // the value being held during a dip
    private readonly Random flameRandom = new Random();

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
    private static readonly Color LedAmber = new Color(0xFFFFB000u);

    public override void Start()
    {
        try
        {
            StartInternal();
            Log.Info("ValveProvingLogic", "ValveProvingLogic BUILD v14 (high fire held thru proving) started OK");
        }
        catch (Exception ex)
        {
            Log.Error("ValveProvingLogic", "Start FAILED - screen controls will be dead: " + ex.ToString());
            throw;
        }
    }

    private void StartInternal()
    {
        vp1Var = Project.Current.GetVariable("Model/VP1");
        vp2Var = Project.Current.GetVariable("Model/VP2");
        vpsVar = Project.Current.GetVariable("Model/VPS");
        pilotVar = Project.Current.GetVariable("Model/Pilot");
        lockoutVar = Project.Current.GetVariable("Model/Lockout");
        lgpVar = Project.Current.GetVariable("Model/LGP");
        hgpVar = Project.Current.GetVariable("Model/HGP");
        interlockVar = Project.Current.GetVariable("Model/Interlock");
        runInterlockVar = Project.Current.GetVariable("Model/RunningInterlock");
        runIntlkFaultVar = Project.Current.GetVariable("Model/RunIntlkFault");
        lgpSetVar = Project.Current.GetVariable("Model/LGPSetpoint");
        hgpSetVar = Project.Current.GetVariable("Model/HGPSetpoint");
        vpsSetVar = Project.Current.GetVariable("Model/VPSSetpoint");
        autoModeVar = Project.Current.GetVariable("Model/AutoMode");
        chamberPressureVar = Project.Current.GetVariable("Model/ChamberPressure");
        supplyPressureVar = Project.Current.GetVariable("Model/SupplyPressure");
        downstreamPressureVar = Project.Current.GetVariable("Model/DownstreamPressure");
        stateVar = Project.Current.GetVariable("Model/State");
        stateTextVar = Project.Current.GetVariable("Model/StateText");
        modMotorRVar = Project.Current.GetVariable("Model/ModMotorR");
        modMotorWVar = Project.Current.GetVariable("Model/ModMotorW");
        modMotorBVar = Project.Current.GetVariable("Model/ModMotorB");
        lowFireVar = Project.Current.GetVariable("Model/LowFireSwitch");
        highFireVar = Project.Current.GetVariable("Model/HighFireSwitch");
        flameSignalVar = Project.Current.GetVariable("Model/FlameSignal");
        rateSetpointVar = Project.Current.GetVariable("Model/RateSetpoint");
        firingRatePercentVar = Project.Current.GetVariable("Model/FiringRatePercent");

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
        lgpLed = Owner.Get<Ellipse>("LgpLed");
        hgpLed = Owner.Get<Ellipse>("HgpLed");
        ledInterlock = Owner.Get<Ellipse>("LedInterlock");
        ledRunInterlock = Owner.Get<Ellipse>("LedRunInterlock");
        tagLabel4 = Owner.Get<Label>("TagLabel4");
        tagLabel5 = Owner.Get<Label>("TagLabel5");
        stateLabel = Owner.Get<Label>("StateLabel");
        timerLabel = Owner.Get<Label>("TimerLabel");
        pressureLabel = Owner.Get<Label>("PressureLabel");
        inletReadout = Owner.Get<Label>("InletReadout");
        modeButton = Owner.Get<Button>("ModeButton");
        startButton = Owner.Get<Button>("StartButton");
        vp1Button = Owner.Get<Button>("Vp1Button");
        vp2Button = Owner.Get<Button>("Vp2Button");
        pilotButton = Owner.Get<Button>("PilotButton");
        interlockButton = Owner.Get<Button>("InterlockButton");
        runInterlockButton = Owner.Get<Button>("RunInterlockButton");
        lowFireSwButton = Owner.Get<Button>("LowFireSwButton");
        highFireSwButton = Owner.Get<Button>("HighFireSwButton");
        lgpSetInput = Owner.Get<SpinBox>("LgpSetInput");
        hgpSetInput = Owner.Get<SpinBox>("HgpSetInput");
        vpsSetInput = Owner.Get<SpinBox>("VpsSetInput");
        inletGauge = Owner.Get<CircularGauge>("InletGauge");
        lgpGauge = Owner.Get<CircularGauge>("LGPGauge");
        hgpGauge = Owner.Get<CircularGauge>("HGPGauge");
        pressGauge = Owner.Get<CircularGauge>("PressGauge");

        frBarFill = Owner.Get<Rectangle>("FrBarFill");
        frReadout = Owner.Get<Label>("FrReadout");
        ratePot = Owner.Get<CircularGauge>("RatePot");
        loFireLed = Owner.Get<Ellipse>("LoFireLed");
        hiFireLed = Owner.Get<Ellipse>("HiFireLed");
        hwLcdLine1 = Owner.Get<Label>("HwLcdLine1");
        hwLcdLine2 = Owner.Get<Label>("HwLcdLine2");
        hwPowerLed = Owner.Get<Ellipse>("HwPowerLed");
        hwPilotLed = Owner.Get<Ellipse>("HwPilotLed");
        hwFlameLed = Owner.Get<Ellipse>("HwFlameLed");
        hwMainLed = Owner.Get<Ellipse>("HwMainLed");
        hwAlarmLed = Owner.Get<Ellipse>("HwAlarmLed");

        stepLeds = new Ellipse[6];
        stepLabels = new Label[6];
        for (int i = 0; i < 6; i++)
        {
            stepLeds[i] = Owner.Get<Ellipse>("StepLed" + (i + 1));
            stepLabels[i] = Owner.Get<Label>("StepLabel" + (i + 1));
        }

        chamberPressure = ReadFloat(chamberPressureVar);
        lgpSet = Clamp(ReadFloat(lgpSetVar));
        hgpSet = Clamp(ReadFloat(hgpSetVar));
        vpsSet = Clamp(ReadFloat(vpsSetVar));
        lgpSetInput.Value = lgpSet;
        hgpSetInput.Value = hgpSet;
        vpsSetInput.Value = vpsSet;
        inletGauge.Value = ReadFloat(supplyPressureVar);
        step = Step.Standby;
        stepElapsed = 0f;
        runEstablished = false;
        motorOhms = 0f;
        modTarget = 0f;
        proveElapsed = 0f;

        // Track the pot LIVE: subscribing to the widget's Value variable
        // fires on every movement of the knob, so the firing rate follows
        // the drag immediately instead of waiting for the mouse release.
        ratePotValueVar = ratePot.GetVariable("Value");
        ratePotValueVar.VariableChange += RatePotChanged;
        ApplyRatePot((float)ratePot.Value);

        periodicTask = new PeriodicTask(Tick, 100, LogicObject);
        periodicTask.Start();
    }

    public override void Stop()
    {
        if (ratePotValueVar != null)
            ratePotValueVar.VariableChange -= RatePotChanged;
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
            case Step.PurgeHoldHigh: return 0;
            case Step.Purge: return 1;
            case Step.Evacuate: return 2;
            case Step.TestV1: return 2;
            case Step.Fill: return 3;
            case Step.TestV2: return 3;
            case Step.PurgeHoldLow: return 4;
            case Step.Ignition: return 5;
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

        // The running interlock must be closed too (no lockout - it is a
        // permissive that simply holds the start).
        if (!ReadBool(runInterlockVar))
            return;

        // Nothing may begin unless the interlock string is closed.
        if (!ReadBool(interlockVar))
        {
            Lockout("INTERLOCK OPEN - START ATTEMPTED WITH INTERLOCK STRING NOT CLOSED", assertVps: false, code: 19);
            return;
        }

        // Trying to start before the LGP has made is a safety lockout.
        if (!ReadBool(lgpVar))
        {
            Lockout("LOW GAS PRESSURE - START ATTEMPTED BEFORE LGP MADE (NEEDS " + lgpSet.ToString("0.#") + " IN. H2O)", assertVps: false, code: 17);
            return;
        }

        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        vpsVar.Value = false;
        EnterStep(Step.PurgeHoldHigh);
    }

    [ExportMethod]
    public void StopReset()
    {
        vp1Var.Value = false;
        vp2Var.Value = false;
        pilotVar.Value = false;
        vpsVar.Value = false;
        lockoutVar.Value = false;
        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        restartPending = false;
        runIntlkFaultVar.Value = false;
        manualLowFire = false;
        manualHighFire = false;
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
        lockoutVar.Value = false;
        lockoutReason = "";
        failedStepIndex = -1;
        runEstablished = false;
        manualLowFire = false;
        manualHighFire = false;
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
            // Low fire was already proven by the PURGE HOLD step before
            // the trial began, so the pilot may light.
            pilotVar.Value = true;
        }
        else if (step == Step.Standby)
        {
            Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: false, code: 25);
        }
        else
        {
            VpsFailLockout("VPS FAIL - INVALID CONTROL FOR CURRENT STEP (PILOT)", code: 55);
        }
    }

    /// <summary>
    /// Opens/closes the burner interlock string. Nothing may begin unless
    /// it is closed, and opening it at any point trips a safety lockout.
    /// Wired to both the MANUAL "INTERLOCK" control and the simulation
    /// "SIM OPEN INTERLOCK" button.
    /// </summary>
    [ExportMethod]
    public void ToggleInterlock()
    {
        interlockVar.Value = !ReadBool(interlockVar);
    }

    /// <summary>
    /// Opens/closes the RUNNING interlock. Unlike the (safety) interlock
    /// string this is NOT a latching lockout: opening it drops the burner
    /// out on a fault, and when it recloses the sequence restarts on its
    /// own - no STOP/RESET and no START press needed.
    /// </summary>
    [ExportMethod]
    public void ToggleRunInterlock()
    {
        runInterlockVar.Value = !ReadBool(runInterlockVar);
    }

    /// <summary>
    /// MANUAL-mode end switches: in the drill the OPERATOR is the field
    /// switch - the purge holds wait for these to be made (within the
    /// 4 minute windows). In AUTO the motor position makes them instead.
    /// </summary>
    [ExportMethod]
    public void ToggleLowFireSw()
    {
        if (ReadBool(autoModeVar) || step == Step.Lockout)
            return;
        manualLowFire = !manualLowFire;
    }

    [ExportMethod]
    public void ToggleHighFireSw()
    {
        if (ReadBool(autoModeVar) || step == Step.Lockout)
            return;
        manualHighFire = !manualHighFire;
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
            VpsFailLockout("VPS FAIL - CONTROL CHANGED DURING RUN", code: 56);
            return;
        }

        bool turningOn = !ReadBool(valveVar);
        if (turningOn && InSequence(step) && !targetOn)
        {
            VpsFailLockout("VPS FAIL - INVALID CONTROL FOR CURRENT STEP", code: 55);
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

            ReadInletGauge();
            SyncSetpoints();
            SimulatePressure();
            UpdateGasPressureSwitches();
            SimulateActuator();
            UpdateFlameSignal();
            RunSequence(auto);
            UpdateGraphics(auto);
        }
        catch (Exception ex)
        {
            Log.Error("ValveProvingLogic", ex.ToString());
        }
    }

    private static float Clamp(float v)
    {
        if (v < SetpointMin) return SetpointMin;
        if (v > SetpointMax) return SetpointMax; // reject anything above 70
        return v;
    }

    /// <summary>
    /// The inlet gauge is the ONLY adjustable gauge: dragging its needle
    /// (or the INLET +/- buttons) sets the incoming gas pressure.
    /// </summary>
    private void ReadInletGauge()
    {
        float g = (float)inletGauge.Value;
        if (g < 0f) g = 0f;
        if (g > InletMax) g = InletMax;
        if ((float)inletGauge.Value != g)
            inletGauge.Value = g;
        supplyPressureVar.Value = g;
    }

    /// <summary>
    /// The numeric spin boxes own the trip settings. The widget itself
    /// only accepts numbers (numeric keypad, no letters) and enforces
    /// 0-80 in. H2O; the logic clamps again as a backstop and publishes
    /// to the Model setpoint tags.
    /// </summary>
    private void SyncSetpoints()
    {
        lgpSet = SyncSetpoint(lgpSetInput, lgpSetVar);
        hgpSet = SyncSetpoint(hgpSetInput, hgpSetVar);
        vpsSet = SyncSetpoint(vpsSetInput, vpsSetVar);
    }

    private float SyncSetpoint(SpinBox box, IUAVariable setVar)
    {
        float value = Clamp((float)box.Value);
        if ((float)box.Value != value)
            box.Value = value; // reject out-of-range entries
        setVar.Value = value;
        return value;
    }

    /// <summary>
    /// Where the BMS commands the mod motor for the current phase, as the
    /// R-W resistance it presents on the Series 90 bus (0 = closed / low
    /// fire, 135 = open / high fire).
    /// </summary>
    private float TargetOhms()
    {
        switch (step)
        {
            case Step.PurgeHoldHigh: return PotOhms; // drive open to high fire
            case Step.Purge: return PotOhms;         // purge holds at high fire
            case Step.Evacuate:                      // stay at HIGH FIRE through
            case Step.TestV1:                        // the whole valve proving
            case Step.Fill:                          // procedure; only come down
            case Step.TestV2: return PotOhms;        // at the low-fire hold
            case Step.Run: return modTarget;         // released to modulation
            default: return 0f;                      // drive closed to low fire
        }
    }

    /// <summary>
    /// Series 90 mod motor simulation. The controller sets the two command
    /// legs (ModMotorW = R-W ohms, ModMotorB = R-B ohms, always summing to
    /// 135): reducing R-W drives the motor CLOSED, reducing R-B drives it
    /// OPEN. The motor travels toward the commanded position at mod-motor
    /// speed (frozen by the fault sim) and its feedback wiper publishes to
    /// ModMotorR. The LOW FIRE switch input makes at/below 5 ohms, the
    /// HIGH FIRE switch input at/above 130 ohms. On a real train, delete
    /// this and wire ModMotorR/W/B and the two end-switch inputs to the
    /// field devices.
    /// </summary>
    /// <summary>
    /// Fires on every movement of the potentiometer, so the rate command
    /// follows the knob live while it is being dragged.
    /// </summary>
    private void RatePotChanged(object sender, VariableChangeEventArgs e)
    {
        try
        {
            ApplyRatePot((float)e.NewValue);
        }
        catch (Exception ex)
        {
            Log.Error("ValveProvingLogic", ex.ToString());
        }
    }

    /// <summary>
    /// The firing-rate potentiometer (0-100%) is the operator's rate
    /// command; it takes effect in RUN, where the BMS has released the
    /// motor to modulation. The knob is never written back to from the
    /// logic, so a drag is never fought or snapped back.
    /// </summary>
    private void ApplyRatePot(float pct)
    {
        if (pct < 0f) pct = 0f;
        if (pct > 100f) pct = 100f;
        modTarget = pct / 100f * PotOhms;
        if (rateSetpointVar != null)
            rateSetpointVar.Value = pct;
    }

    private void SimulateActuator()
    {
        float command = TargetOhms();
        modMotorWVar.Value = command;           // R-W leg: reduced -> drive closed
        modMotorBVar.Value = PotOhms - command; // R-B leg: reduced -> drive open

        float delta = MotorOhmsPerSec * TickSeconds;
        if (motorOhms < command)
            motorOhms = Math.Min(motorOhms + delta, command);
        else if (motorOhms > command)
            motorOhms = Math.Max(motorOhms - delta, command);

        if (motorOhms < 0f) motorOhms = 0f;
        if (motorOhms > PotOhms) motorOhms = PotOhms;

        modMotorRVar.Value = motorOhms;         // feedback wiper position
        if (ReadBool(autoModeVar))
        {
            lowFireVar.Value = motorOhms <= LowFireMakeOhms;
            highFireVar.Value = motorOhms >= HighFireMakeOhms;
        }
        else
        {
            // MANUAL drill: the operator works the end switches.
            lowFireVar.Value = manualLowFire;
            highFireVar.Value = manualHighFire;
        }
    }

    /// <summary>
    /// Flame amplifier signal. With flame proven it sits at a steady 5.0 V;
    /// every 2 minutes it dips for half a second to a random value at or
    /// above 3.5 V, then returns to 5.0 V. No flame reads 0.0 V.
    /// </summary>
    private void UpdateFlameSignal()
    {
        bool flame = ReadBool(pilotVar) || (step == Step.Run && ReadBool(vp1Var) && ReadBool(vp2Var));
        if (!flame)
        {
            flameSignal = 0f;
            flameDipTimer = 0f;
            flameDipRemaining = 0f;
            return;
        }

        if (flameDipRemaining > 0f)
        {
            flameDipRemaining -= TickSeconds;
            flameSignal = flameDipValue;
            if (flameDipRemaining <= 0f)
                flameSignal = FlameNominal;
            return;
        }

        flameDipTimer += TickSeconds;
        if (flameDipTimer >= FlameDipInterval)
        {
            flameDipTimer = 0f;
            flameDipRemaining = FlameDipLength;
            // a random value at/above 3.5 V, below the 5.0 V nominal
            flameDipValue = FlameDipMin
                + (float)flameRandom.NextDouble() * (FlameNominal - FlameDipMin - 0.1f);
            flameSignal = flameDipValue;
            return;
        }

        flameSignal = FlameNominal;
    }

    private void UpdateGasPressureSwitches()
    {
        float supply = SupplyPressure();
        // Downstream of the SKP25 (V2): sees gas only when V2 passes it.
        float downstream = ReadBool(vp2Var) ? chamberPressure : 0f;
        downstreamPressureVar.Value = downstream;
        lgpVar.Value = supply >= lgpSet;     // LGP makes at/above its setting
        hgpVar.Value = downstream > hgpSet;  // HGP must not break
    }

    private void SimulatePressure()
    {
        float supply = SupplyPressure();

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
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
            // Both valves closed: the test volume holds (a real leaking
            // seat would move it - drive ChamberPressure from the field
            // transmitter to see an actual leak).
        }

        if (chamberPressure < 0f) chamberPressure = 0f;
        if (chamberPressure > supply) chamberPressure = supply;

        chamberPressureVar.Value = chamberPressure;
    }

    private void RunSequence(bool auto)
    {
        if (step == Step.Lockout)
        {
            vp1Var.Value = false;
            vp2Var.Value = false;
            pilotVar.Value = false;
            stateVar.Value = (int)step;
            return;
        }

        // RUNNING interlock: a non-latching fault. If it opens the burner
        // drops out to standby with the fault flagged; when it recloses the
        // sequence restarts on its own - no reset, no START press.
        if (!ReadBool(runInterlockVar))
        {
            if (step != Step.Standby)
            {
                vp1Var.Value = false;
                vp2Var.Value = false;
                pilotVar.Value = false;
                vpsVar.Value = false;
                runIntlkFaultVar.Value = true;
                restartPending = true;   // rearm once it recloses
                EnterStep(Step.Standby);
                return;
            }
        }
        else if (restartPending && step == Step.Standby && !ReadBool(lockoutVar))
        {
            // Reclosed: clear the fault and restart the sequence unaided.
            restartPending = false;
            runIntlkFaultVar.Value = false;
            StartSequence();
            return;
        }

        // The (safety) interlock string must stay closed at all times: if it
        // opens at any point in the sequence or the run, lock out immediately.
        if (!ReadBool(interlockVar))
        {
            if (step != Step.Standby)
            {
                Lockout("INTERLOCK OPENED - BURNER INTERLOCK STRING BROKE", assertVps: false, code: 19);
                return;
            }
        }

        // The HGP switch must never break: any trip is an immediate lockout.
        if (ReadBool(hgpVar))
        {
            Lockout("HIGH GAS PRESSURE - HGP TRIPPED (ABOVE " + hgpSet.ToString("0.#") + " IN. H2O)", assertVps: false, code: 18);
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
                Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: false, code: 25);
            stateVar.Value = (int)step;
            return;
        }

        // The LGP must stay made for the whole sequence and run.
        if (!ReadBool(lgpVar))
        {
            Lockout("LOW GAS PRESSURE - LGP DROPPED OUT (BELOW " + lgpSet.ToString("0.#") + " IN. H2O)", assertVps: false, code: 17);
            return;
        }

        // High/low fire switch proving: the PURGE HOLD steps wait on their
        // end switch. Each hold runs a 4 MINUTE window - if the switch has
        // not made when it expires, the burner locks out (95/96).
        bool lfMade = ReadBool(lowFireVar);
        bool hfMade = ReadBool(highFireVar);
        bool proveWait = (step == Step.PurgeHoldHigh && !hfMade)
                      || (step == Step.PurgeHoldLow && !lfMade);
        if (proveWait)
        {
            proveElapsed += TickSeconds;
            if (proveElapsed >= ProveWindow)
            {
                if (step == Step.PurgeHoldHigh)
                    Lockout("HIGH FIRE SWITCH NOT PROVEN IN 4 MINUTES - MOD MOTOR NEVER REACHED HIGH FIRE", assertVps: false, code: 95);
                else
                    Lockout("LOW FIRE SWITCH NOT PROVEN IN 4 MINUTES - MOD MOTOR NEVER RETURNED TO LOW FIRE", assertVps: false, code: 96);
                return;
            }
        }
        else
        {
            proveElapsed = 0f;
            stepElapsed += TickSeconds;

            // A hold step is satisfied the moment its end switch proves.
            if (step == Step.PurgeHoldHigh || step == Step.PurgeHoldLow)
            {
                EnterStep(NextStep(step));
                stateVar.Value = (int)step;
                return;
            }
        }

        if (auto)
        {
            // The logic is the BMS: drive the step's target state. The
            // pilot may only light once the actuator is proven at LOW FIRE.
            vp1Var.Value = TargetV1(step);
            vp2Var.Value = TargetV2(step);
            pilotVar.Value = step == Step.Ignition;
        }

        bool v1 = ReadBool(vp1Var);
        bool v2 = ReadBool(vp2Var);
        bool pilot = ReadBool(pilotVar);

        // Pilot supervision during the sequence (both modes; in manual the
        // pilot button already blocks this, but real I/O could force it).
        if (pilot && !TargetPilot(step) && !(step == Step.Run && !runEstablished))
        {
            Lockout("PILOT ENABLED OUTSIDE IGNITION TRIAL", assertVps: !auto, code: 25);
            return;
        }

        // VPS pressure switch evaluation during the hold tests (both modes).
        if (step == Step.TestV1)
        {
            // Rose more than the allowed differential above the evacuated volume.
            vpsVar.Value = chamberPressure > vpsSet;
            if (ReadBool(vpsVar))
            {
                VpsFailLockout("V1 LEAK DETECTED (PRESSURE ROSE DURING TEST)", code: 91);
                return;
            }
        }
        else if (step == Step.TestV2)
        {
            // Decayed more than the allowed differential below supply pressure.
            vpsVar.Value = chamberPressure < SupplyPressure() - vpsSet;
            if (ReadBool(vpsVar))
            {
                VpsFailLockout("V2 / DOWNSTREAM LEAK (PRESSURE DECAYED DURING TEST)", code: 92);
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
                    VpsFailLockout("VPS FAIL - LIGHT-OFF NOT COMPLETED IN TIME", code: 57);
                    return;
                }
            }
            if (!auto && runEstablished && !(v1 && v2))
            {
                VpsFailLockout("VPS FAIL - CONTROL CHANGED DURING RUN", code: 56);
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
                    Lockout("PILOT FAILED TO LIGHT DURING IGNITION TRIAL", assertVps: false, code: 28);
                }
                else
                {
                    VpsFailLockout("PILOT FAILED TO LIGHT DURING IGNITION TRIAL", code: 28);
                }
            }
            else if (auto || StateMatchesTarget(v1, v2, pilot))
            {
                EnterStep(NextStep(step));
            }
            else
            {
                VpsFailLockout("VPS FAIL - REQUIRED ACTION NOT COMPLETED IN TIME", code: 57);
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
            case Step.PurgeHoldHigh: return Step.Purge;
            case Step.Purge: return Step.Evacuate;
            case Step.Evacuate: return Step.TestV1;
            case Step.TestV1: return Step.Fill;
            case Step.Fill: return Step.TestV2;
            case Step.TestV2: return Step.PurgeHoldLow;
            case Step.PurgeHoldLow: return Step.Ignition;
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
            case Step.PurgeHoldHigh: return float.MaxValue;
            case Step.PurgeHoldLow: return float.MaxValue;
            default: return float.MaxValue;
        }
    }

    private void Lockout(string reason, bool assertVps, int code = 0)
    {
        lockoutReason = reason;
        lockoutCode = code;
        failedStepIndex = ChecklistIndex(step);
        vp1Var.Value = false;
        vp2Var.Value = false;
        pilotVar.Value = false;
        if (assertVps)
            vpsVar.Value = true;
        lockoutVar.Value = true; // I/O output: any lockout drives this high
        EnterStep(Step.Lockout);
    }

    private void VpsFailLockout(string reason, int code = 0)
    {
        Lockout(reason, assertVps: true, code: code);
    }

    private void EnterStep(Step next)
    {
        step = next;
        stepElapsed = 0f;
        proveElapsed = 0f;
        if (next == Step.Run)
        {
            runEstablished = false;
            modTarget = 0f; // released to modulation from low fire
        }
        stateVar.Value = (int)step;
    }

    private float SupplyPressure()
    {
        float supply = ReadFloat(supplyPressureVar);
        return supply < 0f ? 0f : supply;
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
        pipeSupply.FillColor = supply >= 1.0f ? GasYellow : PipeEmpty; // live while inlet gas present
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

        // Gas pressure switches: LGP green only once made; HGP dark
        // unless the pressure exceeds its setting.
        lgpLed.FillColor = ReadBool(lgpVar) ? LedGreen : LedOff;
        hgpLed.FillColor = ReadBool(hgpVar) ? LedRed : LedOff;

        // Interlock: green when closed (permissive), red when open.
        bool interlockClosed = ReadBool(interlockVar);
        bool runIntlkClosed = ReadBool(runInterlockVar);
        ledInterlock.FillColor = interlockClosed ? LedGreen : LedRed;
        ledRunInterlock.FillColor = runIntlkClosed ? LedGreen : LedRed;

        // Pressure readout.
        SetText(pressureLabel, chamberPressure.ToString("0.0"));
        SetText(inletReadout, supply.ToString("0.0"));
        lgpGauge.Value = supply;
        hgpGauge.Value = ReadFloat(downstreamPressureVar);
        pressGauge.Value = chamberPressure;

        // Buttons.
        SetText(modeButton, auto ? "MODE: AUTO (BMS SEQUENCE)" : "MODE: MANUAL (OPERATOR DRILL)");
        startButton.Enabled = step == Step.Standby && !v1 && !v2 && !pilotLit && interlockClosed && runIntlkClosed;
        vp1Button.Enabled = !auto;
        vp2Button.Enabled = !auto;
        pilotButton.Enabled = !auto;
        // Manual interlock controls follow the other manual controls.
        interlockButton.Enabled = !auto;
        runInterlockButton.Enabled = !auto;
        lowFireSwButton.Enabled = !auto;
        highFireSwButton.Enabled = !auto;

        // Mod motor panel: position bar, ohm readouts, HF/LF switch lights.
        bool lfMade = ReadBool(lowFireVar);
        bool hfMade = ReadBool(highFireVar);
        bool proveWait = (step == Step.PurgeHoldHigh && !hfMade)
                      || (step == Step.PurgeHoldLow && !lfMade);
        float ratePct = motorOhms / PotOhms * 100f;
        frBarFill.Width = 2f + motorOhms / PotOhms * 168f;
        SetText(frReadout, ratePct.ToString("0") + "%");
        firingRatePercentVar.Value = ratePct;
        loFireLed.FillColor = lfMade ? LedGreen : LedOff;
        hiFireLed.FillColor = hfMade ? LedGreen : LedOff;
        // The knob is the operator's setpoint and is never overwritten by
        // the logic - the ACTUAL FIRING RATE readout beside it shows where
        // the motor really is (the BMS owns it outside RUN).

        flameSignalVar.Value = flameSignal;

        // Banner, timer, and step list are the same in both modes; only
        // standby and run texts differ so the operator knows what to do.
        string banner = "";
        string lcd1 = "", lcd2 = "";
        float remaining = proveWait ? ProveWindow - proveElapsed : RemainingSeconds(auto);
        SetText(timerLabel, remaining >= 0f ? "T-" + Math.Ceiling(remaining).ToString("00") + " S" : "T- --");
        string tRem = remaining >= 0f ? Mmss(remaining) : "--:--";

        switch (step)
        {
            case Step.Standby:
                bannerRect.FillColor = BannerIdle;
                banner = auto
                    ? "STANDBY - VALVES CLOSED - READY TO START"
                    : "MANUAL DRILL - PRESS START BURNER, THEN WORK THE CONTROLS AT EACH STEP";
                lcd1 = "STANDBY";
                lcd2 = "FLAME SIGNAL          " + flameSignal.ToString("0.0") + "V";
                if (!ReadBool(lgpVar))
                {
                    banner = "LOW GAS PRESSURE - LGP NOT MADE (BELOW " + lgpSet.ToString("0.#") + " IN. H2O) - STARTING NOW WILL LOCK OUT";
                    lcd2 = "(LGP NOT MADE)";
                }
                if (!interlockClosed)
                {
                    banner = "INTERLOCK OPEN - NOTHING CAN START UNTIL THE INTERLOCK STRING IS CLOSED";
                    lcd2 = "(INTERLOCK OPEN)";
                }
                if (!runIntlkClosed)
                {
                    banner = restartPending
                        ? "RUNNING INTERLOCK FAULT - BURNER OFF - WILL RESTART BY ITSELF WHEN IT RECLOSES (NO RESET)"
                        : "RUNNING INTERLOCK OPEN - CLOSE IT TO START";
                    lcd1 = "RUN INTLK";
                    lcd2 = "(RUN INTERLOCK OPEN)";
                }
                PaintSteps(-1, false);
                break;
            case Step.PurgeHoldHigh:
                bannerRect.FillColor = BannerTest;
                banner = "PURGE HOLD - DRIVING TO HIGH FIRE - HIGH FIRE SWITCH MUST MAKE WITHIN 4 MINUTES";
                lcd1 = "PURGE HOLD:";
                lcd2 = "(HIGH FIRE SWITCH)";
                PaintSteps(0, false);
                break;
            case Step.Purge:
                bannerRect.FillColor = BannerTest;
                banner = "PREPURGE AT HIGH FIRE (ALL VALVES CLOSED)";
                lcd1 = "PURGE       " + Mmss(stepElapsed); // counts UP to 10
                lcd2 = "FLAME SIGNAL          " + flameSignal.ToString("0.0") + "V";
                PaintSteps(1, false);
                break;
            case Step.Evacuate:
                bannerRect.FillColor = BannerTest;
                banner = "VALVE PROVING - STEP 1: EVACUATE TEST VOLUME (OPEN V2)";
                lcd1 = "VALVE PROVE  " + tRem;
                lcd2 = "(EVACUATE - V2 OPEN)";
                PaintSteps(2, false);
                break;
            case Step.TestV1:
                bannerRect.FillColor = BannerTest;
                banner = "VALVE PROVING - STEP 2: TESTING V1 (ALL VALVES CLOSED) - PRESSURE MUST STAY LOW";
                lcd1 = "VALVE PROVE  " + tRem;
                lcd2 = "(TEST V1 HOLD)";
                PaintSteps(2, false);
                break;
            case Step.Fill:
                bannerRect.FillColor = BannerTest;
                banner = "VALVE PROVING - STEP 3: FILL TEST VOLUME (OPEN V1)";
                lcd1 = "VALVE PROVE  " + tRem;
                lcd2 = "(FILL - V1 OPEN)";
                PaintSteps(3, false);
                break;
            case Step.TestV2:
                bannerRect.FillColor = BannerTest;
                banner = "VALVE PROVING - STEP 4: TESTING V2 (ALL VALVES CLOSED) - PRESSURE MUST STAY HIGH";
                lcd1 = "VALVE PROVE  " + tRem;
                lcd2 = "(TEST V2 HOLD)";
                PaintSteps(3, false);
                break;
            case Step.PurgeHoldLow:
                bannerRect.FillColor = BannerTest;
                banner = "PURGE HOLD - RETURNING TO LOW FIRE - LOW FIRE SWITCH MUST MAKE WITHIN 4 MINUTES";
                lcd1 = "PURGE HOLD";
                lcd2 = "(LOW FIRE SWITCH)";
                PaintSteps(4, false);
                break;
            case Step.Ignition:
                bannerRect.FillColor = BannerTest;
                banner = pilotLit
                    ? "PILOT TRIAL FOR IGNITION (PTFI) - PILOT LIT, MAIN VALVES CLOSED"
                    : "PILOT TRIAL FOR IGNITION (PTFI) - AWAITING PILOT FLAME";
                lcd1 = "PILOT IGN  " + tRem;
                lcd2 = "FLAME SIGNAL          " + flameSignal.ToString("0.0") + "V";
                PaintSteps(5, false);
                break;
            case Step.Run:
                if (!auto && !runEstablished)
                {
                    bannerRect.FillColor = BannerTest;
                    banner = "LIGHT-OFF - OPEN VP1 + VP2, THEN PILOT OFF";
                }
                else
                {
                    bannerRect.FillColor = BannerRun;
                    banner = "BURNER FIRING - VP1 + VP2 OPEN - VALVE PROVING COMPLETE";
                }
                lcd1 = "RUN";
                lcd2 = "FLAME SIGNAL          " + flameSignal.ToString("0.0") + "V";
                PaintSteps(5, false);
                break;
            case Step.Lockout:
                bannerRect.FillColor = BannerAlarm;
                banner = "SAFETY LOCKOUT - " + lockoutReason + " - PRESS STOP / RESET";
                lcd1 = "LOCKOUT   " + lockoutCode.ToString();
                lcd2 = "(" + (lockoutReason.Length > 24 ? lockoutReason.Substring(0, 24) : lockoutReason) + ")";
                PaintSteps(failedStepIndex, true);
                break;
        }

        SetText(stateLabel, banner);
        stateTextVar.Value = banner;

        // Honeywell 7800 SERIES faceplate: message display + LED row.
        // POWER steady; PILOT amber with the pilot valve; FLAME with any
        // flame; MAIN with the main valves firing; ALARM blinks on lockout.
        SetText(hwLcdLine1, lcd1);
        SetText(hwLcdLine2, lcd2);
        hwPowerLed.FillColor = LedGreen;
        hwPilotLed.FillColor = pilotLit ? LedAmber : LedOff;
        hwFlameLed.FillColor = (pilotLit || running) ? LedAmber : LedOff;
        hwMainLed.FillColor = running ? LedAmber : LedOff;
        hwAlarmLed.FillColor = step == Step.Lockout && (flickerCounter / 5) % 2 == 0 ? LedRed : LedOff;
        if (step == Step.Lockout)
            flickerCounter++; // keep the lockout blink running with no flame
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

    /// <summary>
    /// Label and Button texts are LocalizedText: writing a plain string
    /// can throw "Unable to cast System.String to LocalizedText". These
    /// overloads use the LocalizedText typed property with the 3-argument
    /// constructor (textId, text, localeId) - the exact pattern used by
    /// Rockwell's own template NetLogic - so the write always carries a
    /// real LocalizedText value.
    /// </summary>
    /// <summary>Seconds to the KDM's mm:ss display format.</summary>
    private static string Mmss(float seconds)
    {
        if (seconds < 0f) seconds = 0f;
        int s = (int)Math.Ceiling(seconds);
        return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
    }

    private static void SetText(Label widget, string text)
    {
        widget.LocalizedText = new LocalizedText(string.Empty, text, "en-US");
    }

    private static void SetText(Button widget, string text)
    {
        widget.LocalizedText = new LocalizedText(string.Empty, text, "en-US");
    }


    private static bool ReadBool(IUAVariable variable)
    {
        return variable != null && (bool)variable.Value;
    }

    private static float ReadFloat(IUAVariable variable)
    {
        return variable == null ? 0f : (float)variable.Value;
    }
}
