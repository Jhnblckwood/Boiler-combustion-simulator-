# Valve Proving with Honeywell HFLF Switch Supervision — FactoryTalk Optix Project

A standalone Honeywell 7800 SERIES valve proving system with HFLF switch supervision.
Open `ValveProvingHFLF.optix` in FactoryTalk Optix Studio. All pressures are in **inches of water column (in. H2O)**.

## HFLF Supervision

This variant is configured as a template for **Honeywell HFLF** switch supervision.
The 6-step proving sequence (Evacuate → Test V1 → Fill → Test V2 → Proven/Purge → Ignition → Run)
runs identically to the base project, with AUTO and MANUAL drill modes.

The UI contains:
- Inlet pressure gauge (adjustable, 0–80 in. H2O) and numeric setpoint input for `SupplyPressure`.
- HFLF gauge and switch placeholders (ready for integration with Honeywell HFLF supervision logic).
- Test-volume gauge, piping with gas animation, valves, switch LEDs, step checklist, and banner.

To customize for your HFLF switches:
1. Edit `Nodes/UI/UI.yaml` — add or modify HFLF gauge and switch widgets per your supervision spec.
2. Edit `ProjectFiles/NetSolution/ValveProvingLogic.cs` — implement HFLF supervision rules in the `Start()` method
   and ensure all widget names match the YAML definitions.
3. Update `Model.yaml` with any additional HFLF-specific tags (e.g., HFLF_Made, HFLF_Tripped, etc.)

## Tags for I/O (Model folder)

Core tags (identical to base project):

| Tag | Type | Meaning |
|-----|------|---------|
| `VP1` / `VP2` | Boolean | Safety shutoff valves open |
| `VPS` | Boolean | Valve proving switch input — TRUE = test failed |
| `Pilot` | Boolean | Pilot valve/flame (legal only during the ignition trial) |
| `Lockout` | Boolean | Output — high on any safety lockout |
| `SupplyPressure` | Float | Inlet gas pressure, in. H2O |
| `VPSSetpoint` | Float | VPS allowed differential per hold test (0–80 in. H2O, default 14) |

HFLF-specific tags (add as needed for your supervision logic):

| Tag | Type | Notes |
|-----|------|-------|
| Add tags for your HFLF switches | Boolean | Created in Model.yaml per your supervision spec |
| Add tags for HFLF setpoints | Float | Configure in numeric inputs on the UI |

## Building the project

1. **Customize the UI**: Edit `Nodes/UI/UI.yaml` to add HFLF gauge and switch widgets matching your Honeywell
   HFLF supervision spec (position, colors, connections to Model tags).
2. **Add Model tags**: Edit `Nodes/Model/Model.yaml` to declare all HFLF supervision tags.
3. **Implement supervision logic**: Edit `ProjectFiles/NetSolution/ValveProvingLogic.cs` to implement
   HFLF supervision checks in the `Start()` method. Ensure all widget names in C# match the YAML definitions.
4. **Test**: Open in FactoryTalk Optix Studio, press Play/emulator to run the sequence.

For a reference implementation, see the **gas-pressure variant** (`../valve proving with low and high gas
pressure switch/`) which adds LGP and HGP supervision to the base project.
