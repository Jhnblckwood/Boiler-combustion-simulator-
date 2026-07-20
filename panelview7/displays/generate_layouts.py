#!/usr/bin/env python3
"""Generate FactoryTalk View ME display-XML starters for every PanelView Plus 7
size from one master 1280x800 layout.

Run from this folder:  python3 generate_layouts.py

Outputs ValveProving_<W>x<H>.xml per resolution. The XML is a STARTER in the
gfx shape (root <gfx>, objects carrying <connection Expression="{Tag}">) - see
../README.md: the build spec is authoritative, these files carry the scaled
coordinates and every animation/connection expression to copy into FT View
Studio if an object doesn't import as-is.
"""

RESOLUTIONS = {
    # (W, H): panels covered
    (800, 480):   'PanelView Plus 7 - 7in and 9in widescreen',
    (640, 480):   'PanelView Plus 7 - 6.5in and 10.4in',
    (1024, 768):  'PanelView Plus 7 - 15in',
    (1280, 800):  'PanelView Plus 7 - 12.1in widescreen (master layout)',
    (1280, 1024): 'PanelView Plus 7 - 19in',
}

MASTER_W, MASTER_H = 1280, 800
MIN_FONT = 8

def esc(s):
    """XML attribute escaping (expressions contain < > and quotes)."""
    return (str(s).replace('&', '&amp;').replace('<', '&lt;')
            .replace('>', '&gt;').replace('"', '&quot;'))

BANNER_STATES = [
    (0, 'STANDBY - READY TO START', '#212D3B'),
    (1, 'STEP 1: EVACUATE (OPEN V2)', '#1F4E79'),
    (2, 'STEP 2: TEST V1 - PRESSURE MUST STAY LOW', '#1F4E79'),
    (3, 'STEP 3: FILL (OPEN V1)', '#1F4E79'),
    (4, 'STEP 4: TEST V2 - PRESSURE MUST STAY HIGH', '#1F4E79'),
    (5, 'PROVEN - PREPURGE', '#1F4E79'),
    (6, 'PILOT TRIAL FOR IGNITION', '#1F4E79'),
    (7, 'BURNER FIRING - VALVES PROVEN', '#14532D'),
    (8, 'SAFETY LOCKOUT - PRESS STOP/RESET', '#7F1D1D'),
]

LOCKREASON_STATES = [
    (0, ''), (2, 'LOW GAS PRESSURE - LGP DROPPED OUT'),
    (3, 'HIGH GAS PRESSURE - HGP TRIPPED'),
    (4, 'START ATTEMPTED BEFORE LGP MADE'),
    (5, 'PILOT ENABLED OUTSIDE IGNITION TRIAL'),
    (6, 'INVALID CONTROL FOR CURRENT STEP'),
    (7, 'CONTROL CHANGED DURING RUN'),
    (8, 'REQUIRED ACTION NOT COMPLETED IN TIME'),
    (9, 'VALVES NOT CLOSED FOR HOLD TEST'),
    (10, 'V1 LEAK DETECTED'), (11, 'V2 / DOWNSTREAM LEAK DETECTED'),
    (12, 'PILOT FAILED TO LIGHT'), (13, 'LIGHT-OFF NOT COMPLETED IN TIME'),
]

STEP_LABELS = [
    '1 - EVACUATE: OPEN V2', '2 - TEST V1: ALL CLOSED, STAY LOW',
    '3 - FILL: OPEN V1', '4 - TEST V2: ALL CLOSED, STAY HIGH',
    '5 - PROVEN: PURGE + PILOT TRIAL', '6 - RUN: V1 + V2 OPEN',
]

def step_led_expr(i):
    """0 off / 1 active (yellow) / 2 done (green) from {State} for checklist row i."""
    if i <= 4:
        return f'If {{State}} = {i} Then 1 Else If ({{State}} > {i}) And ({{State}} < 8) Then 2 Else 0'
    if i == 5:
        return 'If ({State} = 5) Or ({State} = 6) Then 1 Else If {State} = 7 Then 2 Else 0'
    return 'If {State} = 7 Then 2 Else 0'

# type, name, x, y, w, h, extras
# extras: cap caption | fs font | fc forecolor | bc backcolor (static fill)
#         conn value-connection tag | expr+states color animation | vis visibility
#         minmax numeric limits | states multistate table
O = []
def add(typ, name, x, y, w, h, **kw):
    O.append(dict(typ=typ, name=name, x=x, y=y, w=w, h=h, **kw))

