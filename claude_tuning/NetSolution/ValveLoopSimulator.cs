#region Using directives
using System;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
using FTOptix.Core;
#endregion

/*
 * claude_tuning — 90° Valve Loop Simulator NetLogic
 * -------------------------------------------------
 * FOPDT (first-order plus dead time) process driven by a parallel-form
 * PID with derivative-on-PV and back-calculation anti-windup, through a
 * 90° rotary valve characteristic (linear / equal-% / quick-open /
 * butterfly). Mirrors the pid-tuning-trainer web simulator so gains
 * recommended by TuningAdvisorLogic can be verified before touching a
 * live loop.
 *
 * Add the variables listed in Model/ModelVariables.md as children of
 * this NetLogic. Bind PV / Setpoint / OutputPct to a Trend widget
 * (with a DataLogger) for the live chart, and ValveAngleDeg to a
 * rotating image/gauge for the valve position widget.
 *
 * Methods for buttons: StartSim(), PauseSim(), ResetSim(), Disturb().
 *
 * Shares gain variables with TuningAdvisorLogic via NodePointer or by
 * binding this NetLogic's Kp/Ki/Kd to the advisor's — see README.
 */

public class ValveLoopSimulator : BaseNetLogic
{
    private const int PeriodMs = 100;          // sim step, matches dt below
    private const double Dt = 0.1;             // seconds per step
    private const double InitialPv = 20.0;

    private PeriodicTask simTask;
    private readonly Random rng = new Random();

    // Sim state (not model variables — internal only)
    private double t, integral, prevPv, pv, output, iae, peakPv, loadDist;
    private double settledAt = -1;
    private double[] delayLine = new double[1];
    private int delayIdx;
    private bool running;

    public override void Start()
    {
        ResetSim();
        simTask = new PeriodicTask(SimStep, PeriodMs, LogicObject);
        simTask.Start();
    }

    public override void Stop()
    {
        simTask?.Dispose();
        simTask = null;
    }

    [ExportMethod]
    public void StartSim()
    {
        running = true;
        SetS("SimStatus", "Running");
    }

    [ExportMethod]
    public void PauseSim()
    {
        running = false;
        SetS("SimStatus", "Paused");
    }

    [ExportMethod]
    public void ResetSim()
    {
        running = false;
        t = 0; integral = 0; iae = 0; loadDist = 0;
        pv = InitialPv; prevPv = InitialPv; peakPv = InitialPv;
        output = 0; settledAt = -1;
        RebuildDelayLine(0);

        SetD("PV", pv);
        SetD("OutputPct", 0);
        SetD("ValveAngleDeg", 0);
        SetD("ErrorValue", GetD("Setpoint") - pv);
        SetD("OvershootPct", 0);
        SetD("SettleTimeS", 0);
        SetD("IAE", 0);
        SetS("SimStatus", "Idle");
    }

    /* Injects a decaying load step (+7 units/s, 0.9 decay per step) to
     * exercise disturbance rejection — same shape as the web trainer. */
    [ExportMethod]
    public void Disturb()
    {
        loadDist = 7.0;
    }

