# Conduit Bending Calculator

Single-file HTML app. Open `index.html` in any browser — no build step, no
dependencies, works offline and on a phone.

Pick a bend from the dropdown, enter what you measured, and it returns the
angle to use, the multiplier, shrinkage, every mark measured from the starting
end of the conduit, the center punch, the overall cut length, and a
step-by-step bending sequence naming the bender symbol to line up on each mark.

Built from the Electrical Construction Course handout — 90° bends, offset
bends, box offset, 3-point saddle, and the 3-point saddle bend table. The
back-to-back 90 and the 4-point saddle are not in that handout; they follow
standard practice and the handout's own offset chart, and are marked as such
below.

## Bend types

| Type | You enter | You get |
|---|---|---|
| 90° stub-up | stub height, tail | mark #1, take-up, gain, cut length |
| Back-to-back 90° * | stub, back-to-back, second stub | mark #1 (arrow), mark #2 (star/tee), cut length |
| Offset | rise, distance to obstruction, tail, space available | best angle, multiplier, marks #1–#2, shrinkage, cut length |
| Box offset | rise, mark #1 distance, tail | marks #1–#2, cut length; defaults to 10° and 3/8" |
| 3-point saddle | obstruction rise, distance to center, clearance, tail | center + return angles, marks #A/#B/#C, shrinkage, cut length |
| 4-point saddle * | rise, obstruction length, distance to near edge, clearance, tail | best angle, marks #1–#4, shrinkage, cut length |

\* not from the handout — see "Beyond the handout" below.

Inputs accept decimals or fractions — `12`, `12.5`, `12 1/2`, `12-1/2`.

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

3-point saddle, per inch of obstruction height: 45° center (22.5° returns) →
2-1/2" off center mark, 3/16" shrink. 60° center (30° returns) → 2" off center
mark, 1/4" shrink.

**Take-up, radius and gain are not in the handout** — it lists them as bender
info to read off your own tool. The app ships the common hand-bender EMT
values and they are the first thing to check:

| Size | 1/2" | 3/4" | 1" | 1-1/4" | 1-1/2" | 2" |
|---|---|---|---|---|---|---|
| Take-up | 5" | 6" | 8" | 11" | 14" | 16" |
| Radius | 4" | 4-1/2" | 5-3/4" | 7-1/4" | 8-1/4" | 9-1/2" |
| Gain | 1-11/16" | 1-15/16" | 2-1/2" | 3-1/8" | 3-9/16" | 4-1/16" |

Gain is seeded from the radius as `2R − πR/2` and is directly editable — the
handout defines it as a measured quantity, `(stub + tail) − initial length`, so
overwrite it with what you measure off your bender.

## Beyond the handout

**Back-to-back 90°** — `mark #1 = stub − take-up` (arrow), then
`mark #2 = mark #1 + back-to-back` bent with the star/tee, which puts the
*back* of the second bend on the mark. `cut = stub1 + B + stub2 − 2 × gain`.

**4-point saddle** — two matching offsets off the handout's offset chart.
`mark #2 = near edge + shrinkage` lands the crest on the near edge,
`mark #1 = mark #2 − X`, `mark #3 = mark #2 + obstruction length`,
`mark #4 = mark #3 + X`. Total shrinkage is twice a single offset's.

**Exact mode** swaps every chart multiplier for `1/sin θ` and every shrinkage
for `tan(θ/2)` — the pure geometry, before the rounding the chart carries.
Useful for checking, not for matching the book.

Check every layout with a tape before you bend.
