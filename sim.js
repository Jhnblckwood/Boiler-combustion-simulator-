/* ═══════════════════════════════════════════════════════════════════════════
   Boiler Combustion Simulator — shared engine
   CB CBEX-2D · Hawk ICS Parallel Positioning · 30 ppm NOx

   Loaded by BOTH desktop.html and mobile.html. Everything that affects
   behaviour — physics, state, validation, render loop, L5X export and the
   flameout/relight cycle — lives here so the two layouts cannot drift apart.

   Each page owns only its control widgets and calls startSim() with two hooks:
     renderControls(pt)   sync the page's own controls (sliders / steppers) to pt
     renderReadings(r)    update any page-specific readouts (e.g. mobile live strip)
   and changes a control by calling setControl(field, value).
   ═══════════════════════════════════════════════════════════════════════════ */
'use strict';

// ─── CONSTANTS ────────────────────────────────────────────────────────────────
const VFD_MAX_HZ = 60.0;   // 60Hz motor — full speed at 60 Hz

// Commanded control ranges (0 .. max)
const CONTROL_MAX = { gas: 100, air: 100, vfd: VFD_MAX_HZ, fgr: 100 };

// ─── NAMEPLATE DATA (CB CBEX-2D 200-350-250ST) ─────────────────────────────
const NAMEPLATE_MIN_INPUT = 1429000;   // BTU/hr min gas input
const NAMEPLATE_MAX_INPUT = 14288000;  // BTU/hr max gas input
const GAS_HHV             = 1020;      // BTU/ft³ natural gas HHV
const NAMEPLATE_MAX_CFH   = NAMEPLATE_MAX_INPUT / GAS_HHV;  // ~14,008 CFH

// ─── PHYSICS CONSTANTS (v11 — CB Hawk ICS Calibration) ───────────────────────
// Model: lambda = K * (airEff/100)^Pa / flowFrac
//   flowFrac = butterflyFlow(gas%)  — actual gas flow, not valve position
//   airEff   = airDamperFlow(damper%) * (VFD_Hz / 60) * 100
//   FGR displaces fresh air at the fan inlet (20% FGR = 80% fresh air + 20% inert)
//
// Dual-anchor calibration:
//   Light-off: gas=3%, air=10%, VFD=42Hz (70%)  → O2 ≈ 8.5%  (lean ignition)
//   Full fire: gas=100%, air=75%, VFD=57Hz (95%) → O2 ≈ 3.5% (tight high-fire trim)
//
// NOx: 90 ppm thermal base (LNE staged air) + 8 ppm prompt (Fenimore)
// FGR suppression exp = -7.5 gives ~30ppm at 18-19% FGR (CB induced FGR target)
// With realistic FGR dilution, operator must open air damper ~15% more to
// compensate for FGR — matches real commissioning experience.
const Pa = 1.02;    // air exponent
const K  = 1.53;    // air scale factor

// ─── TUNING POINTS ───────────────────────────────────────────────────────────
const POINTS = [
  { id:0,  label:"Low Fire", pct:0   },
  { id:1,  label:"Point 2",  pct:10  },
  { id:2,  label:"Point 3",  pct:20  },
  { id:3,  label:"Point 4",  pct:30  },
  { id:4,  label:"Point 5",  pct:40  },
  { id:5,  label:"Point 6",  pct:50  },
  { id:6,  label:"Point 7",  pct:60  },
  { id:7,  label:"Point 8",  pct:70  },
  { id:8,  label:"Point 9",  pct:80  },
  { id:9,  label:"Point 10", pct:90  },
  { id:10, label:"Point 11", pct:100 },
];
const NUM_POINTS = POINTS.length;
const LAST_IDX   = NUM_POINTS - 1;

// ─── FLAME STABILITY TIMING ─────────────────────────────────────────────────
// instabilityTicks counts consecutive render() calls with an unstable flame.
const IMMINENT_TICKS = 4;   // badge switches from UNSTABLE to FLAMEOUT IMMINENT
const FLAMEOUT_TICKS = 8;   // burner locks out

// ─── ANALYZER LAG ────────────────────────────────────────────────────────────
// Real flue gas analyzers respond slowly due to sample transport and cell response.
// Modelled as first-order exponential lag:  Y(n) = Y(n-1) + (X - Y(n-1)) * α
// where α = Δt / τ  (scan interval / time constant). The render loop is the scan.
const LAG_DT    = 0.25;   // seconds per render tick (250 ms)
const TAU_O2    = 8.0;    // O2 electrochemical cell  T63 ≈ 8s
const TAU_CO2   = 8.0;    // CO₂ NDIR cell            T63 ≈ 8s
const TAU_CO    = 12.0;   // CO NDIR / electrochemical T63 ≈ 12s
const TAU_NOX   = 15.0;   // NOx chemiluminescent      T63 ≈ 15s
const ALPHA_O2  = Math.min(1, LAG_DT / TAU_O2);
const ALPHA_CO2 = Math.min(1, LAG_DT / TAU_CO2);
const ALPHA_CO  = Math.min(1, LAG_DT / TAU_CO);
const ALPHA_NOX = Math.min(1, LAG_DT / TAU_NOX);

// ─── VALIDATION TARGETS ─────────────────────────────────────────────────────
// Fine-tune O2 targets per firing range (tight high-fire, relaxed light-off)
const FINE_O2_TARGETS = [
  { max: 10.0, ideal: 8.5 },  // Pt0  Light-Off — lean for safe ignition
  { max: 8.0,  ideal: 6.5 },  // Pt1  10%
  { max: 7.0,  ideal: 5.5 },  // Pt2  20%
  { max: 6.0,  ideal: 4.8 },  // Pt3  30%
  { max: 5.5,  ideal: 4.3 },  // Pt4  40%
  { max: 5.0,  ideal: 3.8 },  // Pt5  50%
  { max: 4.5,  ideal: 3.5 },  // Pt6  60%
  { max: 4.2,  ideal: 3.3 },  // Pt7  70%
  { max: 4.0,  ideal: 3.0 },  // Pt8  80%
  { max: 3.8,  ideal: 2.8 },  // Pt9  90%
  { max: 3.5,  ideal: 2.5 },  // Pt10 100% — tight trim
];

// ─── VALVE / DAMPER CHARACTERISTICS ─────────────────────────────────────────
// CB gas butterfly valve: highly nonlinear, most flow change in mid-range.
// Calibrated so 3% valve ≈ 10% flow (min fire) and 100% valve = 100% flow.
function butterflyFlow(valvePct) {
  const x = Math.max(0, valvePct / 100);
  if (x < 0.005) return 0;
  return Math.min(1.0, Math.pow(Math.sin(x * Math.PI / 2), 0.65));
}