add('text', 'Title', 40, 14, 600, 34, cap='VALVE PROVING SYSTEM', fs=24, fc='#FFFFFF')
add('text', 'Subtitle', 40, 52, 1000, 18, fs=12, fc='#8FA3B8',
    cap='HONEYWELL 7800 SERIES - SIEMENS SKP - TAGS VP1 VP2 VPS LGP HGP')
add('multistateindicator', 'Banner', 40, 80, 1200, 42, conn='{State}',
    states=BANNER_STATES, fs=15)
# gauges (min 0 max 80); inlet is the operator-adjustable one
add('text', 'InletLabel', 40, 128, 140, 14, cap='INLET (IN H2O)', fs=11, fc='#C8D3DE')
add('gauge', 'InletGauge', 40, 150, 110, 110, conn='{SupplyPressure}')
add('numericdisplay', 'InletValue', 72, 235, 46, 18, conn='{SupplyPressure}', fs=12, fc='#FFFFFF')
add('text', 'LGPGaugeLabel', 190, 128, 140, 14, cap='LGP (IN H2O)', fs=11, fc='#C8D3DE')
add('gauge', 'LGPGauge', 190, 150, 110, 110, conn='{SupplyPressure}')
add('gauge', 'PressGauge', 560, 132, 140, 140, conn='{ChamberPressure}')
add('numericdisplay', 'PressValue', 610, 244, 60, 18, conn='{ChamberPressure}', fs=12, fc='#FFFFFF')
add('text', 'HGPGaugeLabel', 1000, 128, 160, 14, cap='HGP (IN H2O)', fs=11, fc='#C8D3DE')
add('gauge', 'HGPGauge', 1000, 150, 110, 110, conn='{DownstreamPressure}')
# gas train
GAS_OFF, GAS_ON = '#3B4654', '#FFC400'
add('rectangle', 'PipeSupply', 40, 292, 300, 18,
    expr='{SupplyPressure} >= 1', states_color=[(0, GAS_OFF), (1, GAS_ON)])
add('rectangle', 'Actuator1', 355, 166, 70, 95, bc='#00707E')
add('text', 'Actuator1Label', 368, 206, 50, 18, cap='SKP15', fs=12, fc='#FFFFFF')
add('rectangle', 'Valve1', 340, 271, 100, 60,
    expr='{VP1}', states_color=[(0, '#E74C3C'), (1, '#2ECC71')])
add('rectangle', 'PipeChamber', 440, 292, 380, 18,
    expr='{VP1} Or ({ChamberPressure} > 8)', states_color=[(0, GAS_OFF), (1, GAS_ON)])
add('rectangle', 'Actuator2', 835, 158, 70, 103, bc='#00707E')
add('text', 'Actuator2Label', 848, 200, 50, 18, cap='SKP25', fs=12, fc='#FFFFFF')
add('rectangle', 'Valve2', 820, 271, 100, 60,
    expr='{VP2}', states_color=[(0, '#E74C3C'), (1, '#2ECC71')])
add('rectangle', 'PipeDownstream', 920, 292, 220, 18,
    expr='{VP2} And ({VP1} Or ({ChamberPressure} > 8))',
    states_color=[(0, GAS_OFF), (1, GAS_ON)])
add('rectangle', 'BurnerBlock', 1140, 270, 84, 62, bc='#3B4654')
add('text', 'BurnerLabel', 1150, 340, 70, 16, cap='BURNER', fs=12, fc='#C8D3DE')
add('ellipse', 'FlameMain', 1157, 190, 50, 78, bc='#FF8C00', vis='{Flame_Main}')
add('ellipse', 'FlamePilot', 1170, 230, 24, 38, bc='#FFB000', vis='{Flame_Pilot}')
# switches + setpoint inputs
add('rectangle', 'VpsBody', 700, 225, 56, 36, bc='#333D49')
add('ellipse', 'VpsLed', 708, 231, 24, 24,
    expr='{VPS}', states_color=[(0, '#2ECC71'), (1, '#E74C3C')])
