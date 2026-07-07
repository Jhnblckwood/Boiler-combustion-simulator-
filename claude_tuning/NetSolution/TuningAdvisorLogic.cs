#region Using directives
using System;
using System.Collections.Generic;
using UAManagedCore;
using FTOptix.NetLogic;
using FTOptix.HMIProject;
using FTOptix.Core;
#endregion

/*
 * claude_tuning — PID Tuning Advisor NetLogic
 * -------------------------------------------
 * Port of the pid-tuning-trainer symptom/recommendation engine to
 * FactoryTalk Optix. Attach this NetLogic anywhere in the project and
 * add the variables listed in Model/ModelVariables.md as children of
 * the NetLogic object (design time), then bind UI widgets to them.
 *
 * Inputs  (NetLogic variables): Kp, Ki, Kd, LoopType, ActuatorSpeed,
 *                               ValveCharacteristic, Symptom
 * Outputs (NetLogic variables): NewKp, NewKi, NewKd, Headline,
 *                               Reasoning, FieldNotes, Warning,
 *                               TiSeconds, TdSeconds, HealthSummary
 *
 * Call Analyze() from a button (Method invocation → this NetLogic).
 * Call LoadRecommendation() to copy NewKp/NewKi/NewKd back into
 * Kp/Ki/Kd so the ValveLoopSimulator picks them up.
 */

public class TuningAdvisorLogic : BaseNetLogic
{
    // Enum conventions used by the UI comboboxes (Int32 variables):
    // LoopType:            0=Flow 1=Pressure 2=Level 3=Temperature 4=Fuel 5=CombustionAir 6=FGR
    // ActuatorSpeed:       0=Fast(<15s) 1=Medium(15-30s) 2=Slow(30-60s) 3=VerySlow(>60s)
    // ValveCharacteristic: 0=Linear 1=EqualPercent 2=QuickOpen 3=Butterfly
    // Symptom:             see SymptomId below

    private enum SymptomId
    {
        Slow = 0,          // Too slow / sluggish
        Overshoot = 1,     // Goes past SP then recovers
        Oscillate = 2,     // Continuous hunting around SP
        Offset = 3,        // Steady-state offset
        Aggressive = 4,    // Weak response, need more authority
        SlowOpen = 5,      // Valve creeps open on demand
        SlowClose = 6,     // Valve hangs high, slow pull-back
        Noisy = 7,         // Jittery output
        Windup = 8,        // Overshoot after saturation
        Spiky = 9,         // Derivative kick on SP change
        LowFire = 10,      // Stable at high fire, hunts near minimum
        DistRej = 11       // Poor load disturbance rejection
    }

    public override void Start()
    {
        UpdateStandardForm();
        UpdateHealthSummary();
    }

    public override void Stop() { }