// CB butterfly / opposed-blade air damper: quick-opening characteristic.
// At 50% position ≈ 64% of max airflow; at 10% ≈ 22%.
function airDamperFlow(damperPct) {
  const x = Math.max(0, damperPct / 100);
  if (x < 0.005) return 0;
  return Math.min(1.0, Math.pow(x, 0.65));
}

// ─── PHYSICS ─────────────────────────────────────────────────────────────────
function calcCombustion(gas, air, vfd_hz, fgr, mode) {
  // mode: 'rough' = cold furnace (default), 'fine' = hot/heat-soaked furnace
  mode = mode || 'rough';

  // Commanded position = actual position. Actuator deadband / hysteresis is
  // deliberately NOT modelled: the value-dependent wobble it added made the
  // response non-monotonic (raising a control could lower its reading).
  // Real nonlinearity comes from butterflyFlow() and airDamperFlow().
  const gasAct = Math.max(0, gas);
  const airAct = Math.max(0, air);
  const vfdAct = Math.max(0, vfd_hz);
  const fgrAct = Math.max(0, fgr);

  // Combined air: nonlinear damper characteristic × VFD speed (fan law: flow ∝ speed)
  const vfdSpd  = vfdAct / VFD_MAX_HZ * 100;   // 0-100%
  const airDamp = airDamperFlow(airAct);        // nonlinear 0-1
  const airEff  = airDamp * vfdSpd;             // effective air %

  // Gas flow via butterfly valve characteristic
  const flowFrac = butterflyFlow(gasAct);
  const btuInput = flowFrac * NAMEPLATE_MAX_INPUT;
  const cfh      = flowFrac * NAMEPLATE_MAX_CFH;
  const mmbtuIn  = btuInput / 1000000;

  const fgrFrac = fgrAct / 100;

  if (gasAct < 0.5) return { o2:0, co:0, co2:0, nox:0, eff:0, ea:0, stable:false, lambda:0, coReason:'', airEff:0, vfdSpd:0, btuInput:0, cfh:0, mmbtuIn:0, flowFrac:0, btuOutput:0, stackTemp:0 };

  const airNorm = K * Math.pow(Math.max(airEff, 0.1) / 100, Pa);

  // FGR displaces fresh air at the FD fan inlet (induced FGR system).
  // Factor 0.72 = 1 - (residual O2 in FGR / ambient O2), adjusted for imperfect
  // mixing and duct in-leakage. At 20% FGR the effective fresh-air fraction is
  // 1 - 0.20×0.72 = 0.856 → O2 drops ~1.5-2.0 points; operator must add air.
  const fgrDilution = 1 - fgrFrac * 0.72;
  const effAir  = airNorm * fgrDilution;
  const lambda  = Math.max(0.40, Math.min(7.0, effAir / Math.max(flowFrac, 0.001)));

  // ── Hot-boiler adjustments (fine-tune mode) ──────────────────────────────
  // Heat-soaked furnace: better combustion, O2 reads 0.3-0.5% lower,
  // hotter refractory = higher flame temp = more NOx, tighter CO response
  const isHot         = mode === 'fine';
  const heatSoakO2    = isHot ? 0.4  : 0;    // O2 drop from heat soak
  const furnaceTempK  = isHot ? 1.08 : 1.0;  // 8% NOx increase from hot refractory
  const coSensitivity = isHot ? 1.15 : 1.0;  // CO rises faster near stoichiometric when hot

  const rawO2 = lambda >= 1.0 ? Math.min(20.9, 20.9 * (lambda - 1) / lambda) : 0;
  const o2 = Math.max(0, rawO2 - heatSoakO2);
  const ea = Math.max(0, (lambda - 1) * 100);

  // ── CO — computed BEFORE stability so CO informs flame state ─────────────
  // Real CB firetube CO vs O2: flat at 10-30 ppm, then a near-vertical wall
  // at O2 ≈ 1.0-1.5% (λ ≈ 1.05-1.08). You're fine until suddenly you're not.
  //
  // Calibration targets (stable, FGR≈0):
  //   λ=2.5 → ~5 ppm    λ=1.8 → ~10 ppm   λ=1.4 → ~20 ppm   λ=1.15 → ~30 ppm
  //   λ=1.08 → ~50 ppm  λ=1.06 → ~320 ppm (THE KNEE)   λ=1.03 → ~700   λ=1.00 → ~1800
  const coBase = 4 + 14 * Math.exp(-2.6 * (lambda - 1.0)) * coSensitivity;

  // Rich spike — near-vertical wall below λ≈1.06, transition zone 1.06-1.12
  let coRich = 0;
  if (lambda < 1.06) {
    coRich = 300 * coSensitivity * Math.exp(12.0 * (1.06 - lambda));
  } else if (lambda < 1.12) {
    coRich = 35 * coSensitivity * Math.pow((1.12 - lambda) / 0.06, 2.0);
  }

  // Lean tail — flame cooling, quenching at extreme excess air
  const coLean = lambda > 2.8 ? 18 * Math.pow(lambda - 2.8, 2.0) : 0;

  // Firing-rate effect — higher firing rate shortens residence time slightly
  const coFiring = 4 * Math.pow(flowFrac, 1.4);

  // FGR: moderate FGR is fine, but excessive FGR quenches the flame
  let coFgrHigh = 0;
  if      (fgrFrac > 0.22) coFgrHigh = 400 * Math.pow(fgrFrac - 0.22, 1.3) * (1 + flowFrac * 1.5);
  else if (fgrFrac > 0.18) coFgrHigh = 50  * Math.pow(fgrFrac - 0.18, 1.1) * (1 + flowFrac);
  else if (fgrFrac > 0.10) coFgrHigh = 8   * (fgrFrac - 0.10) * 10;

  // FGR at light-off is particularly bad (cold furnace, weak flame).
  // Light-off region extends to ~10% gas (flowFrac≈0.25)
  const coFgrLightOff = (flowFrac < 0.25 && fgrFrac > 0.05)
    ? 120 * fgrFrac * (0.25 - flowFrac) * 16 : 0;

  // Sum of all chemistry-driven CO sources — what the analyzer would read
  // from the combustion alone, before flame degradation adds more.
  const coCombustion = coBase + coRich + coLean + coFiring + coFgrHigh + coFgrLightOff;

  // ── Stability — what the BMS flame scanner (UV / flame rod) would trip on ─
  //   λ < 0.90:  flame is orange/lazy, UV signal marginal → trips in seconds
  //   λ > 5.5:   flame lifts off, lean blow-off
  //   CO > 350:  combustion is failing — catches the λ=0.90-1.06 gap where
  //              O2 is near zero and CO is spiking
  //   O2 < 0.5% at meaningful fire (gas>8%): starved, flame integrity failing
  //   FGR > 28%: flame temperature too low, quench
  //   FGR > 8% at light-off: cold furnace can't sustain diluted flame
  const tooRich       = lambda < 0.90;
  const tooLean       = lambda > 5.5;
  const fgrQuench     = fgrFrac > 0.28;
  const fgrAtLightOff = flowFrac < 0.22 && fgrFrac > 0.08;
  const highCO        = coCombustion > 350;
  const lowO2         = o2 < 0.5 && gasAct > 8;
  const stable        = !tooRich && !tooLean && !fgrQuench && !fgrAtLightOff
                      && !highCO && !lowO2;

  // ── Instability CO — flame degradation adds unburned fuel to exhaust ─────
  let coInstability = 0;
  if (!stable) {
    let factor = 0;
    if (tooRich)       factor = Math.max(factor, (0.90 - lambda) * 6);
    if (tooLean)       factor = Math.max(factor, (lambda - 5.5) * 3.5);
    if (fgrQuench)     factor = Math.max(factor, (fgrFrac - 0.28) * 16);
    if (fgrAtLightOff) factor = Math.max(factor, 0.80);
    if (highCO)        factor = Math.max(factor, (coCombustion - 350) / 600);
    if (lowO2)         factor = Math.max(factor, (0.5 - o2) * 3);
    coInstability = Math.min(4000, 400 + 1800 * factor);
  }
  const co = Math.min(9999, Math.max(3, coCombustion + coInstability));

  // ── NOx — LNE thermal base + prompt NOx floor ────────────────────────────
  // CB LNE burner: staged air mixing reduces thermal NOx base to ~90 ppm.
  // Prompt NOx (Fenimore): ~8 ppm, NOT suppressed by FGR — sets a hard floor.
  //   Light-off (gas≈3, FGR=0, λ≈1.7): ≈ 55-65 ppm
  //   Full fire FGR=0 (λ≈1.2):         ≈ 98 ppm
  //   Full fire FGR=19% (λ≈1.2):       ≈ 28-30 ppm (CB 30ppm target)
  let tempFactor;
  if      (lambda < 1.05)  tempFactor = lambda * 0.86;
  else if (lambda <= 1.15) tempFactor = 1.0;
  else                     tempFactor = 1.0 / (1 + 0.7 * Math.pow(lambda - 1.15, 1.6));
  const fgrSuppression = Math.exp(-7.5 * fgrFrac);
  const firingFactor   = 0.55 + 0.45 * Math.pow(flowFrac, 0.30);   // small flames ≈ 0.80, full fire ≈ 1.0
  const thermalNOx = 90 * tempFactor * firingFactor * fgrSuppression * furnaceTempK;
  const promptNOx  = 8;
  let nox = thermalNOx + promptNOx;
  if (!stable) nox *= 0.70;
  nox = Math.max(0, Math.round(nox));

  // ── Efficiency — stack temp & radiation ──────────────────────────────────
  // Stack temp: firing rate is the dominant driver (turbulent flow regime),
  // ~2°F per 1% O₂, modest increase above ~10% FGR.
  //   Low fire ~320-340°F · Mid fire ~370-390°F · High fire ~440-460°F
  const stackTemp = 260 + 180 * Math.pow(flowFrac, 0.55) + o2 * 2
    + (fgrFrac > 0.10 ? (fgrFrac - 0.10) * 80 : 0);
  const dryFlueLoss  = (0.38 + ea * 0.0034) * (stackTemp - 60) / 100 * 4.2;
  const moistureLoss = 10.5;                        // H2O from hydrogen in CH4, HHV basis
  const RADIATION_BTU = 40000;                      // fixed shell loss
  const radiationLoss = btuInput > 0
    ? Math.min(3.0, (RADIATION_BTU / btuInput) * 100) : 0.5;
  const eff = Math.max(0, Math.min(99.5,
    100 - dryFlueLoss - moistureLoss - radiationLoss
    - Math.min(co, 600) * 0.003));

  // ── CO₂ — 11.7% max for natural gas at stoichiometric ────────────────────
  const co2 = Math.max(0, Math.round(11.7 * (20.9 - o2) / 20.9 * 10) / 10);

  // ── Diagnostic ───────────────────────────────────────────────────────────
  let coReason = '';
  if (!stable) {
    if (tooRich)            coReason = 'Rich — increase air damper or VFD speed';
    else if (highCO)        coReason = 'CO breakpoint — combustion failing, add air or reduce gas';
    else if (lowO2)         coReason = 'O₂ critically low — increase air damper or VFD';
    else if (tooLean)       coReason = 'Lean blow-off — reduce air or decrease VFD';
    else if (fgrQuench)     coReason = 'FGR quench — cut FGR below 28%';
    else if (fgrAtLightOff) coReason = 'FGR too early — close FGR until flame is established';
  } else if (co > 150) {
    if (fgrFrac > 0.22)    coReason = 'FGR too high — incomplete combustion';
    else if (lambda > 3.8) coReason = 'Excess air — lean quench, reduce air or VFD';
    else if (lambda < 1.06) coReason = 'Rich — approaching CO breakpoint, add air or reduce gas';
  }

  return {
    o2:      Math.round(o2 * 10) / 10,
    co2,
    co:      Math.round(co),
    nox,
    eff:     Math.round(eff * 10) / 10,
    ea:      Math.round(ea),
    stable,
    lambda:  Math.round(lambda * 100) / 100,
    coReason,
    airEff:  Math.round(airEff * 10) / 10,
    vfdSpd:  Math.round(vfdSpd * 10) / 10,
    btuInput: Math.round(btuInput),
    cfh:      Math.round(cfh),
    mmbtuIn:  Math.round(mmbtuIn * 1000) / 1000,
    flowFrac: Math.round(flowFrac * 1000) / 1000,
    btuOutput: Math.round(btuInput * (eff / 100)),
    stackTemp: Math.round(stackTemp),
  };
}

