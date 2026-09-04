# Boiler-combustion-simulator
A program to practice tuning combustion for a boiler. Wip 

Cleaver Brooks CBEX-2D 30ppm nox 
natural gas 1.43 btu/hr min 14.29 btu/hr max input. 
Gross output 11,716,000 btu/hr

**index.html will auto direct to phone/pc version**

## Files

| File | Purpose |
|---|---|
| `index.html` | Redirects to the phone or PC version |
| `desktop.html` | PC layout: range sliders, sticky header |
| `mobile.html` | Phone layout: ± stepper buttons, sticky live-readings strip |
| `sim.js` | Shared engine: physics, tuning state, validation, render loop, L5X export, flameout/relight |
| `sim.css` | Shared styles |

The two pages differ only in layout and control widgets. All behaviour lives in
`sim.js`, so a change there applies to both versions.
