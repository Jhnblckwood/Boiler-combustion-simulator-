# Conduit Bending Calculator

Single-file HTML app. Open `index.html` in any browser — no build step, no
dependencies, works offline and on a phone.

Pick a bend from the dropdown, enter what you measured, and it returns the
angle to use, the multiplier, shrink, every mark measured from the starting
end of the conduit, the center punch, the overall cut length, and a
step-by-step bending sequence with the bender symbol to line up on each mark.

## Bend types

| Type | You enter | You get |
|---|---|---|
| 90° stub-up | stub height, tail | mark 1, take-up, gain, cut length |
| Back-to-back 90° | stub, back-to-back, second stub | mark 1 (arrow), mark 2 (star/tee), cut length |
| Offset | rise, distance to obstacle, tail, space available | best angle, multiplier, marks 1–2, shrink, cut length |
| Box offset | depth, distance, tail | same as offset, defaults to 10° and 3/4" |
| 3-point saddle | obstacle rise, distance to center, clearance, tail | center + outer angles, marks 1–3, shrink, cut length |
| 4-point saddle | rise, obstacle length, distance to near edge, clearance, tail | best angle, marks 1–4, shrink, cut length |

Inputs accept decimals or fractions — `12`, `12.5`, `12 1/2`, `12-1/2`.

## Formulas

Let `R` = rise / saddle depth, `θ` = bend angle, `M` = multiplier,
`s` = shrink per inch of rise.

**90° stub** — `mark = stub − take-up`, `cut = stub + tail − gain`,
where `gain = 2r − πr/2` for centerline radius `r`.

**Back-to-back 90°** — `mark 1 = stub − take-up` (arrow),
`mark 2 = mark 1 + back-to-back` (star/tee, which puts the *back* of the
second bend on the mark), `cut = stub1 + B + stub2 − 2 × gain`.

**Offset** — `between marks = R × M`, `shrink = R × s`,
`horizontal run = R × (M − s)`. Mark 1 goes at the measured distance, mark 2
one multiplier-length later; the far end of the piece pulls back by the
shrink, which the cut length already carries.

**3-point saddle** — outer bends are half the center bend.
`center mark = distance to center of obstacle + R × s`,
`outer marks = center ± R × M`, total shrink `2 × R × s`.
The saddle multiplier is slightly under `1/sin(outer)` because the center
bend itself consumes conduit.

**4-point saddle** — two matching offsets.
`mark 2 = near edge + R × s` (this lands the crest exactly on the near edge),
`mark 1 = mark 2 − R × M`, `mark 3 = mark 2 + obstacle length`,
`mark 4 = mark 3 + R × M`, total shrink `2 × R × s`.

**Exact mode** replaces the chart values with `M = 1/sin θ` and
`s = tan(θ/2)` — the pure geometry, before any allowance for bend gain.

## Angle selection

With a space limit entered, it picks the *smallest* angle whose
`R × M` still fits, because gentler bends pull easier and stress the conduit
less; if nothing fits it shows 60° and flags it. With no limit it goes by
rise: under 1" → 10°, under 2" → 22.5°, up to 12" → 30°, above that → 45°.
Saddles go 22.5° / 30° / 45° / 60° center bend as the obstacle gets taller.
Every choice is explained in the result, and the angle dropdown overrides it.

## Charts used

All of these are editable in the app under **Charts & constants**, and every
change recalculates immediately.

Take-up and centerline radius (hand bender, EMT):

| Size | Take-up | Radius | Gain (computed) |
|---|---|---|---|
| 1/2" | 5" | 4" | 1-11/16" |
| 3/4" | 6" | 4-1/2" | 1-15/16" |
| 1" | 8" | 5-3/4" | 2-1/2" |
| 1-1/4" | 11" | 7-1/4" | 3-1/8" |
| 1-1/2" | 14" | 8-1/4" | 3-9/16" |
| 2" | 16" | 9-1/2" | 4-1/16" |

Offset multipliers and shrink: 10° → 6.0 / 1/16", 22.5° → 2.6 / 3/16",
30° → 2.0 / 1/4", 45° → 1.4 / 3/8", 60° → 1.2 / 1/2".

3-point saddle (center/outer → multiplier, shrink per side):
22.5°/11.25° → 5.0, 1/16" · 30°/15° → 3.7, 1/8" ·
45°/22.5° → 2.5, 3/16" · 60°/30° → 2.0, 1/4".

These are the common hand-bender/EMT chart values. Take-up and radius vary by
bender brand — overwrite them with the numbers stamped on yours. Check every
layout with a tape before bending.
