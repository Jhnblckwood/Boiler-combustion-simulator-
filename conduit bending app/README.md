# Conduit Bending Calculator

Single-file HTML app. Open `index.html` in any browser — no build step, no
dependencies, works offline and on a phone.

Pick a bend from the dropdown, enter what you measured, and it returns the
angle to use, the multiplier, shrinkage, every mark measured from the starting
end of the conduit, the center punch, the overall cut length, and a
step-by-step bending sequence naming the bender symbol to line up on each mark.

Built from the Electrical Construction Course handout — 90° bends, offset
bends, box offset, 3-point saddle and its bend table, and 4-point saddles. The
back-to-back 90 is not in that handout; it follows standard practice and is
marked as such below.

## Bend types

| Type | You enter | You get |
|---|---|---|
| 90° stub-up | stub height, tail | mark #1, take-up, gain, cut length |
| Back-to-back 90° * | stub, back-to-back, second stub | mark #1 (arrow), mark #2 (star/tee), cut length |
| Offset | rise, distance to obstruction, tail, space available | best angle, multiplier, marks #1–#2, shrinkage, cut length |
| Box offset | rise, mark #1 distance, tail | marks #1–#2, cut length; defaults to 10° and 3/8" |
| 3-point saddle | obstruction rise, distance to center, clearance, tail | center + return angles, marks #A/#B/#C, shrinkage, cut length |
| 4-point saddle | rise, length of obstruction, distance to obstruction, clearance, tail | best angle, marks #A–#D, shrinkage, cut length |

\* not from the handout — see "Beyond the handout" below.

All measurements are in inches. Inputs accept decimals or fractions — `12`,
`12.5`, `12 1/2`, `12-1/2`.

## Bend order

Bends that come in pairs go **closest to the obstruction first**, then rotate
the conduit 180° for its partner. The marks table numbers them 1st / 2nd / 3rd
/ 4th so the order is on screen with the measurements.

| Bend | Order |
|---|---|
| Offset | #1 (at the obstruction) → rotate 180° → #2 |
| Box offset | #1 (nearest the box) → rotate 180° → #2 |
| 3-point saddle | #A center notch → rotate 180° → #B, #C |
| 4-point saddle | #A → rotate 180° → #B → **swing the conduit end for end** → #C → rotate 180° → #D |

On a 4-point saddle, check the first pair before pulling the third bend — it is
easy to put the second pair in the wrong way and end up with a Z instead of a
saddle. All bends in a set must finish in one plane.

## Formulas from the handout

**90° bends**

    MARK #1    = STUB HEIGHT − STUB TAKE-UP
    CUT LENGTH = (STUB HEIGHT + TAIL) − GAIN
    GAIN       = (STUB HEIGHT + TAIL) − INITIAL LENGTH

Align your marks with the arrow to bend.

**Offset bends**

    MARK #1 = DIST + SHRINKAGE          (the upper bend, at the obstruction)
    X       = MULTIPLIER × RISE
    MARK #2 = MARK #1 − X               (the lower bend, back toward the end)

`DIST` is from the end of the conduit to the obstruction. Adding the shrinkage
to mark #1 is what lands the finished conduit at the obstruction rather than
short of it. Align your marks with the arrow.