// ─── LIGHT-OFF START POSITION ───────────────────────────────────────────────
// VFD=42Hz (70%), FGR=0%. Gas and Air are randomized each fresh session /
// relight so the operator starts from a different position and must tune in
// manually — like a real boiler relight procedure.
//
// Gas max = 7%: above ~8% gas no air value within 3–14% is stable (always
// rich). test.stable is the only reliable guard — lambda alone is not enough
// because highCO triggers at λ=0.90–1.06 even when lambda looks "safe".
function freshLightOff() {
  const SAFE_FALLBACK = { gas: 3.0, air: 10.0, vfd: 42.0, fgr: 0 };
  let gas, air, attempts = 0;
  do {
    gas = 1.0 + Math.round(Math.random() * 6.0 * 10) / 10;  // 1.0–7.0%
    air = 3.0 + Math.round(Math.random() * 11.0 * 10) / 10; // 3.0–14.0%
    if (calcCombustion(gas, air, 42.0, 0).stable) break;
    attempts++;
  } while (attempts < 40);
  if (attempts >= 40) return SAFE_FALLBACK;
  return { gas, air, vfd: 42.0, fgr: 0 };
}

// ─── STATE ───────────────────────────────────────────────────────────────────
let LIGHTOFF         = freshLightOff();
let currentIdx       = 0;
let savedPoints      = Array(NUM_POINTS).fill(null);
let roughPoints      = Array(NUM_POINTS).fill(null);  // Rough-tune stored separately
let currentPt        = { ...LIGHTOFF };
let isSaved          = false;
let saveTried        = false;
let instabilityTicks = 0;
let tuneMode         = 'rough';     // 'rough' or 'fine'
let hintsUnlocked    = false;       // hints hidden until first flameout (Advanced)
let skillMode        = 'beginner';  // 'beginner' or 'advanced'
function hintsOn() { return skillMode === 'beginner' || hintsUnlocked; }