    private void SimStep()
    {
        if (!running) return;

        double kp = GetD("Kp"), ki = GetD("Ki"), kd = GetD("Kd");
        double sp = GetD("Setpoint");
        double maxOut = GetDOr("OutputClampPct", 100.0);
        double k = GetDOr("ProcessGain", 1.0);
        double tau = GetDOr("TimeConstantS", 18.0);
        double noise = GetDOr("NoiseLevel", 0.3);
        int valveChar = GetI("ValveCharacteristic");
        double deadTime = GetDOr("DeadTimeS", 2.0);

        // Resize dead-time delay line if the setting changed
        int wantedLen = Math.Max(1, (int)Math.Round(deadTime / Dt));
        if (wantedLen != delayLine.Length) RebuildDelayLine(output, wantedLen);

        double err = sp - pv;
        integral += err * Dt;

        // Derivative on PV — no kick on SP changes
        double dPv = -((pv - prevPv) / Dt);

        double rawOut = kp * err + ki * integral + kd * dPv;
        double clamped = Math.Min(maxOut, Math.Max(0, rawOut));

        // Back-calculation anti-windup
        if (clamped != rawOut && ki > 0)
            integral -= err * Dt * 0.5;
        output = clamped;

        // Dead time via circular buffer
        double delayed = delayLine[delayIdx];
        delayLine[delayIdx] = output;
        delayIdx = (delayIdx + 1) % delayLine.Length;

        // 90° valve characteristic: controller % → effective flow %
        double effectiveFlow = ApplyValveChar(delayed, valveChar);

        prevPv = pv;
        pv += ((k * effectiveFlow - pv) / tau) * Dt;
        pv += Gaussian(noise) * 0.05;

        // Decaying load disturbance
        if (loadDist != 0)
        {
            pv += loadDist * Dt;
            loadDist *= 0.90;
            if (Math.Abs(loadDist) < 0.01) loadDist = 0;
        }

        t += Dt;
        iae += Math.Abs(err) * Dt;
        peakPv = Math.Max(peakPv, pv);

        // Settle detection: 2% band held
        double band = Math.Max(1, sp * 0.02);
        if (Math.Abs(err) <= band)
        {
            if (settledAt < 0) settledAt = t;
        }
        else settledAt = -1;

        // Publish to model variables (Trend/DataLogger picks these up)
        SetD("PV", Math.Round(pv, 2));
        SetD("OutputPct", Math.Round(output, 1));
        SetD("ValveAngleDeg", Math.Round(output / 100.0 * 90.0, 1));
        SetD("ErrorValue", Math.Round(sp - pv, 2));
        SetD("OvershootPct", sp > 0 ? Math.Round(Math.Max(0, (peakPv - sp) / sp * 100), 1) : 0);
        SetD("IAE", Math.Round(iae, 1));
        if (settledAt > 0 && t - settledAt > 2.5)
            SetD("SettleTimeS", Math.Round(settledAt, 1));
    }

    /* Maps controller output % to effective flow % for a 90° rotary
     * valve. Equal-% uses 50:1 rangeability; butterfly approximated by
     * a sine law; quick-open by square root. */
    private static double ApplyValveChar(double pct, int valveChar)
    {
        double x = Math.Min(1.0, Math.Max(0.0, pct / 100.0));
        return valveChar switch
        {
            1 => 100.0 * (x < 1e-4 ? 0 : Math.Pow(50, x - 1)), // Equal %
            2 => 100.0 * Math.Sqrt(x),                          // Quick-open
            3 => 100.0 * Math.Sin(x * Math.PI / 2),             // Butterfly
            _ => pct                                            // Linear
        };
    }

    private double Gaussian(double s)
    {
        double u1 = Math.Max(rng.NextDouble(), 1e-8);
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * s;
    }

    private void RebuildDelayLine(double fillValue, int len = 0)
    {
        if (len <= 0)
            len = Math.Max(1, (int)Math.Round(GetDOr("DeadTimeS", 2.0) / Dt));
        delayLine = new double[len];
        for (int i = 0; i < len; i++) delayLine[i] = fillValue;
        delayIdx = 0;
    }

    /* ---------- variable helpers ---------- */
    private double GetD(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v != null ? (double)v.Value : 0.0;
    }
    private double GetDOr(string name, double fallback)
    {
        var v = LogicObject.GetVariable(name);
        return v != null ? (double)v.Value : fallback;
    }
    private int GetI(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v != null ? (int)v.Value : 0;
    }
    private void SetD(string name, double value) { var v = LogicObject.GetVariable(name); if (v != null) v.Value = value; }
    private void SetS(string name, string value) { var v = LogicObject.GetVariable(name); if (v != null) v.Value = value; }
}