**Box offset** — two 10° bends. Mark #1 goes a short distance in from the box
end (2" in the handout example), mark #2 sits `MULTIPLIER × RISE` further back.
With the handout's 3/8" rise that is 6.0 × 3/8" = 2-1/4" between marks.
Shrinkage at 10° is under 1/16" here, which is why the handout ignores it.

**3-point saddle** (for round obstructions)

    MARK #A = DIST + SHRINKAGE                        (center mark)
    MARK #B = MARK #C = MARK #A ± (RISE × off-center) (return bends)

    45° center bend:  shrinkage = rise × 3/16",  off center = rise × 2-1/2"
    60° center bend:  shrinkage = rise × 1/4",   off center = rise × 2"

Return bend angle is half the center bend. Use the **center notch** for the
center bend and the **arrow** for the return bends.

**4-point saddle**

    SHRINKAGE        = RISE × TABLE VALUE
    MARK #A          = DIST + SHRINKAGE
    MARK #A → MARK #C = LENGTH OF OBSTRUCTION
    MARK #A → MARK #B = RISE × MULTIPLIER
    MARK #C → MARK #D = RISE × MULTIPLIER

Marks run #B, #A, #C, #D along the pipe from the reference end. Align all four
with the bender arrow, hook facing the obstruction. Mark #A lands on the near
edge of the obstruction and mark #C on the far edge; the run loses twice the
shrinkage overall, which the cut length carries.

## Charts used

All editable in the app under **Charts & constants**; every change
recalculates immediately.

Best angle by obstruction rise: 1"–2" → 10°, 2"–3" → 22.5°, 3"–6" → 30°,
6"+ (tight space) → 45°. If you enter the space available and the chart angle
won't fit it, the app steps up to the next angle that does and says so.

Offset multipliers and shrinkage per inch of rise:

| Angle | 10° | 15° | 22.5° | 30° | 45° | 60° |
|---|---|---|---|---|---|---|
| Multiplier | 6.0 | 3.9 | 2.6 | 2.0 | 1.4 | 1.2 |
| Shrink/in | 1/16" | 1/8" | 3/16" | 1/4" | 3/8" | 1/2" |

The 4-point saddle page carries the same multiplier and shrinkage table.

3-point saddle, per inch of obstruction height: 45° center (22.5° returns) →
2-1/2" off center mark, 3/16" shrink. 60° center (30° returns) → 2" off center
mark, 1/4" shrink.

**Take-up, radius and gain are not in the handout** — it lists them as bender
info to read off your own tool. Take-up is the standard field value and OD is
the EMT spec; radius and gain are derived from those two so the set stays
self-consistent:

    radius = take-up − OD/2
    gain   = OD + 2R − πR/2

| Size | 1/2" | 3/4" | 1" | 1-1/4" | 1-1/2" | 2" |
|---|---|---|---|---|---|---|
| OD | 0.706" | 0.922" | 1.163" | 1.510" | 1.740" | 2.197" |
| Take-up | 5" | 6" | 8" | 11" | 14" | 16" |
| Radius | 4.65" | 5.54" | 7.42" | 10.24" | 13.13" | 14.90" |
| Gain | 2-11/16" | 3-5/16" | 4-3/8" | 5-15/16" | 7-3/8" | 8-9/16" |

`2R − πR/2` is the square corner minus the quadrant arc that replaces it. The
extra OD is there because stub height and tail are both measured to the **back**
of the bend, so the corner they imply sits one full diameter outside the
centerline corner.

**Gain is the one value to verify on your own bender.** Published figures for
it vary between sources, and it changes with the bender's radius. Measure it
the way the handout defines it — bend a 90, then
`gain = (stub + tail) − the length you started with` — and type it into the
charts panel. Editing OD or take-up reseeds radius and gain; editing gain
overrides it directly.

## Beyond the handout

**Back-to-back 90°** — `mark #1 = stub − take-up` (arrow), then
`mark #2 = mark #1 + back-to-back` bent with the star/tee, which puts the
*back* of the second bend on the mark. `cut = stub1 + B + stub2 − 2 × gain`.

**Clearance** — an optional extra gap added around the obstruction on either
saddle, not in the handout. Leave it at 0 to work the handout's numbers
exactly.

**Exact mode** swaps every chart multiplier for `1/sin θ` and every shrinkage
for `tan(θ/2)` — the pure geometry, before the rounding the chart carries.
Useful for checking, not for matching the book.

## Checked against outside sources

Spot-checked against published worked examples and trade references:

- 4" offset at 30° → 8" between marks, 1" shrink ✓
- 11" stub in 1/2" EMT → mark at 6"; take-up 5" / 6" / 8" for 1/2" / 3/4" / 1" ✓
- Multipliers as `1/sin θ`: 22° → 2.6, 15° → 3.86 ✓
- 4" three-point saddle at 45°/22.5°: `4/sin(22.5) − 4/tan(22.5)` = 0.80"
  shrinkage per side, against the chart's 3/16" × 4" = 3/4" ✓
- Three-point saddle 2.5 multiplier and 3/16" per inch shrink ✓

Gain was the one value the sources disagreed on — see above.

Check every layout with a tape before you bend.