// Analyzer lag filter state
let lagO2 = 0, lagCO2 = 0, lagCO = 0, lagNOx = 0;

// Display-only noise state (flow meter jitter, thermocouple bounce)
let flowNoise  = 0;
let stackNoise = 0;

// Page hooks (set by startSim)
let hooks = { renderControls() {}, renderReadings() {} };

// Cached element lookup
const _els = {};
function el(id) { return _els[id] || (_els[id] = document.getElementById(id)); }

function seedLag(pt, mode) {
  const s = calcCombustion(pt.gas, pt.air, pt.vfd, pt.fgr, mode);
  lagO2 = s.o2; lagCO2 = s.co2; lagCO = s.co; lagNOx = s.nox;
}

// ─── VALIDATION ──────────────────────────────────────────────────────────────
function getValidation(pt, idx) {
  if (idx === 0) return null;
  const prev = savedPoints[idx - 1];
  if (!prev) return null;

  // Rough-tune ascending: gas/air/VFD must be ≥ previous point
  if (tuneMode === 'rough') {
    if (pt.gas < prev.gas) return `Gas must be ≥ Point ${idx} (${prev.gas.toFixed(1)}%)`;
    if (pt.air < prev.air) return `Air damper must be ≥ Point ${idx} (${prev.air.toFixed(1)}%)`;
    if (pt.vfd < prev.vfd) return `VFD must be ≥ Point ${idx} (${prev.vfd.toFixed(1)} Hz)`;
  }

  // Hint-level validation: shown always in Beginner, after flameout in Advanced
  if (hintsOn()) {
    const curSim = calcCombustion(pt.gas, pt.air, pt.vfd, pt.fgr, tuneMode);

    // Rough: O2 must trend down going up the curve
    if (tuneMode === 'rough') {
      const prevSim = calcCombustion(prev.gas, prev.air, prev.vfd, prev.fgr, tuneMode);
      if (curSim.o2 > prevSim.o2 + 0.5) {
        return `O₂ should not rise (prev ${prevSim.o2}% → now ${curSim.o2}%) — increase gas or reduce air/VFD`;
      }
    }

    // Fine-tune: stricter O2 targets and CO limit
    if (tuneMode === 'fine') {
      const target = FINE_O2_TARGETS[idx];
      if (curSim.o2 > target.max) {
        return `Fine tune: O₂ too high (${curSim.o2}% > ${target.max}% max) — reduce air or increase gas`;
      }
      if (curSim.co > 100) {
        return `Fine tune: CO too high (${curSim.co} ppm > 100 ppm limit) — add air or reduce FGR`;
      }
    }
  }

  return null;
}

// ─── GAUGES ──────────────────────────────────────────────────────────────────
function gaugeColor(id, value) {
  if (id === 'o2')  return value < 1.0 ? '#f87171' : (value < 2.0 || value > 8.0) ? '#fbbf24' : '#4ade80';
  if (id === 'co2') return value > 11.0 ? '#fbbf24' : value < 4.0 ? '#94a3b8' : '#60a5fa';
  if (id === 'co')  return value > 400 ? '#f87171' : value > 150 ? '#fbbf24' : '#4ade80';
  if (id === 'nox') return value > 50 ? '#fbbf24' : '#4ade80';
  if (id === 'eff') return value < 75 ? '#f87171' : value < 80 ? '#fbbf24' : '#4ade80';
  return '#4ade80';
}

function updateGauge(id, value, min, max, unit) {
  const pct   = Math.min(100, Math.max(0, ((value - min) / (max - min)) * 100));
  const color = gaugeColor(id, value);
  const val = el(id + 'Val'), bar = el(id + 'Bar');
  val.textContent = value + ' ' + unit;
  val.style.color = color;
  bar.style.width      = pct + '%';
  bar.style.background = color;
}

// ─── PROGRESS DOTS ───────────────────────────────────────────────────────────
function renderDots() {
  const container = el('progressDots');
  container.innerHTML = '';
  for (let i = 0; i < NUM_POINTS; i++) {
    const wrap = document.createElement('div');
    wrap.className = 'dot-wrap' + (i < LAST_IDX ? ' grow' : '');

    const dot = document.createElement('div');
    dot.className = 'dot';
    dot.title = POINTS[i].label;

    if (i === currentIdx) {
      dot.classList.add('current');
      const inner = document.createElement('div');
      inner.className = 'dot-inner';
      dot.appendChild(inner);
    } else if (savedPoints[i]) {
      dot.classList.add('saved');
      const inner = document.createElement('div');
      inner.className = 'dot-inner-saved';
      dot.appendChild(inner);
    } else if (i < currentIdx) {
      dot.classList.add('unsaved-past');
    }
    wrap.appendChild(dot);

    if (i < LAST_IDX) {
      const line = document.createElement('div');
      line.className = 'dot-line' + (savedPoints[i] ? ' done' : '');
      wrap.appendChild(line);
    }
    container.appendChild(wrap);
  }
}