add('text', 'VpsLabel', 762, 233, 40, 18, cap='VPS', fs=12, fc='#C8D3DE')
add('numericinput', 'VpsSet', 700, 196, 60, 24, conn='{VPSSetpoint}', minmax=(0, 80))
add('numericinput', 'LgpSet', 140, 336, 60, 24, conn='{LGPSetpoint}', minmax=(0, 80))
add('rectangle', 'LgpBody', 210, 330, 56, 36, bc='#333D49')
add('ellipse', 'LgpLed', 218, 336, 24, 24,
    expr='{LGP}', states_color=[(0, GAS_OFF), (1, '#2ECC71')])
add('text', 'LgpLabel', 272, 340, 34, 18, cap='LGP', fs=12, fc='#C8D3DE')
add('numericinput', 'HgpSet', 966, 336, 60, 24, conn='{HGPSetpoint}', minmax=(0, 80))
add('rectangle', 'HgpBody', 1030, 330, 56, 36, bc='#333D49')
add('ellipse', 'HgpLed', 1038, 336, 24, 24,
    expr='{HGP}', states_color=[(0, GAS_OFF), (1, '#E74C3C')])
add('text', 'HgpLabel', 1092, 340, 34, 18, cap='HGP', fs=12, fc='#C8D3DE')
add('text', 'V1Label', 330, 360, 150, 16, cap='SSOV V1 - VP1', fs=12, fc='#C8D3DE')
add('text', 'V2Label', 790, 360, 170, 16, cap='SSOV V2+REG - VP2', fs=12, fc='#C8D3DE')
# step checklist
for i in range(1, 7):
    y = 384 + (i - 1) * 34
    add('ellipse', f'StepLed{i}', 48, y, 20, 20, expr=step_led_expr(i),
        states_color=[(0, GAS_OFF), (1, '#FFC400'), (2, '#2ECC71')])
    add('text', f'StepLabel{i}', 82, y + 2, 640, 18, cap=STEP_LABELS[i - 1],
        fs=13, fc='#8FA3B8')
# tag LED row
add('ellipse', 'LedVP1', 40, 656, 22, 22, expr='{VP1}', states_color=[(0, GAS_OFF), (1, '#2ECC71')])
add('text', 'TagLabel1', 72, 660, 130, 16, cap='VP1 - V1 OPEN', fs=12, fc='#C8D3DE')
add('ellipse', 'LedVP2', 210, 656, 22, 22, expr='{VP2}', states_color=[(0, GAS_OFF), (1, '#2ECC71')])
add('text', 'TagLabel2', 242, 660, 130, 16, cap='VP2 - V2 OPEN', fs=12, fc='#C8D3DE')
add('ellipse', 'LedVPS', 380, 656, 22, 22, expr='{VPS}', states_color=[(0, GAS_OFF), (1, '#E74C3C')])
add('text', 'TagLabel3', 412, 660, 160, 16, cap='VPS - TEST FAIL', fs=12, fc='#C8D3DE')
# control panel (momentary buttons -> Cmd_ bits, PLC one-shots them)
add('rectangle', 'PanelRect', 900, 380, 340, 390, bc='#212D3B')
add('text', 'PanelTitle', 920, 392, 200, 18, cap='CONTROLS', fs=14, fc='#FFFFFF')
add('momentarybutton', 'ModeBtn', 920, 420, 300, 38, cap='TOGGLE AUTO / MANUAL', conn='{Cmd_ToggleMode}', fs=12)
add('momentarybutton', 'StartBtn', 920, 468, 145, 44, cap='START BURNER', conn='{Cmd_Start}', fs=12)
add('momentarybutton', 'StopBtn', 1075, 468, 145, 44, cap='STOP / RESET', conn='{Cmd_Stop}', fs=12)
add('text', 'ManualTitle', 920, 522, 300, 14, cap='MANUAL CONTROL (MANUAL MODE ONLY)', fs=10, fc='#6E7F91')
add('momentarybutton', 'VP1Btn', 920, 540, 145, 36, cap='VP1', conn='{Cmd_VP1}', fs=12)
add('momentarybutton', 'VP2Btn', 1075, 540, 145, 36, cap='VP2', conn='{Cmd_VP2}', fs=12)
add('momentarybutton', 'PilotBtn', 920, 582, 300, 36, cap='PILOT', conn='{Cmd_Pilot}', fs=12)
add('text', 'SimTitle', 920, 628, 300, 14, cap='SIMULATION', fs=10, fc='#6E7F91')
add('momentarybutton', 'Leak1Btn', 920, 646, 145, 36, cap='SIM V1 LEAK', conn='{Cmd_LeakV1}', fs=11)
add('momentarybutton', 'Leak2Btn', 1075, 646, 145, 36, cap='SIM V2 LEAK', conn='{Cmd_LeakV2}', fs=11)
add('momentarybutton', 'PilotFailBtn', 920, 690, 300, 32, cap='SIM PILOT FAIL', conn='{Cmd_PilotFail}', fs=11)
add('momentarybutton', 'InletDnBtn', 920, 730, 145, 32, cap='INLET PRESSURE -', conn='{Cmd_InletDown}', fs=11)
add('momentarybutton', 'InletUpBtn', 1075, 730, 145, 32, cap='INLET PRESSURE +', conn='{Cmd_InletUp}', fs=11)
# lockout reason readout
add('multistateindicator', 'LockReasonText', 40, 764, 840, 28, conn='{LockReason}',
    states=[(v, cap, '#151E27') for v, cap in LOCKREASON_STATES], fs=12)