    [ExportMethod]
    public void Analyze()
    {
        double kp = GetD("Kp"), ki = GetD("Ki"), kd = GetD("Kd");
        int loopType = GetI("LoopType");
        int actSpeed = GetI("ActuatorSpeed");
        int valveChar = GetI("ValveCharacteristic");
        var symptom = (SymptomId)GetI("Symptom");

        double loopFactor = loopType switch
        {
            0 => 1.0,  // Flow
            1 => 1.0,  // Pressure
            2 => 0.6,  // Level
            3 => 0.5,  // Temperature
            4 => 0.85, // Fuel
            5 => 0.9,  // Combustion Air
            6 => 0.85, // FGR
            _ => 1.0
        };
        double speedFactor = actSpeed switch
        {
            0 => 1.1, 1 => 1.0, 2 => 0.75, 3 => 0.55, _ => 1.0
        };
        double charFactor = valveChar switch
        {
            0 => 1.0, 1 => 0.95, 2 => 0.8, 3 => 0.85, _ => 1.0
        };
        double f = loopFactor * speedFactor * charFactor;

        double newKp = kp, newKi = ki, newKd = kd;
        string headline = "", reasoning = "", warning = "";
        var notes = new List<string>();

        bool slowActuator = actSpeed >= 2;
        bool isTemperature = loopType == 3;
        bool isFuel = loopType == 4;
        bool isFgr = loopType == 6;
        bool isEqPct = valveChar == 1;

        switch (symptom)
        {
            case SymptomId.Slow:
                headline = "Increase proportional action, then trim integral.";
                newKp = Pct(kp, 0.35 * f);
                newKi = Pct(ki, 0.20 * f);
                reasoning = "Sluggish response almost always means Kp is too low. Raise Kp ~35% first — "
                    + "that alone should visibly speed the loop. Nudge Ki up modestly; too much Ki on a "
                    + "slow loop creates delayed overshoot. Leave Kd unchanged unless lag persists.";
                notes.Add("Watch 2–3 step changes after the bump. If PV still lags, repeat in 20% increments until you see overshoot, then back off 15%.");
                notes.Add(isTemperature
                    ? "Temperature loops have large time constants — allow a full τ before judging the new tune."
                    : "Allow at least one full settling cycle before judging.");
                notes.Add("If output saturates at 100% during ramps, the problem is flow capacity — not tuning.");
                break;

            case SymptomId.Overshoot:
                headline = "Reduce Kp and add derivative braking.";
                newKp = Pct(kp, -0.20 * f);
                newKd = Pct(kd == 0 ? 0.05 : kd, 0.35 * f);
                reasoning = "Overshoot = too much energy at the approach. Cut Kp ~20% so the loop decelerates "
                    + "earlier, and raise Kd to provide predictive damping on the rising edge. Integral is "
                    + "usually fine — only trim Ki 10–15% if you see slow post-overshoot oscillation.";
                notes.Add("If overshoot exceeds 25% of SP, use SP ramping in the controller rather than pushing Kd harder.");
                notes.Add("Noisy PV + high Kd = valve wear. Filter the PV (0.5–2 s) before increasing Kd.");
                if (slowActuator) notes.Add("Slow actuators often overshoot due to integral accumulation, not Kp. Check Ki first.");
                if (isTemperature) notes.Add("Thermal overshoot is often from mass, not gains. SP ramping is more effective than more Kd.");
                break;

            case SymptomId.Oscillate:
                headline = "Cut Kp and Ki — loop is at or above ultimate gain.";
                newKp = Mul(kp, 0.60);
                newKi = Mul(ki, 0.60);
                reasoning = "Sustained oscillation means you're at Ku (ultimate gain). Cut both Kp and Ki ~40%, "
                    + "preserving their ratio. If you can measure the period Pu, Ziegler–Nichols PI gives "
                    + "Kp ≈ 0.45·Ku and Ti ≈ 0.83·Pu.";
                notes.Add("Measure the oscillation period — it's your Pu reference for all future tuning on this loop.");
                notes.Add("Rule out mechanical causes first: tight packing, scored stem, positioner dither too high, air hammer.");
                notes.Add("Oscillation only at certain SPs = valve nonlinearity issue, not a PID gain problem.");
                warning = "Large reversing valve cycles cause actuator fatigue. Cut gains and observe before leaving the loop in AUTO.";
                break;

            case SymptomId.Offset:
                headline = "Add integral action — proportional alone cannot eliminate offset.";
                newKi = ki == 0 ? Math.Round(kp * 0.10, 3) : Pct(ki, 0.40 * f);
                reasoning = "Steady-state offset is diagnostic for insufficient Ki. Proportional control is "
                    + "self-limiting. Raise Ki ~40% (or set it to Kp×0.1 if it was zero). Don't touch Kp "
                    + "unless the loop is also sluggish.";
                notes.Add("Integral acts slowly by design. Wait 2–3 time constants before judging the new tune.");
                notes.Add("Offset only after saturation = integral windup, not lack of Ki. Enable anti-windup first.");
                notes.Add("Mechanical deadband (stem slop, positioner hysteresis) mimics offset — verify mechanicals before touching gains.");
                break;

            case SymptomId.Aggressive:
                headline = "Boost Kp substantially; raise Ki moderately.";
                newKp = Pct(kp, 0.60 * f);
                newKi = Pct(ki, 0.25 * f);
                reasoning = "If the valve barely responds to real errors, Kp is too small to matter. Push Kp up "
                    + "50–60% and nudge Ki so residual error actually drives the valve. Don't raise Kd — that "
                    + "blunts the aggressive response you're trying to create.";
                notes.Add("After the increase, make a deliberate SP step and watch the shape. Ideal: fast rise, minimal overshoot.");
                notes.Add("Output moving but process dead? That's low process gain (K), not a PID issue. Scale Kp further.");
                if (isEqPct) notes.Add("Equal-% trim: effective gain varies strongly with position. May feel fine at high openings but weak near closed.");
                break;

            case SymptomId.SlowOpen:
                headline = "Raise Kp for lift-off; trim Kd if it's damping the leading edge.";
                newKp = Pct(kp, 0.40 * f);
                newKd = kd > 0.1 ? Pct(kd, -0.25) : kd;
                reasoning = "Slow opening means the controller isn't swinging its output enough at step-in. Raise "
                    + "Kp for a sharper initial push. If Kd is significant it fights the rising edge — trim it "
                    + "20–25% to let the valve accelerate.";
                notes.Add("Verify positioner zero/span and any minimum-position interlock. A 4 mA floor at 10% opening looks exactly like a tuning problem.");
                if (slowActuator) notes.Add("A slow actuator limits opening rate regardless of output — mechanical stroke time is the hard constraint.");
                notes.Add("Boiler fuel valves: check cross-limiting or lead/lag logic that may be holding the valve back upstream of PID.");
                break;

            case SymptomId.SlowClose:
                headline = "Raise Kp for pull-down; trim Ki to stop it holding output up.";
                newKp = Pct(kp, 0.35 * f);
                newKi = Mul(ki, 0.80);
                reasoning = "Slow closing is the mirror of slow opening. Raise Kp so the controller has more "
                    + "authority on the falling edge. Trim Ki slightly — a large integral term can sustain a "
                    + "positive output bias that fights valve closure even when SP drops.";
                notes.Add("Check mechanicals first: spring-return working? Actuator air supply adequate for fail-close stroke?");
                notes.Add("High Ki with SP near zero can hold a large positive integrator bias — trim Ki or verify anti-windup is working.");
                if (isFuel)
                {
                    notes.Add("Fuel valve closing lag is a safety issue. Confirm mechanical close time is within spec by independent actuator test.");
                    warning = "On a fuel valve, slow closure is a burner safety issue. Verify the actuator closes within spec mechanically before adjusting PID.";
                }
                break;

            case SymptomId.Noisy:
                headline = "Drop Kd sharply; trim Kp slightly; add PV filter upstream.";
                newKd = Mul(kd, 0.35);
                newKp = Pct(kp, -0.10);
                reasoning = "Jittery valve output is almost always Kd amplifying measurement noise. Cut Kd to "
                    + "~35% of current. If jitter persists, trim Kp 10%. The durable fix is a 0.5–2 s "
                    + "first-order filter on the PV input — that lets you restore Kd without noise amplification.";
                notes.Add("Check signal wiring: shielded twisted pair, single-point ground, 4–20 mA loop integrity.");
                notes.Add("Pulsating flow (reciprocating pump, slug flow) is genuine process noise — PV filtering is the answer, not reducing gains.");
                notes.Add("High Kd on a noisy PV accelerates packing and actuator wear. Not worth the marginal derivative benefit.");
                break;

            case SymptomId.Windup:
                headline = "Enable anti-windup in the controller; reduce Ki as interim fix.";
                newKi = Mul(ki, 0.70);
                reasoning = "Post-saturation overshoot is classic integral windup. While the valve was pinned at "
                    + "100% (or 0%), the integrator kept accumulating. When error reversed, all that stored "
                    + "action had to unwind. Reduce Ki ~30% as a band-aid — the real fix is back-calculation "
                    + "or conditional integration in the controller.";
                notes.Add("Logix PIDE: built-in AWU — verify OUT_MAX/OUT_MIN are set and AWU mode is enabled.");
                notes.Add("Siemens LMV5: load controller handles this internally. Check Pb/Tn rather than adding logic band-aids.");
                notes.Add("If AWU isn't configurable, keep Ki low enough that saturation doesn't build excessive stored action.");
                break;

            case SymptomId.Spiky:
                headline = "Switch derivative to act on PV, not error.";
                newKd = Mul(kd, 0.70);
                reasoning = "Derivative kick happens because Kd is acting on error, which spikes instantly when "
                    + "SP steps. Derivative-on-PV fixes this: the term only sees PV changes (smooth), not SP "
                    + "jumps (instantaneous). Switch that mode in the controller. Reducing Kd 30% is the "
                    + "fallback if the mode isn't configurable.";
                notes.Add("Logix PIDE: enable the DOPV bit (Derivative of PV) instead of DOE (Derivative of Error).");
                notes.Add("Frequent operator SP steps? Add a ramp/rate-limit on the SP input upstream of the PID block.");
                notes.Add("Derivative-on-PV is best practice for virtually all real industrial processes.");
                break;

            case SymptomId.LowFire:
                headline = "Reduce Kp at low fire; consider gain scheduling for the full range.";
                newKp = Pct(kp, -0.30 * f);
                newKi = Pct(ki, -0.15 * f);
                reasoning = "Instability near minimum with stability at high fire is a valve nonlinearity "
                    + "problem. An equal-% or butterfly valve has much higher incremental gain near the closed "
                    + "position — the same Kp that's stable at 60° causes oscillation at 10°. Reducing Kp "
                    + "25–30% makes the whole range stable but sacrifices high-fire responsiveness. Gain "
                    + "scheduling is the proper fix.";
                notes.Add("Equal-% valves at 50:1 rangeability: effective gain at 10% open is ~7× higher than at 70% open.");
                notes.Add("Many combustion controllers (LMV, 7800 series, Fireye) have built-in gain scheduling — use it instead of compromising one set of gains.");
                notes.Add("Check minimum fire rate: if the burner runs below the valve's control range, hunting is an application issue, not PID.");
                notes.Add("Consider a valve with better low-flow authority (characterized ball, segmented butterfly) if this is recurring.");
                break;

            case SymptomId.DistRej:
                headline = "Raise Ki for faster load recovery; give Kp a modest boost.";
                newKi = Pct(ki, 0.35 * f);
                newKp = Pct(kp, 0.15 * f);
                reasoning = "Poor disturbance rejection means the loop is slow to return PV to SP after a load "
                    + "change. Unlike SP tracking (where Kp leads), load disturbance rejection is primarily an "
                    + "integral function. Raise Ki ~35% and give Kp a nudge. If dead time is significant, more "
                    + "aggressive Ki may cause oscillation — use the simulator's Disturb method to find the sweet spot.";
                notes.Add("Load disturbances in boilers: firing rate demand change, flue damper shift, feed water temperature swing.");
                notes.Add("Large initial spike = Kp too low. Slow recovery but small peak = Ki too low. Address accordingly.");
                notes.Add("For large, fast disturbances, feedforward (measuring the disturbance and adding directly to output) outperforms tighter PID.");
                if (isFgr) notes.Add("FGR loops: combustion air fan speed changes are major disturbances — check the fan-load curve before tightening gains.");
                break;
        }

        // Guardrails
        if (newKp <= 0) newKp = 0.05;
        if (newKi < 0) newKi = 0;
        if (newKd < 0) newKd = 0;

        SetD("NewKp", Math.Round(newKp, 3));
        SetD("NewKi", Math.Round(newKi, 3));
        SetD("NewKd", Math.Round(newKd, 3));
        SetS("Headline", headline);
        SetS("Reasoning", reasoning);
        SetS("FieldNotes", string.Join("\n• ", notes).Insert(0, "• "));
        SetS("Warning", warning);
        SetB("HasRecommendation", true);

        UpdateStandardForm();
        UpdateHealthSummary();
        Log.Info("TuningAdvisorLogic", $"Analyze: {(SymptomId)GetI("Symptom")} → Kp {newKp}, Ki {newKi}, Kd {newKd}");
    }