// ─── CURVE TABLE ─────────────────────────────────────────────────────────────
function renderTable() {
  const tbody = el('curveTable');
  tbody.innerHTML = '';
  POINTS.forEach((p, i) => {
    const s     = savedPoints[i];
    const isCur = i === currentIdx;
    const tr    = document.createElement('tr');
    if (isCur) tr.className = 'cur-row';
    const ptLabel = p.pct === 0 ? 'L.F.' : p.pct + '%';
    if (s) {
      const c = calcCombustion(s.gas, s.air, s.vfd, s.fgr, s.mode || 'rough');
      tr.innerHTML = `
        <td style="color:${isCur?'#38bdf8':'#64748b'}">${ptLabel}</td>
        <td style="color:#fb923c">${s.gas.toFixed(0)}</td>
        <td style="color:#38bdf8">${s.air.toFixed(0)}</td>
        <td style="color:#22d3ee">${(s.vfd / VFD_MAX_HZ * 100).toFixed(1)}</td>
        <td style="color:#a78bfa">${s.fgr.toFixed(0)}</td>
        <td style="color:${c.o2<2?'#f87171':'#4ade80'}">${c.o2}</td>
        <td style="color:#60a5fa">${c.co2}</td>
        <td style="color:${c.co>400?'#f87171':'#94a3b8'}">${c.co}</td>
        <td style="color:${c.nox>30?'#fbbf24':'#4ade80'}">${c.nox}</td>
        <td style="color:#fb923c;font-size:10px">${c.stackTemp}</td>`;
    } else {
      tr.innerHTML = `
        <td style="color:${isCur?'#38bdf8':'#64748b'}">${ptLabel}</td>
        <td colspan="9" style="text-align:center;color:#94a3b8;font-size:10px">
          ${isCur ? '— editing —' : '· · ·'}</td>`;
    }
    tbody.appendChild(tr);
  });
}

// ─── STRUCTURE RENDER ────────────────────────────────────────────────────────
// Everything that only changes when a point is saved or the operator moves
// between points / modes. Re-rendered only when structureKey() changes, so
// the 4 Hz analyzer tick does not rebuild the dots and table every time.
let lastStructureKey = null;
function structureKey() {
  return [currentIdx, tuneMode, skillMode, hintsUnlocked,
    savedPoints.map(p => p ? `${p.gas},${p.air},${p.vfd},${p.fgr},${p.mode}` : '-').join(';')
  ].join('|');
}

function renderStructure() {
  const pt         = POINTS[currentIdx];
  const savedCount = savedPoints.filter(Boolean).length;
  const beginner   = skillMode === 'beginner';
  const fine       = tuneMode === 'fine';

  // Skill mode buttons
  el('btnBeginner').classList.toggle('active', beginner);
  el('btnAdvanced').classList.toggle('active', !beginner);

  // Tune mode toggle — hidden entirely in Beginner mode
  el('tuneModeToggle').style.display = beginner ? 'none' : '';
  el('btnRoughMode').classList.toggle('active', !fine);
  el('btnFineMode').classList.toggle('active', fine);

  // Fine-tune hint — only visible once hints are unlocked
  const fineHint = el('fineHint');
  if (hintsOn() && fine && !beginner) {
    const tgt = FINE_O2_TARGETS[currentIdx];
    fineHint.style.display = '';
    fineHint.innerHTML = `⬇ FINE TUNE — Hot boiler, tightening O₂. Target: <b>${tgt.ideal}%</b> (max ${tgt.max}%) &nbsp;·&nbsp; CO limit: <b>100 ppm</b>`;
  } else {
    fineHint.style.display = 'none';
  }

  // Analyzer lag display — faster in fine-tune mode
  el('lagDisplay').innerHTML = fine
    ? 'O₂/CO₂ lag <span class="hot">4s</span> &nbsp;·&nbsp;CO lag <span class="hot">6s</span> &nbsp;·&nbsp;NOₓ lag <span class="hot">8s</span> &nbsp;<span class="hot">HOT</span>'
    : 'O₂/CO₂ lag <span class="cold">8s</span> &nbsp;·&nbsp;CO lag <span class="cold">12s</span> &nbsp;·&nbsp;NOₓ lag <span class="cold">15s</span>';

  // Header
  const countEl = el('savedCountNum');
  countEl.innerHTML   = `${savedCount}<span>/${NUM_POINTS}</span>`;
  countEl.style.color = savedCount === NUM_POINTS ? '#4ade80' : '#38bdf8';

  // Point info
  const modeLabel = beginner ? '' : (fine ? ' (Fine)' : ' (Rough)');
  el('pointMetaLabel').textContent = (pt.id === 0 ? 'Low Fire Point' : `Tuning Point ${pt.id + 1}`) + modeLabel;
  el('pointName').textContent      = pt.label;
  el('firingPct').textContent      = pt.pct;

  // Back / Export — direction-aware
  const showBack = fine ? currentIdx < LAST_IDX : currentIdx > 0;
  el('btnBack').style.display   = showBack ? '' : 'none';
  el('btnBack').textContent     = fine ? 'BACK →' : '← BACK';
  el('btnExport').style.display = savedCount === NUM_POINTS ? '' : 'none';

  renderDots();
  renderTable();
}