def emit(W, H, panels):
    sx, sy = W / MASTER_W, H / MASTER_H
    fscale = min(sx, sy)
    out = []
    out.append('<?xml version="1.0" encoding="utf-8"?>')
    out.append(f'<!-- ValveProving display STARTER for {panels} ({W}x{H}).')
    out.append('     Generated by generate_layouts.py from the 1280x800 master.')
    out.append('     See ../README.md: the build spec is authoritative; this file')
    out.append('     carries the scaled coordinates and every connection/animation')
    out.append('     expression to copy into FT View Studio ME. -->')
    out.append(f'<gfx name="ValveProving" width="{W}" height="{H}" backcolor="#151E27">')
    for o in O:
        x, y = round(o['x'] * sx), round(o['y'] * sy)
        w, h = max(2, round(o['w'] * sx)), max(2, round(o['h'] * sy))
        fs = max(MIN_FONT, round(o.get('fs', 12) * fscale)) if 'fs' in o else None
        a = [f'name="{esc(o["name"])}"', f'left="{x}"', f'top="{y}"',
             f'width="{w}"', f'height="{h}"']
        if fs: a.append(f'fontsize="{fs}"')
        if 'fc' in o: a.append(f'forecolor="{o["fc"]}"')
        if 'bc' in o and 'vis' not in o: a.append(f'backcolor="{o["bc"]}"')
        if 'bc' in o and 'vis' in o: a.append(f'backcolor="{o["bc"]}"')
        if 'cap' in o and o['typ'] != 'momentarybutton': a.append(f'caption="{esc(o["cap"])}"')
        if o['typ'] == 'momentarybutton':
            a.append(f'caption="{esc(o.get("cap",""))}"'); a.append('action="set"')
        if o['typ'] == 'numericinput' and 'minmax' in o:
            a.append(f'min="{o["minmax"][0]}"'); a.append(f'max="{o["minmax"][1]}"')
        if o['typ'] == 'gauge':
            a.append('min="0"'); a.append('max="80"')
        body = []
        if 'conn' in o:
            body.append(f'  <connection name="Value" Expression="{esc(o["conn"])}"/>')
        if 'states' in o:
            for s in o['states']:
                v, cap, bc = s
                body.append(f'  <state value="{v}" caption="{esc(cap)}" backcolor="{bc}"/>')
        anims = []
        if 'expr' in o:
            lines = [f'    <color type="fill">',
                     f'      <connection name="Expression" Expression="{esc(o["expr"])}"/>']
            for v, c in o['states_color']:
                lines.append(f'      <state value="{v}" color="{c}"/>')
            lines.append('    </color>')
            anims += lines
        if 'vis' in o:
            anims += ['    <visibility>',
                      f'      <connection name="Expression" Expression="{esc(o["vis"])}"/>',
                      '    </visibility>']
        if anims:
            body.append('  <animations>'); body += anims; body.append('  </animations>')
        if body:
            out.append(f'  <{o["typ"]} {" ".join(a)}>')
            out += body
            out.append(f'  </{o["typ"]}>')
        else:
            out.append(f'  <{o["typ"]} {" ".join(a)}/>')
    out.append('</gfx>')
    fn = f'ValveProving_{W}x{H}.xml'
    open(fn, 'w').write('\n'.join(out) + '\n')
    return fn, len(O)


if __name__ == '__main__':
    for (W, H), panels in RESOLUTIONS.items():
        fn, n = emit(W, H, panels)
        print(f'{fn}: {n} objects for {panels}')