    [ExportMethod]
    public void LoadRecommendation()
    {
        if (!GetB("HasRecommendation"))
        {
            Log.Warning("TuningAdvisorLogic", "No recommendation to load — run Analyze first.");
            return;
        }
        SetD("Kp", GetD("NewKp"));
        SetD("Ki", GetD("NewKi"));
        SetD("Kd", GetD("NewKd"));
        UpdateStandardForm();
        UpdateHealthSummary();
        Log.Info("TuningAdvisorLogic", "Recommended gains loaded into active Kp/Ki/Kd.");
    }

    /* Ti = Kp/Ki, Td = Kd/Kp — the standard (ISA) form values most field
     * controllers are actually configured with. */
    [ExportMethod]
    public void UpdateStandardForm()
    {
        double kp = GetD("Kp"), ki = GetD("Ki"), kd = GetD("Kd");
        SetD("TiSeconds", ki > 0 ? Math.Round(kp / ki, 2) : 0); // 0 = no integral (Ti → ∞)
        SetD("TdSeconds", kp > 0 ? Math.Round(kd / kp, 3) : 0);
    }

    [ExportMethod]
    public void UpdateHealthSummary()
    {
        double kp = GetD("Kp"), ki = GetD("Ki"), kd = GetD("Kd");
        var flags = new List<string>();

        if (kp <= 0) flags.Add("Kp ≤ 0 — no proportional action");
        else if (kp > 15) flags.Add("Kp very high — oscillation risk");

        if (ki == 0) flags.Add("Ki = 0 — offset will not clear");
        else if (kp > 0 && ki / kp > 1.5) flags.Add("Ki/Kp > 1.5 — windup risk");

        if (kp > 0 && kd / kp > 0.5) flags.Add("Kd/Kp > 0.5 — noise sensitive");

        SetS("HealthSummary", flags.Count == 0 ? "Gain ratios OK" : string.Join(" | ", flags));
    }

    /* ---------- variable helpers ---------- */
    private static double Pct(double x, double p) => Math.Round(x * (1 + p), 3);
    private static double Mul(double x, double m) => Math.Round(x * m, 3);

    private double GetD(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v != null ? (double)v.Value : 0.0;
    }
    private int GetI(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v != null ? (int)v.Value : 0;
    }
    private bool GetB(string name)
    {
        var v = LogicObject.GetVariable(name);
        return v != null && (bool)v.Value;
    }
    private void SetD(string name, double value) { var v = LogicObject.GetVariable(name); if (v != null) v.Value = value; }
    private void SetS(string name, string value) { var v = LogicObject.GetVariable(name); if (v != null) v.Value = value; }
    private void SetB(string name, bool value)   { var v = LogicObject.GetVariable(name); if (v != null) v.Value = value; }
}