// ─── LIVE RENDER (every tick and every control change) ──────────────────────
function render() {
  const sim        = calcCombustion(currentPt.gas, currentPt.air, currentPt.vfd, currentPt.fgr, tuneMode);
  const validation = getValidation(currentPt, currentIdx);

  // Dots, table, header and point labels only when something structural changed
  const key = structureKey();
  if (key !== lastStructureKey) { lastStructureKey = key; renderStructure(); }

  // Control readouts (commanded values)
  el('gasVal').childNodes[0].textContent = currentPt.gas.toFixed(1);
  el('airVal').childNodes[0].textContent = currentPt.air.toFixed(1);
  el('vfdVal').childNodes[0].textContent = (currentPt.vfd / VFD_MAX_HZ * 100).toFixed(1);
  el('vfdHz').textContent                = currentPt.vfd.toFixed(1);
  el('fgrVal').childNodes[0].textContent = currentPt.fgr.toFixed(1);
  hooks.renderControls(currentPt);

  // Gas flow meter — bounded random walk so the reading jitters like a real
  // turbine/orifice meter. Display only; sim.btuInput is untouched.
  flowNoise += (Math.random() - 0.5) * 0.006;
  flowNoise *= 0.90;   // mean-reversion
  flowNoise  = Math.max(-0.02, Math.min(0.02, flowNoise));
  const flowFactor = 1 + flowNoise;
  const cfhDisp    = sim.cfh > 0 ? Math.round(sim.cfh * flowFactor) : 0;
  const mmbtuDisp  = sim.mmbtuIn > 0 ? sim.mmbtuIn * flowFactor : 0;
  el('cfhDisplay').innerHTML   = cfhDisp.toLocaleString() + ' <span>CFH</span>';
  el('mmbtuDisplay').innerHTML = mmbtuDisp.toFixed(3) + ' <span>MMBTU/hr</span>';
  el('btuInVal').textContent   = (sim.btuInput / 1000000).toFixed(2) + 'M BTU/hr';
  el('btuInVal').style.color   =
    sim.btuInput < NAMEPLATE_MIN_INPUT && currentPt.gas >= 1 ? '#f87171' :
    sim.btuInput > NAMEPLATE_MAX_INPUT ? '#f87171' : '#fb923c';
  el('btuInBar').style.width   = Math.min(100, (sim.btuInput / NAMEPLATE_MAX_INPUT) * 100) + '%';

  // Heat output
  const outMMBTU = sim.btuOutput > 0 ? (sim.btuOutput / 1000000).toFixed(3) : '0.000';
  el('btuOutDisplay').innerHTML    = outMMBTU + ' <span class="unit">MMBTU/hr</span>';
  el('btuOutEffDisplay').innerHTML = sim.btuInput > 0 ? `<span class="eff">${sim.eff}%</span>` : '—';

  // Validation
  // Beginner: hint shown LIVE whenever a constraint is not met
  // Advanced: hint shown only after a save attempt; auto-clears once valid
  const valBox = el('validationBox');
  let showVal;
  if (skillMode === 'beginner') {
    showVal = !!validation;
  } else {
    if (saveTried && !validation) saveTried = false;
    showVal = saveTried && !!validation;
  }
  if (showVal) { valBox.style.display = ''; valBox.textContent = '⚠ ' + validation; }
  else { valBox.style.display = 'none'; }

  // Save button
  const btnSave = el('btnSave');
  btnSave.textContent = isSaved ? '✓ SAVED' : 'SAVE POINT';
  btnSave.classList.toggle('saved', isSaved);

  // Next button — direction depends on tune mode
  let canNext, nextLabel;
  if (tuneMode === 'rough') {
    canNext   = isSaved && currentIdx < LAST_IDX;
    nextLabel = currentIdx === LAST_IDX ? 'ROUGH COMPLETE ✓' : 'NEXT ⬆';
  } else {
    canNext   = isSaved && currentIdx > 0;
    nextLabel = currentIdx === 0 ? 'FINE TUNE COMPLETE ✓' : 'NEXT ⬇';
  }
  const btnNext = el('btnNext');
  btnNext.disabled    = !canNext;
  btnNext.textContent = nextLabel;
  btnNext.classList.toggle('active', canNext);

  // Sim card + flame state
  el('simCard').className = 'card' + (sim.stable ? '' : ' unstable');
  const badge = el('flameBadge');
  if (!sim.stable) {
    instabilityTicks++;
    if (instabilityTicks >= FLAMEOUT_TICKS) {
      triggerFlameout(sim.coReason || 'Sustained instability — flame extinguished');
      return;
    }
    badge.textContent = instabilityTicks >= IMMINENT_TICKS ? '⚠ FLAMEOUT IMMINENT' : '⚠ UNSTABLE';
    badge.className   = 'flame-badge flame-unstable';
  } else {
    instabilityTicks = 0;
    badge.textContent = '● FLAME STABLE';
    badge.className   = 'flame-badge flame-stable';
  }

  // Analyzer lag — first-order filter on O2, CO₂, CO, NOx.
  // Fine-tune mode: warm analyzer, established sample flow → 2x faster response.
  const lagMult = tuneMode === 'fine' ? 2.0 : 1.0;
  if (currentPt.gas < 0.5) {
    // Gas off: snap to zero immediately (no phantom readings)
    lagO2 = 0; lagCO2 = 0; lagCO = 0; lagNOx = 0;
  } else {
    lagO2  += (sim.o2  - lagO2)  * Math.min(1, ALPHA_O2  * lagMult);
    lagCO2 += (sim.co2 - lagCO2) * Math.min(1, ALPHA_CO2 * lagMult);
    lagCO  += (sim.co  - lagCO)  * Math.min(1, ALPHA_CO  * lagMult);
    lagNOx += (sim.nox - lagNOx) * Math.min(1, ALPHA_NOX * lagMult);
  }
  const dispO2  = Math.round(lagO2  * 10) / 10;
  const dispCO2 = Math.round(lagCO2 * 10) / 10;
  const dispCO  = Math.round(lagCO);
  const dispNOx = Math.round(lagNOx);

  updateGauge('o2',  dispO2,  0,  20,   '%');
  updateGauge('co2', dispCO2, 0,  12,   '%');
  updateGauge('co',  dispCO,  0,  1000, 'ppm');
  updateGauge('nox', dispNOx, 0,  60,   'ppm');
  updateGauge('eff', sim.eff, 70, 100,  '%');
  el('eaVal').textContent     = sim.ea + '%';
  el('lambdaVal').textContent = sim.lambda;

  // Stack temp thermocouple jitter — real K-type readings bounce ±3-5°F
  stackNoise += (Math.random() - 0.5) * 2.5;
  stackNoise *= 0.82;   // mean-reversion
  stackNoise  = Math.max(-5, Math.min(5, stackNoise));
  const dispStackTemp = sim.stackTemp > 0 ? sim.stackTemp + Math.round(stackNoise) : 0;
  el('stackTempVal').textContent = dispStackTemp + '°F';

  // CO hint — only visible once hints are unlocked
  const coHint = el('coHint');
  if (hintsOn() && sim.coReason) { coHint.textContent = '⚠ ' + sim.coReason; coHint.style.display = ''; }
  else { coHint.style.display = 'none'; }

  // NOx 30ppm target — only visible once hints are unlocked, from Point 3 up
  const noxTarget = el('noxTarget');
  if (hintsOn() && currentIdx >= 2) {
    const met   = dispNOx <= 30;
    const close = dispNOx <= 40 && !met;
    noxTarget.style.display     = '';
    noxTarget.textContent       = met
      ? '✓ 30 ppm NOx target met — CB LNE standard'
      : close
        ? `≈ ${dispNOx} ppm — close, add ~2% more FGR`
        : `✗ 30 ppm NOx target — increase FGR (currently ${dispNOx} ppm)`;
    noxTarget.style.color       = met ? '#4ade80' : close ? '#fbbf24' : '#f87171';
    noxTarget.style.background  = met ? '#052e16' : close ? '#292100' : '#2d0a0a';
    noxTarget.style.borderColor = met ? '#166534' : close ? '#854d0e' : '#7f1d1d';
  } else {
    noxTarget.style.display = 'none';
  }

  hooks.renderReadings({
    sim, o2: dispO2, co2: dispCO2, co: dispCO, nox: dispNOx,
    stable: sim.stable, imminent: instabilityTicks >= IMMINENT_TICKS,
  });
}

