# Troubleshooting

## Studio 5000 / Logix Designer crash + I/O import error moving the boiler PLC project from a Series A to a Series B controller

### Hardware / project facts

- **Controller:** `1769-L24ER-QBFC1B` — CompactLogix 5370 **L2** with **two embedded I/O
  modules**: one digital (16 DI / 16 DO) and one combined **analog + high-speed-counter**
  module (4 universal AI, 2 AO, 4 HSC). These embedded modules are fixed profiles built into
  the CPU.
- **Original project:** saved with **RSLogix 5000 / Studio 5000 V20.05.00** (internal
  `RxController` version 68), built for a **Series A** controller.
- **New hardware:** **Series B** `1769-L24ER-QBFC1B`.
- **Software in use:** Studio 5000 Logix Designer **V34.01.00**.

### Symptoms

1. Opening the V20 `.ACD` in V34 crashes ~1 second in with:
   `Error 0x8004203b — RxE_INVALID_INTERNAL_STATE — Invalid software state due to
   inconsistency found.`
2. Importing the V20 `.L5X` into V34 fails with:
   `The data from Local:3:I doesn't match the name of the member as defined in the container.`

### Root cause (confirmed)

This is **not** a OneDrive/cloud-sync problem (it reproduces from a fully local copy). It is the
**Series A → Series B hardware change forcing a firmware and module-definition jump**:

- **Series A** runs firmware ~**V20.019 through V36**.
- **Series B requires firmware revision 30 or greater** (early units shipped labeled "V34+";
  Rockwell later back-published selected revisions to V30). A Series B controller **cannot run
  the V20 firmware the project targets**, so the project must be moved up to at least V30 — V34
  is the practical target since it is installed.

`Local:3:I` is the **Input** connection tag of an I/O module in the local tree (Local:1 and
Local:2 are the two embedded modules; Local:3 is the next local node). Between the V20/Series-A
profile and the V30+/V34/Series-B profile, that module's **module definition changed** — the
member layout of its auto-generated `:I` tag is not identical. On import, V34 creates the module
with the **new** definition and then cannot map the **old** V20 tag data member-for-member →
the "data doesn't match the name of the member as defined in the container" error. The
whole-`.ACD` conversion crash (`RxE_INVALID_INTERNAL_STATE`) is the same root cause hit during
in-place migration of the embedded/local I/O definitions.

### Fix (do not carry the old I/O module tags across)

Let V34/Series-B **generate** the module definitions, then bring the logic in on top. In order
of preference:

1. **Stage the conversion — don't jump V20→V34 in one step.** Open the V20 `.ACD` in an
   intermediate version (e.g. V24 or V28), *Save As*, then open that in V34. Each hop migrates
   module definitions incrementally and usually survives where the direct jump crashes.
   (Requires the intermediate Logix Designer versions installed.)

2. **Rebuild the shell, import only the logic.** Create a **fresh V34 project** targeting the
   `1769-L24ER-QBFC1B` (V34 creates the correct Series-B embedded I/O automatically). Add any
   1769 expansion modules so their definitions are V34-native. Then import from the V20 `.L5X`
   **only** routines, UDTs, AOIs, and program/controller tags — **exclude the module-generated
   `Local:x:I` / `Local:x:O` tags** (they regenerate from the modules). Reconcile alias tags
   that referenced old I/O members.

3. **Import the L5X piece by piece** (per routine / per program) rather than the whole
   controller at once, so the conflicting module tag containers are not dragged along.

After it opens clean: in **Controller Properties**, confirm the target is the
`1769-L24ER-QBFC1B` at firmware **V34** (or ≥ V30), then download to the Series B controller.
Also verify the project's major revision was not accidentally left at 20 — Series B refuses V20
firmware and that alone can look like a conversion failure.

### References

- Rockwell 1769-L24ER-QBFC1B product page:
  https://www.rockwellautomation.com/en-us/products/details.1769-L24ER-QBFC1B.html
- CompactLogix 1769-QBFC1B Series A/B firmware issue (PLCtalk):
  https://www.plctalk.net/forums/threads/compactlogix-1769-qbfc1b-series-a-b-firmware-issue.142393/
- CompactLogix 1769-L24ER Series B Unsupported Firmware (PLCtalk):
  https://www.plctalk.net/forums/threads/compactlogix-1769-l24er-series-b-unsupported-firmware.139729/
- Studio 5000 "Error: .L5X file load failed" (Rockwell docs):
  https://www.rockwellautomation.com/en-gb/docs/studio-5000-logix-designer/37-00/contents-ditamap/studio-5000-logix-designer/import-and-export/error----l5x-file-load-failed.html
- CompactLogix 5370 L2 Technical Data (1769-TD005):
  https://assetcloud.roccommerce.net/files/_stateelectric/10/6/8/a-b1769l24erqbfc1b.pdf

### Note on the earlier OneDrive theory

The first version of this guide led with a OneDrive/cloud-sync hypothesis. That was ruled out:
the crash reproduces from a fully local copy ("Always keep on this device"). Keeping working
`.ACD` files on a local, non-synced path is still good practice, but it is not the cause here.