// ─── CONTROL CHANGES (called by the page's sliders / steppers) ──────────────
function setControl(field, value) {
  currentPt[field] = value;
  isSaved = false;
  // Advanced: keep saveTried true while invalid — render() auto-clears it once valid
  // Beginner: validation is live, saveTried is not used for display
  if (skillMode === 'beginner') saveTried = false;
  render();
}

// ─── SKILL MODE (Beginner / Advanced) ───────────────────────────────────────
function setSkillMode(mode) {
  if (mode === skillMode) return;
  skillMode = mode;
  // Force rough mode in Beginner (fine/rough toggle is hidden)
  if (mode === 'beginner' && tuneMode !== 'rough') {
    tuneMode    = 'rough';
    savedPoints = roughPoints;
    currentIdx  = 0;
    currentPt   = savedPoints[0] ? { ...savedPoints[0] } : { ...LIGHTOFF };
    isSaved     = !!savedPoints[0];
  }
  saveTried = false;
  render();
}

// ─── TUNE MODE (Rough ⬆ / Fine ⬇) ──────────────────────────────────────────
function setTuneMode(mode) {
  if (mode === tuneMode) return;
  if (mode === 'rough') {
    tuneMode    = 'rough';
    savedPoints = roughPoints;
    currentIdx  = 0;                              // restart ascending from low fire
    currentPt   = savedPoints[0] ? { ...savedPoints[0] } : { ...LIGHTOFF };
    isSaved     = !!savedPoints[0];
  } else {
    roughPoints = savedPoints.map(p => p ? { ...p } : null);   // keep rough data
    tuneMode    = 'fine';
    savedPoints = roughPoints.map(p => p ? { ...p } : null);   // start from rough values
    currentIdx  = LAST_IDX;                       // fine-tune descends from high fire
    currentPt   = savedPoints[LAST_IDX] ? { ...savedPoints[LAST_IDX] } : { ...LIGHTOFF };
    isSaved     = false;                          // must re-save each point in fine mode
  }
  saveTried = false;
  seedLag(currentPt, tuneMode);
  render();
}

// ─── SAVE / NEXT / BACK ─────────────────────────────────────────────────────
function savePoint() {
  saveTried = true;
  if (getValidation(currentPt, currentIdx)) { render(); return; }
  savedPoints[currentIdx] = { ...currentPt, mode: tuneMode };
  isSaved = true;
  render();
}

function nextPoint() {
  if (!isSaved) return;
  if (tuneMode === 'rough') {
    // Ascending: carry all values from the just-saved point as the baseline
    if (currentIdx >= LAST_IDX) return;
    currentIdx++;
    currentPt = { ...currentPt };
  } else {
    // Descending: load the rough-tune values as baseline for fine adjustment
    if (currentIdx <= 0) return;
    currentIdx--;
    currentPt = savedPoints[currentIdx] ? { ...savedPoints[currentIdx] } : { ...currentPt };
  }
  isSaved = false;
  render();
}

function backPoint() {
  if (tuneMode === 'rough') {
    if (currentIdx === 0) return;
    currentIdx--;
  } else {
    if (currentIdx >= LAST_IDX) return;
    currentIdx++;
  }
  currentPt = savedPoints[currentIdx] ? { ...savedPoints[currentIdx] } : { ...LIGHTOFF };
  isSaved   = !!savedPoints[currentIdx];
  saveTried = false;
  render();
}

// ─── L5X EXPORT ──────────────────────────────────────────────────────────────
function exportL5X() {
  const xml  = generateL5X(savedPoints);
  const blob = new Blob([xml], { type: 'application/xml' });
  const url  = URL.createObjectURL(blob);
  const a    = document.createElement('a');
  a.href = url; a.download = 'BoilerTuningTable.L5X'; a.click();
  URL.revokeObjectURL(url);
  const btn = el('btnExport');
  btn.textContent = 'EXPORTED ✓'; btn.classList.add('flash');
  setTimeout(() => { btn.textContent = 'EXPORT L5X'; btn.classList.remove('flash'); }, 1000);
}

function generateL5X(savedPoints) {
  const now = new Date().toDateString();
  const pts = savedPoints.map((pt, i) => {
    if (!pt) return '';
    const c  = calcCombustion(pt.gas, pt.air, pt.vfd, pt.fgr, pt.mode || tuneMode);
    const sp = Math.round((pt.vfd / VFD_MAX_HZ) * 100 * 10) / 10;
    const ae = Math.round(((pt.air / 100) * (pt.vfd / VFD_MAX_HZ) * 100) * 10) / 10;
    return `
  <Tag Name="TunePoint_${String(i+1).padStart(2,'0')}" TagType="Base" DataType="TUNE_POINT" Constant="false" ExternalAccess="Read/Write">
    <Description><![CDATA[${POINTS[i].label} - ${POINTS[i].pct}% Firing Rate]]></Description>
    <Data Format="Decorated">
      <Structure DataType="TUNE_POINT">
        <DataValueMember Name="FiringRate_Pct" DataType="REAL" Radix="Float" Value="${POINTS[i].pct}"/>
        <DataValueMember Name="GasValve_Pct"   DataType="REAL" Radix="Float" Value="${pt.gas.toFixed(1)}"/>
        <DataValueMember Name="AirDamper_Pct"  DataType="REAL" Radix="Float" Value="${pt.air.toFixed(1)}"/>
        <DataValueMember Name="VFD_Hz"         DataType="REAL" Radix="Float" Value="${pt.vfd.toFixed(1)}"/>
        <DataValueMember Name="VFD_SpeedPct"   DataType="REAL" Radix="Float" Value="${sp}"/>
        <DataValueMember Name="AirEff_Pct"     DataType="REAL" Radix="Float" Value="${ae}"/>
        <DataValueMember Name="FGR_Pct"        DataType="REAL" Radix="Float" Value="${pt.fgr.toFixed(1)}"/>
        <DataValueMember Name="O2_Setpoint"    DataType="REAL" Radix="Float" Value="${c.o2}"/>
        <DataValueMember Name="CO_Expected"    DataType="REAL" Radix="Float" Value="${c.co}"/>
        <DataValueMember Name="CO2_Pct"        DataType="REAL" Radix="Float" Value="${c.co2}"/>
        <DataValueMember Name="NOx_Expected"   DataType="REAL" Radix="Float" Value="${c.nox}"/>
        <DataValueMember Name="StackTemp_F"    DataType="REAL" Radix="Float" Value="${c.stackTemp}"/>
        <DataValueMember Name="Efficiency_Pct" DataType="REAL" Radix="Float" Value="${c.eff}"/>
        <DataValueMember Name="ExcessAir_Pct"  DataType="REAL" Radix="Float" Value="${c.ea}"/>
        <DataValueMember Name="Lambda"         DataType="REAL" Radix="Float" Value="${c.lambda}"/>
        <DataValueMember Name="GasFlow_CFH"    DataType="REAL" Radix="Float" Value="${c.cfh}"/>
        <DataValueMember Name="Input_BTU"      DataType="REAL" Radix="Float" Value="${c.btuInput}"/>
        <DataValueMember Name="Input_MMBTU"    DataType="REAL" Radix="Float" Value="${c.mmbtuIn}"/>
        <DataValueMember Name="Output_BTU"     DataType="REAL" Radix="Float" Value="${c.btuOutput}"/>
      </Structure>
    </Data>
  </Tag>`;
  }).join('\n');

  return `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<!--
  Firetube Boiler Combustion Tuning Table
  Generated: ${now} | Studio 5000 v37
  Physics: v11 (Pa=1.02 K=1.53) FlowFrac Lambda + Realistic FGR Dilution
  AirEff = airDamperFlow(damper%) x (VFD_Hz / 60) x 100
-->
<RSLogix5000Content SchemaRevision="1.0" SoftwareRevision="37.00"
  TargetName="BoilerTuningTable" TargetType="Controller" ExportDate="${now}">
<Controller Use="Target" Name="BoilerTuningTable" ProcessorType="1756-L85E" MajorRev="37" MinorRev="11">

<DataTypes>
  <DataType Name="TUNE_POINT" Family="NoFamily" Class="User">
    <Description><![CDATA[Combustion curve tuning point - Gas + AirDamper + VFD + FGR]]></Description>
    <Members>
      <Member Name="FiringRate_Pct" DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="GasValve_Pct"   DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="AirDamper_Pct"  DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="VFD_Hz"         DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="VFD_SpeedPct"   DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="AirEff_Pct"     DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="FGR_Pct"        DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="O2_Setpoint"    DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="CO_Expected"    DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="CO2_Pct"        DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="NOx_Expected"   DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="StackTemp_F"    DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="Efficiency_Pct" DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="ExcessAir_Pct"  DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="Lambda"         DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="GasFlow_CFH"    DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="Input_BTU"      DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="Input_MMBTU"    DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
      <Member Name="Output_BTU"     DataType="REAL" Dimension="0" Radix="Float" Hidden="false" ExternalAccess="Read/Write"/>
    </Members>
  </DataType>
</DataTypes>

<Tags>
  <Tag Name="TuneTable_Count" TagType="Base" DataType="INT" Radix="Decimal" Constant="false" ExternalAccess="Read/Write">
    <Description><![CDATA[Number of valid tuning points saved]]></Description>
    <Data Format="Decorated"><DataValue DataType="INT" Radix="Decimal" Value="${savedPoints.filter(Boolean).length}"/></Data>
  </Tag>
${pts}
</Tags>

</Controller>
</RSLogix5000Content>`;
}

// ─── FLAMEOUT / RELIGHT ──────────────────────────────────────────────────────
function triggerFlameout(reason) {
  instabilityTicks = 0;
  hintsUnlocked = true;   // Unlock all hints after first flameout
  currentIdx  = 0;
  savedPoints = Array(NUM_POINTS).fill(null);
  roughPoints = Array(NUM_POINTS).fill(null);
  tuneMode    = 'rough';
  LIGHTOFF    = freshLightOff();   // fresh random low-fire start each reset
  currentPt   = { ...LIGHTOFF };
  isSaved     = false;
  saveTried   = false;
  // Seed lag states to light-off physics so gauges read correctly on relight
  seedLag(LIGHTOFF, 'rough');

  el('flameoutReason').innerHTML =
    'Cause: ' + reason + '<br/><br/>' +
    'Last readings before lockout:<br/>' +
    'CO &gt; 500 ppm &nbsp;|&nbsp; Flame unstable<br/>' +
    'Reset required — begin relight from low fire.';
  el('flameoutOverlay').style.display = 'flex';
}

function relight() {
  el('flameoutOverlay').style.display = 'none';
  seedLag(LIGHTOFF, 'rough');   // gauges read light-off values immediately
  render();
}

// ─── START ───────────────────────────────────────────────────────────────────
function startSim(pageHooks) {
  hooks = { ...hooks, ...pageHooks };

  el('btnBeginner').addEventListener('click',  () => setSkillMode('beginner'));
  el('btnAdvanced').addEventListener('click',  () => setSkillMode('advanced'));
  el('btnRoughMode').addEventListener('click', () => setTuneMode('rough'));
  el('btnFineMode').addEventListener('click',  () => setTuneMode('fine'));
  el('btnSave').addEventListener('click',   savePoint);
  el('btnNext').addEventListener('click',   nextPoint);
  el('btnBack').addEventListener('click',   backPoint);
  el('btnExport').addEventListener('click', exportL5X);
  el('btnRelight').addEventListener('click', relight);

  // Pre-seed analyzer lag to light-off physics so gauges read correctly on
  // page load instead of climbing from zero.
  seedLag(LIGHTOFF, 'rough');
  render();

  // Continuous analyzer tick: advances the lag filter (and the flame stability
  // countdown) every LAG_DT seconds whether or not the operator is touching
  // the controls. Paused while the flameout overlay is up.
  setInterval(() => {
    if (el('flameoutOverlay').style.display === 'flex') return;
    render();
  }, LAG_DT * 1000);
}
