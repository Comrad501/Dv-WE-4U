// EU07 for Derail Valley - the shell: one spawned locomotive's power circuit, bound to the
// ports of its Custom Car Loader prefab.
//
// CRUDE, on purpose. The prefab is Knsgf's example railcar with new numbers, and one of two
// desks (unity/Assets/Editor/eu07_build.cs, -eu07desk). The driver finds out which from the
// ports the prefab has:
//
//   v1  Knsgf's desk with clones. The throttle lever IS the controller wheel, cut into 44
//       positions; four switches right of the pantograph switches: fast breaker (read for
//       its level), relay reset (flip it either way), line contactors off (each flip is one
//       press of the button), high start; and a field lever on the dynamic brake's keys
//   v2  docs/desk/design_desk.md: a 44-notch controller wheel, push-buttons for WS on and off, OPN,
//       WJ1, PLW and PLP, switches for the converter, compressors and train heating, the
//       real signal lamps. The buttons are taken from their ports' events, not polled
//
//   - and always the numeric keypad, for those who have one, and only for the locomotive
//     you are standing in:
//
//         7   fast breaker ON             1   fast breaker OFF
//         5   reset relays (OPN and WJ1)  0   hold: line contactors off (PLW)
//         +   field weakening, a step     -   field weakening, a step back
//         9   high start on / off         3   this panel on / off
//
//   - without switches of their own (v1) the converter and both compressors are switched on
//     for you
//   - a panel in the corner of the screen stands in for the meters and the lamps
//
// What it reads and writes by port id is the contract with the prefab: see the constants.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

using UnityEngine;

using LocoSim.Definitions;
using LocoSim.Implementations;

namespace electric_sim.unit_A;

internal class eu07_driver: MonoBehaviour
{
    // ---- the prefab's side of the contract --------------------------------------------------
    public const string port_throttle      = "throttle.EXT_IN";              // 0-1
    public const string port_reverser      = "reverser.REVERSER";            // -1, 0, 1
    public const string port_wheel_RPM     = "traction.WHEEL_RPM_EXT_IN";
    public const string port_supply_volts  = "busBar.SUPPLY_VOLTAGE";        // Knsgf's bus bar
    public const string port_torque        = "eu07.TORQUE_OUT";              // wired to traction.TORQUE_IN
    // running resistance beyond the game's own rolling resistance: 12 N a km/h and 0.42 N a (km/h)2,
    // which is 5.4 kN at 100 km/h and 7.5 kN at 120
    public const float drag_newtons_per_kmh = 12.0f, drag_newtons_per_kmh2 = 0.42f, wheel_radius_metres = 0.625f;
    public float running_drag_newtons { get; private set; }
    public const string port_line_current  = "eu07.LINE_CURRENT";            // read by busBar.CURRENT_DRAW
    public const string port_ammeter_12    = "eu07.AMMETER_12";
    public const string port_ammeter_34    = "eu07.AMMETER_34";
    public const string port_position      = "eu07.CONTROLLER_POSITION";     // 0-43, where the contactors are
    // The speed recorder's clock and distance counter (v2 prefab only): its two hands, the six
    // drums of its counter, and the kilometres run, which the game keeps over a save.
    public const string port_clock_hours   = "eu07.CLOCK_HOURS";             // 0-12
    public const string port_clock_minutes = "eu07.CLOCK_MINUTES";           // 0-60
    public const string port_odometer      = "eu07.ODOMETER_";               // + 0..5, each 0-10: what its drum shows
    public const string control_odometer   = "odometer.EXT_IN";              // km
    public const string port_field         = "eu07.FIELD_POSITION";          // 0-6
    public const string port_fan_speed     = "eu07.RESISTOR_FAN_SPEED";
    public const string port_converter     = "eu07.CONVERTER_SPEED";
    public const string port_resistor_heat = "eu07.RESISTOR_HEAT";           // W
    public const string port_motor_heat    = "eu07.MOTOR_HEAT";              // W
    // the EU07's own handles on the desk, each a control in the simulation; where the prefab
    // has none the keypad still works. Both desks:
    public const string port_relay_reset   = "relayReset.EXT_IN";          // v1 switch: either way resets OPN and WJ1; v2 button OPN
    public const string port_line_off      = "lineContactorsOff.EXT_IN";   // v1 switch: every flip is one press of PLW; v2 button, held
    public const string port_high_start    = "highStart.EXT_IN";           // switch
    public const string port_field_lever   = "fieldLever.EXT_IN";          // 0-1 over 7 notches
    // v1 only
    public const string port_fast_breaker  = "fastBreaker.EXT_IN";         // switch: up closes, down opens
    // v2 only. Buttons are 1 while held (hold mode); presses are their rising edges
    public const string port_breaker_on    = "fastBreakerOn.EXT_IN";       // button WJ7,8
    public const string port_breaker_off   = "fastBreakerOff.EXT_IN";      // button WJ5,6
    public const string port_aux_reset     = "auxRelayReset.EXT_IN";       // button WJ1
    public const string port_slip_button   = "wheelSlip.EXT_IN";           // button PLP, held
    public const string port_converter_switch  = "converterSwitch.EXT_IN";  // switch WDG
    public const string port_compressor_switch = "compressorSwitch.EXT_IN"; // switch WDC1,2
    public const string port_heating_switch    = "heatingSwitch.EXT_IN";    // switch WDO3,4
    // lamps, 0 or 1, named as on the DTR's signal panel
    public const string lamp_fast_breaker  = "eu07.LAMP_LSS";
    public const string lamp_line_open     = "eu07.LAMP_LS10";
    public const string lamp_resistors     = "eu07.LAMP_LS5";
    public const string lamp_high_start    = "eu07.LAMP_LS4";
    public const string lamp_overcurrent   = "eu07.LAMP_LS8";
    public const string lamp_earth_fault   = "eu07.LAMP_LS7";
    public const string lamp_slip          = "eu07.LAMP_LS9";
    public const string lamp_fans          = "eu07.LAMP_LS6";
    public const string lamp_auxiliary     = "eu07.LAMP_LSPO";
    public const string lamp_compressor    = "eu07.LAMP_LSC";
    public const string lamp_heating       = "eu07.LAMP_LSGP";               // v2 prefab only
    // The last-mile battery (eu07_power_circuit.cs; v2 prefab only): its switch, its lamp (lit
    // while the traction circuit is on it), its meter, and what is left in it, which the game
    // keeps over a save as it keeps the kilometres.
    public const string port_battery_switch = "batterySwitch.EXT_IN";       // switch
    public const string control_battery     = "batteryCharge.EXT_IN";       // 0-1 of full
    public const string port_battery_charge = "eu07.BATTERY_CHARGE";        // 0-100
    public const string lamp_battery        = "eu07.LAMP_BATTERY";

    // THE 110 V (eu07_low_voltage.cs): its battery behind the game's main-electrics fuse - the
    // "Bateria" switch under the flap in the assistant's desk - and the breakers beside it, each
    // a fuse of the game's own fuse box. In the order of eu07_sim.low_breaker.
    public const string fuse_battery = "fusebox.ELECTRICS_MAIN";
    private static readonly string[] breaker_fuses = { "fusebox.B_FANS", "fusebox.B_CONVERTER", "fusebox.B_COMPRESSORS", "fusebox.B_CAB_HEATING", "fusebox.B_PANTOGRAPHS",
        "fusebox.B_SANDING", "fusebox.TM_POWER", "fusebox.B_WINDSCREENS", "fusebox.B_LIGHTING", "fusebox.B_HEADLIGHTS" };
    // Three fuses with no lever, that are set HERE and nowhere else: the game's headlights, cab
    // light and sander have them for their power fuse, and so go by the 110 V and their breakers.
    private const string fed_headlights = "fusebox.FED_HEADLIGHTS", fed_lighting = "fusebox.FED_LIGHTING", fed_sanding = "fusebox.FED_SANDING";
    public const string control_low_battery = "lowBatteryCharge.EXT_IN";    // 0-1 of full, kept over a save
    public const string port_low_volts      = "eu07.LOW_VOLTS";             // the 110 V bus, for the desk's low-voltage voltmeter
    // THE FAULT LAMP ("we also need a general fault indicator and a motor temp indicator, so we
    // follow the Dv standard", the author, 2026-10-11). It was first one lamp for everything of
    // OURS that can let go; then, the game's own lamps having been read (NOTES, 23:40): "we
    // already have our status lights for those, im thinking we make the fault lamp adress the
    // stock stuff". So it says what the GAME's two motor lamps say, and nothing that a lamp
    // of this desk says already:
    //   BLINKING  the motors' breaker is out - the game's own, which the game throws out for
    //             heat, for power against the motors' turning, for water (here it is the
    //             "Control" breaker under the flap) - or the motors are at 105 C;
    //   STEADY    a traction motor is dead, or the motors are at 90 C;
    //             and ("What about the brake fault and low resource statuses?", the same
    //             evening) the game's BRAKE warning, by the game's own checker - a leak, any
    //             handbrake on in the train, not exactly one driver's valve cut in, cars coupled
    //             without their air; and something RUNNING OUT, at the figures of the game's
    //             own lamps - sand under a tenth, a battery under a quarter (the 110 V one;
    //             the last-mile one while its switch is on).
    // With the game's warning sound as it comes on, as it goes from steady to blinking, and as
    // one more thing joins what it already shows. Blinking - the game's continuous buzz with
    // it - is kept for what wants doing NOW; a handbrake left on is steady and quiet.
    public const string lamp_fault          = "eu07.LAMP_FAULT";            // the game's own lamp codes: 0 off, 1 on, 3 blinking; 2 and 4 the same with the sound
    public const float  motors_warm_celsius = 90.0f, motors_hot_celsius = 105.0f;       // the game's: its lamp comes on, its lamp blinks
    public const float  sand_low = 0.10f, battery_low = 0.25f;                           // the game's DE6 for sand, its shunter for a battery
    // ACKNOWLEDGED: the button under the lamp. What blinks when it is pressed is "seen": the lamp
    // stands steady and the buzz stops, for as long as that same thing lasts. A cause that goes
    // away is forgotten, so that it blinks again if it comes back; a new one blinks at once.
    public const string port_fault_ack = "faultAck.EXT_IN";
    private int   _want_fault_ack = 0;
    // THE GAME'S OWN VIGILANCE DEVICE, ANSWERED FROM THE DESK ("to have one of those wood cabinets open up, and to
    // have space for the stock stuff, like the vigilance box. that would connect to our system and we can answer
    // the box's vigillance with our click", the author, 2026-10-13). The box is the game's gadget, GadgetATS, mounted
    // by the player in the instrument cupboard under the desk. Read in the game: it counts down while the wheels
    // turn, warns when the count runs out, and five seconds on shuts the throttle and puts the train brake full on;
    // its own button does one thing - sets the count back - and the count is a public field. So:
    //  - the desk's vigilance button sets the count back, exactly as the box's own button does;
    //  - the lamp over the button blinks, with the game's buzz, while the box warns;
    //  - while the box is stopping the train the line contactors are held out (PLW's own way), which is what
    //    cuts power on THIS locomotive - its throttle is a wheel of notches the box cannot wind back;
    //  - the box is powered by a fuse set here: the 110 V there, the control breaker in, and the locomotive not
    //    being hauled dead (power_circuit.train_protection_live, left for this on the 12th).
    // No box aboard: none of it does anything.
    public const string port_vigilance = "vigilance.EXT_IN", lamp_vigilance = "eu07.LAMP_VIGILANCE";
    private const string fed_gadgets = "fusebox.FED_GADGETS";
    // THE LAMP FIRST, THEN THE BUZZER ("the Czuwak flashes before buzzing ... yes, 3 s lamp then 2 s buzz. but we
    // have to have it work with the in game one. is there a way to mute the gadget buzzer and use our own?", the
    // author, 2026-10-13). The real device (an operator's own account, Koleje Slaskie; the Polish Wikipedia's the
    // same): the lamp alone for about 3 s, then the buzzer with it, and 2 s after that the brake. The game's box
    // warns for 5 s before it stops the train, so the two fit: of its five seconds the first three are the lamp
    // in the box on the sill, blinking and silent, and from the fourth on the cab's own buzzer sounds as well
    // (eu07_audio: the buzzer synthesised for the high start) until the driver answers.
    // THE BOX'S OWN BUZZ is its little state lamp's: a LampControl whose blinking carries a looped clip (read in
    // the game: GadgetATSLOD.stateLamp, LampControl.blinkStateBuzzingLoopAudio). While the box is on THIS
    // locomotive those clips are taken off it, and kept, and given back the moment it is on this locomotive no
    // longer - on anything else it buzzes as the game made it.
    public const float vigilance_lamp_alone = 3.0f;            // seconds of lamp before the buzzer joins it
    // THE STOP IS THE LOCOMOTIVE'S OWN. The game's box no longer works the handles of one of ours (Main.cs,
    // vigilance_box_on_ours). When its warning has run out the device's valve opens and empties the brake pipe -
    // the whole train's, at the rate the game's own brake valve does in its emergency place (150) - for as long as
    // the device goes unanswered; and the line contactors are held out with it. Before that valve stands a COCK, on
    // the cab's back wall under the high-voltage one: shut, the device lights its lamp and sounds its buzzer and
    // can do no more. It is kept in the save, and starts open.
    public const string port_vigilance_cock = "vigilanceCock.EXT_IN";
    public const float vigilance_dump = 150.0f;
    private bool _vigilance_vents = false;
    // (half a second each at first: "up the speed of the blink. half it", the author - a quarter of a second each)
    public const float vigilance_lit_for = 0.25f, vigilance_dark_for = 0.25f;
    private DV.CabControls.ButtonBase? _vigilance_button;
    private bool _vigilance_key_holds = false;
    private float _vigilance_blinking = 0.0f;
    private bool  _vigilance_lit = false;
    private DV.Customization.Gadgets.Implementations.GadgetATS? _vigilance;
    private float _vigilance_sought = -10.0f;
    private bool  _vigilance_asks = false, _vigilance_stops = false, _vigilance_buzzes = false;
    private int   _want_vigilance = 0, _vigilance_answers = 0;
    private readonly List<(LampControl lamp, AudioClip? warning, AudioClip? on, AudioClip? blinking)> _vigilance_hushed = new();
    // THE SHP BOX ("time for our own SHP box. It should only spawn in shops when the Signal mod is in", and "shp box
    // should ship with our signalling mod", the author, 2026-10-14). The box is the signalling mod's (dv-signal-
    // dispatch, ShpBox.cs): a gadget made out of the game's vigilance device, mounted in the instrument cupboard
    // beside it, that waits for a track magnet instead of counting seconds - and then warns and stops through the
    // very same count, which is why this locomotive needs no knowledge of that mod to work with it. Told from the
    // game's device by its name alone. Here it is the SAME apparatus as the vigilance device's, as on the real
    // locomotive: the red lamp in the box on the sill, the same buzzer, the same button, the same valve and cock.
    //  - the lamp is steady, not blinking, and the buzzer joins it after 2.5 s; the brake 2 s after that (the
    //    signalling mod's times, from the PLK's and the ministry's own accounts of the device);
    //  - the button is passed to the BOX's own button, so that the box's own rule holds: once it is braking it
    //    is not answered above 10 km/h.
    public const string shp_gadget_name = "ShpBox_Gadget", lamp_shp = "eu07.LAMP_SHP";
    public const float shp_lamp_alone = 2.5f;
    private DV.Customization.Gadgets.Implementations.GadgetATS? _shp;
    private bool  _shp_asks = false, _shp_stops = false, _shp_buzzes = false, _shp_lit = false;
    private int   _shp_answers = 0, _shp_refused = 0;
    private float _hush_tried = -10.0f;

    private static bool is_shp_box(DV.Customization.Gadgets.Implementations.GadgetATS box) => box.name.StartsWith(shp_gadget_name, StringComparison.Ordinal);

    private void hush_vigilance(DV.Customization.Gadgets.Implementations.GadgetATS box)
    {
        foreach (DV.Customization.CustomizerLODObject shown in box.LODObjects)
        {
            if (shown is not DV.Customization.Gadgets.Implementations.GadgetATSLOD face || face.stateLamp == null)
                continue;
            LampControl lamp = face.stateLamp;
            if (_vigilance_hushed.Any(one => one.lamp == lamp))
                continue;
            _vigilance_hushed.Add((lamp, lamp.warningAudio, lamp.onStateBuzzingLoopAudio, lamp.blinkStateBuzzingLoopAudio));
            lamp.SetLampState(LampControl.LampState.Off);       // with its clips still on it: this is what stops a buzz already sounding
            lamp.warningAudio = null;
            lamp.onStateBuzzingLoopAudio = null;
            lamp.blinkStateBuzzingLoopAudio = null;
        }
    }

    private void unhush_vigilance()
    {
        foreach ((LampControl lamp, AudioClip? warning, AudioClip? on, AudioClip? blinking) in _vigilance_hushed)
        {
            if (lamp == null)
                continue;                                       // the box was taken down and is an item again
            lamp.warningAudio = warning;
            lamp.onStateBuzzingLoopAudio = on;
            lamp.blinkStateBuzzingLoopAudio = blinking;
        }
        _vigilance_hushed.Clear();
    }

    private void watch_vigilance(bool live)
    {
        bool gone = false;
        if (_vigilance != null && _vigilance.TrainCar != _car)
        {
            _vigilance = null;                                  // taken off, or the same box mounted somewhere else
            gone = true;
        }
        if (_shp != null && _shp.TrainCar != _car)
        {
            _shp = null;
            gone = true;
        }
        if (gone)
        {
            unhush_vigilance();                                 // (whichever is still aboard is hushed again below)
            _hush_tried = -10.0f;
        }
        if ((_vigilance == null || _shp == null) && Time.time - _vigilance_sought > 1.0f && _car != null && _car.Customization != null)
        {
            _vigilance_sought = Time.time;
            foreach (DV.Customization.Gadgets.Implementations.GadgetATS one in _car.Customization.Customizers.OfType<DV.Customization.Gadgets.Implementations.GadgetATS>())
            {
                if (is_shp_box(one))
                    _shp ??= one;
                else
                    _vigilance ??= one;
            }
        }
        bool pressed = take(ref _want_vigilance);
        DV.Customization.Gadgets.Implementations.GadgetATS? box = _vigilance;
        DV.Customization.Gadgets.Implementations.GadgetATS? shp = _shp;
        if (Time.time - _hush_tried > 1.0f)                     // (a box's face is not there until the box is near enough to be seen)
        {
            _hush_tried = Time.time;
            if (box != null)
                hush_vigilance(box);
            if (shp != null)
                hush_vigilance(shp);
        }
        _shp_asks = shp != null && shp.IsActive && shp.DoWarning;
        _shp_stops = shp != null && shp.IsActive && shp.IsStopping;
        if (pressed && shp != null && shp.IsOn)
        {
            float before = shp.currentTimer;
            DV.Customization.Gadgets.Implementations.GadgetATSLOD? face = shp.LODObjects.OfType<DV.Customization.Gadgets.Implementations.GadgetATSLOD>().FirstOrDefault();
            if (face != null)
                face.OnAckButton();                             // the box's own button: the signalling mod's rule is in it
            else
                shp.currentTimer = shp.CurrentRegimeTimerLength;
            if (before < 0.0f && shp.currentTimer >= 0.0f)
            {
                ++_shp_answers;
                _shp_asks = _shp_stops = false;
            }
            else if (before < 0.0f)
                ++_shp_refused;
        }
        _shp_buzzes = live && _shp_asks && shp != null && -shp.currentTimer >= shp_lamp_alone;
        bool shp_lit = live && _shp_asks;
        if (shp_lit && !_shp_lit)
            _audio?.sound("relay_click", 0.8f, "cab");
        if (!shp_lit && _shp_lit)
            _audio?.sound("relay_click", 0.5f, "cab", 1.25f);
        _shp_lit = shp_lit;
        write(lamp_shp, _shp_lit ? 1.0f : 0.0f);
        _vigilance_asks = box != null && box.IsActive && box.DoWarning;
        _vigilance_stops = box != null && box.IsActive && box.IsStopping;
        if (pressed && box != null && box.IsOn)
        {
            box.currentTimer = box.CurrentRegimeTimerLength;
            ++_vigilance_answers;
            _vigilance_asks = _vigilance_stops = false;
        }
        // (the box's count goes on down past nothing while it warns: how far past is how long it has been asking)
        _vigilance_buzzes = live && _vigilance_asks && box != null && -box.currentTimer >= vigilance_lamp_alone;
        // THE LAMP IS BLINKED FROM HERE, NOT BY THE GAME ("... and the relay closing for every blink", the author,
        // 2026-10-13). The game's blinking lamp keeps its own time, as fast as its glow rises and falls, and tells
        // nobody when it lights. So the lamp is simply switched: lit for `vigilance_lit_for`, dark for
        // `vigilance_dark_for`, starting lit; and every time it lights the device's relay is heard closing - the
        // switchgear's relay click, in the cab.
        if (live && _vigilance_asks)
        {
            bool lit = _vigilance_blinking % (vigilance_lit_for + vigilance_dark_for) < vigilance_lit_for;
            _vigilance_blinking += Time.fixedDeltaTime;
            if (lit && !_vigilance_lit)
                _audio?.sound("relay_click", 0.8f, "cab");
            // ("also. add the open audio": the relay letting go as the lamp goes dark - the same click, lighter and higher)
            if (!lit && _vigilance_lit)
                _audio?.sound("relay_click", 0.5f, "cab", 1.25f);
            _vigilance_lit = lit;
        }
        else
        {
            if (_vigilance_lit)                                 // answered with the lamp lit: the relay lets go then, too
                _audio?.sound("relay_click", 0.5f, "cab", 1.25f);
            _vigilance_blinking = 0.0f;
            _vigilance_lit = false;
        }
        write(lamp_vigilance, _vigilance_lit ? 1.0f : 0.0f);
        // (one valve for both devices, and one cock before it)
        _vigilance_vents = live && (_vigilance_stops || _shp_stops) && read(port_vigilance_cock, 1.0f) > 0.5f;
        if (_vigilance_vents && _car != null && _car.brakeSystem != null && _car.brakeSystem.brakeset != null)
        {
            DV.Simulation.Brake.Brakeset pipe = _car.brakeSystem.brakeset;
            DV.Simulation.Brake.BrakeSystem.VentToAtmosphere(Time.fixedDeltaTime, ref pipe.pipePressure, pipe.pipeVolume, vigilance_dump);
        }
        if (_audio != null)
            _audio.vigilance_buzzer = _vigilance_buzzes || _shp_buzzes;
    }
    private readonly bool[] _fault_seen = new bool[4];          // of the four that blink: the motors' breaker out, the motors at 105 C, the resistor grids red hot, a foreign unit of the train saying "critical"
    private int   _fault_level = 0, _fault_causes = 0;
    private float _fault_code = 0.0f;
    private bool  _fault_buzz_owed = false;
    private DV.Simulation.Brake.BrakeWarningChecker? _brake_checker;
    public const string port_cab_heating    = "cabHeatingSwitch.EXT_IN";
    // The desk's pantograph switches ASK; the railcar's own switch controls are what its valves obey.
    private static readonly (string ask, string valve)[] pantograph_ports =
        { ("pantographAsk.EXT_IN", "pantographSwitch.EXT_IN"), ("pantograph2Ask.EXT_IN", "pantograph2Switch.EXT_IN") };
    private readonly eu07_sim.low_voltage _low = new();
    private Fuse?  _control_power;
    private bool   _has_breakers = false;
    private float  _low_kept = -1.0f;         // what the save has of the 110 V battery's charge; under 0: not yet read from it

    // THE PANTOGRAPHS' AIR and the small compressor that makes it from the battery
    // (eu07_sim.pantograph_air), in the machine room by the outside door behind cab A's handbrake;
    // and the room's own lamps, which the mod turns on and off by the switch at each passage door.
    public const string port_small_compressor = "smallCompressor.EXT_IN";   // its switch, beside it
    public const string control_panto_air     = "pantoAir.EXT_IN";          // bar + 1, kept over a save; 0: never kept
    public const string port_panto_air        = "eu07.PANTO_AIR";           // bar, for its gauge
    public const string port_panto_cock       = "pantoCock.EXT_IN";         // the three-way cock: 1 = on the small compressor
    // the frame's labelled cocks: 1 = open. Three stand open, the drain shut.
    public const string port_cock_control_air = "controlAirCock.EXT_IN";    // the main reservoir into the pantographs' and control circuit
    public const string port_cock_panto_air   = "pantoAirCock.EXT_IN";      // that circuit to the pantographs
    public const string port_cock_drain       = "drainCock.EXT_IN";         // that circuit to the open air
    public const string port_cock_main_res    = "mainResCock.EXT_IN";       // the main compressor's delivery into the main reservoir
    public const string port_room_light_a     = "roomLightA.EXT_IN", port_room_light_b = "roomLightB.EXT_IN";   // two-way: either changes them
    // THE HIGH VOLTAGE BY HAND: the interlock cock on the cab's back wall (1 = P, shut away; 0 =
    // open - the pantographs come down and stay down, the line breaker is thrown out), the two
    // motor isolators on the cage (1 = that pair cut out; the game itself holds their handles
    // while the cock is at P or the wheel off 0), the converter's knife switch (1 = pulled).
    public const string port_hv_interlock = "hvInterlock.EXT_IN", port_converter_knife = "converterKnife.EXT_IN";
    public const string port_hauled = "hauledRun.EXT_IN";                  // the switch behind the driver: 1 = jazda ciagniona (hauled), 0 = train running
    // CAB CUT OUT ("we just put a cab cutout option, which ignores desk I/O and lets the leader do the I/O",
    // the author, 2026-10-12, of two things a multiple unit still wanted: a follower's switches as the
    // leader's stand from the moment they are joined, and somebody to see a follower's lamps). The switch
    // beside the hauled-run one. Set, and joined to another WE-4U whose cab is NOT cut out (by the game's
    // cable or its radio), this locomotive is a FOLLOWER:
    //   IN   its desk is held (the game's own lock on every handle the leader has), and every control in
    //        leader_sets is made what the leader's is, every step - whatever it was when they were joined;
    //   OUT  its lamps light on the LEADER's desk as well as its own, its fault lamp counts there, and the
    //        leader's battery gauge shows the lower of the charges.
    // Throttle, brakes, reverser and sander are the game's to pass on, and it does. The battery, the
    // breakers and the lights stay each locomotive's own.
    public const string port_cab_cutout = "cabCutout.EXT_IN";
    private static readonly string[] leader_sets =
    {
        "pantographAsk.EXT_IN", "pantograph2Ask.EXT_IN", "fastBreakerOn.EXT_IN", "fastBreakerOff.EXT_IN", "relayReset.EXT_IN", "auxRelayReset.EXT_IN",
        "lineContactorsOff.EXT_IN", "wheelSlip.EXT_IN", "converterSwitch.EXT_IN", "compressorSwitch.EXT_IN", "heatingSwitch.EXT_IN", "highStart.EXT_IN",
        "fieldLever.EXT_IN", "batterySwitch.EXT_IN",
    };
    private bool _cab_cut_out = false, _desk_held = false;
    // LED BY A FOREIGN DESK ("Does the game know how to bocznikowanie? we dont want to overheat under way", the
    // author, 2026-10-12): the throttle comes from a vehicle that is not one of ours - a diesel, by the
    // game's cable with this cab cut out and none of ours leading, or by the game's radio transmitting to
    // this one. The game sends a throttle and nothing else, so the wheel and the field lever are
    // then worked here: eu07_auto_notching.cs.
    private bool _foreign_led = false, _foreign_in_group = false;
    private float _not_charging_for = 0.0f;
    private DV.Simulation.Controllers.ControlBlocker? _hold_seen;         // one of the desk's locks, to see whether the hold still stands
    private int _holds_put_back = 0;
    public const string port_train_lights_front = "trainLightsFront.EXT_IN", port_train_lights_rear = "trainLightsRear.EXT_IN";     // what a leading vehicle's lamp selector says
    // WHAT THE OTHER UNITS OF A TRAIN ARE TOLD OF THIS ONE, and this one of them, by the game's own means:
    // a temperature each unit says (MultipleUnitStateObserver), 90 and over a warning, 105 and over
    // critical, for which every desk in the train lights its motor-temperature lamp, steady or
    // blinking. Ours says its motors' heat, as the game's do - and says "critical" when POWER IS
    // ASKED OF IT AND IT CANNOT GIVE ANY: the overload relays out, the main breaker open, an earth
    // fault, the motors' breaker out. Behind a diesel nobody sees its desk, and a unit that has
    // dropped its load is otherwise only felt. NOT when the hauled-run switch is up: then it is
    // meant to be dead. Also its grids: warm a warning, red hot critical.
    public const string port_said_celsius = "eu07.SAID_CELSIUS";
    public const float  said_warning_celsius = 95.0f, said_critical_celsius = 110.0f, refused_seconds = 1.0f;
    private float _refused_for = 0.0f, _said_celsius = -1.0f;
    private DV.Simulation.Cars.MultipleUnitStateObserver.TemperatureState _foreign_says;
    public  bool refusing_power => _refused_for >= refused_seconds;
    private readonly eu07_sim.auto_notching _auto = new();
    private eu07_driver? _leader;
    private List<eu07_driver> _followers = new();
    private readonly Dictionary<string, bool> _own_lamps = new();

    // every locomotive of ours joined to this one, by cable either way along the train or by radio; itself first
    private List<eu07_driver> multiple_unit_group()
    {
        List<eu07_driver> group = new() { this };
        DV.MultipleUnit.MultipleUnitModule? own = _car != null ? _car.muModule : null;
        _foreign_in_group = false;
        _foreign_says = DV.Simulation.Cars.MultipleUnitStateObserver.TemperatureState.Nominal;
        if (own == null)
            return group;
        void add(DV.MultipleUnit.MultipleUnitModule? module)
        {
            if (module == null)
                return;
            eu07_driver? driver = all.Find(one => one != null && one._car == module.train);
            if (driver == null)
            {
                _foreign_in_group = true;           // joined to something that is not one of ours
                if (module.TryGetComponent(out DV.Simulation.Cars.MultipleUnitStateObserver theirs))
                    _foreign_says |= theirs.CarTemperatureState;
            }
            else if (!group.Contains(driver))
                group.Add(driver);
        }
        foreach (bool forward in new[] { true, false })
        {
            DV.MultipleUnit.MultipleUnitModule? module = own;
            bool way = forward;
            for (int hops = 0; module != null && hops < 16; ++hops)
            {
                DV.MultipleUnit.MultipleUnitCable? cable = way ? module.FrontCable : module.RearCable;
                if (cable == null || !cable.IsConnected)
                    break;
                way = !cable.connectedTo.isFront;
                module = cable.connectedTo.muModule;
                add(module);
            }
        }
        if (own.RemoteChannel != null)
        {
            foreach (DV.MultipleUnit.MultipleUnitModule device in own.RemoteChannel.devices)
                add(device);
        }
        return group;
    }

    private void multiple_unit_step()
    {
        _cab_cut_out = read(port_cab_cutout) > 0.5f;
        List<eu07_driver> group = multiple_unit_group();
        _leader = _cab_cut_out ? group.Find(one => one != this && !one._cab_cut_out) : null;
        _followers = _cab_cut_out ? new() : group.Where(one => one != this && one._cab_cut_out && one._leader == this).ToList();
        // Who leads, if none of ours: by radio, whoever transmits to this one; by cable, with this cab cut out,
        // whatever else is on the cable (the cab cut out is what says the throttle is not this desk's own).
        DV.MultipleUnit.MultipleUnitModule? own_module = _car != null ? _car.muModule : null;
        TrainCar? transmitter = own_module != null && own_module.UseWireless && !own_module.IsTransmitter && own_module.RemoteChannel != null && own_module.RemoteChannel.Transmitter != null
            ? own_module.RemoteChannel.Transmitter.train : null;
        bool by_radio = transmitter != null && !all.Exists(one => one != null && one._car == transmitter);
        bool by_cable = _cab_cut_out && _leader == null && _foreign_in_group;
        if ((by_radio || by_cable) != _foreign_led)
        {
            _foreign_led = by_radio || by_cable;
            Main.release_log($"EU07 {_car?.ID}: {(_foreign_led ? $"led by a foreign desk ({(by_radio ? "radio, " + transmitter?.ID : "cable, the cab cut out")}): notching by itself" : "its own wheel again")}");
        }
        // A CAB THAT IS CUT OUT DOES NOTHING, led or not. (The desk was held only while somebody led her: with the
        // switch up and nothing on the cable it was a live desk still - in the author's save of 21:20 the DH4's
        // cable was out, "cab_cut_out True desk_held False". "we just put a cab cutout option, which ignores
        // desk I/O", he had said of it.) With nobody leading, she also takes no power: below.
        bool held = _cab_cut_out || _leader != null || by_cable;
        if (held != _desk_held && _car != null)
        {
            _desk_held = held;
            Main.release_log($"EU07 {_car.ID}: cab cut out: {(held ? "following " + (_leader != null ? _leader._car?.ID : "a foreign desk") + ", the desk held" : "the desk free again")}");
        }
        // THE HOLD IS KEPT, not given once ("cab cutout still allows me to give control the loco from the WE-4's cab.
        // cab cut out is set to up", the author, 2026-10-12). It was put on the moment she became a follower,
        // and the game takes it off again behind the mod's back: its multiple-unit module lets every lock
        // go whenever it looks at its radio and finds none (MultipleUnitModule.SyncFromRadio - at a load, at
        // a coupling). So every step one lock is looked at, and the hold put back, or let go, to what it
        // should be. (A follower by the game's own radio is the game's to hold: left alone.)
        DV.Simulation.Controllers.ControlsBlockController? blocks = _car != null && _car.SimController != null ? _car.SimController.controlsBlocker : null;
        bool the_games = own_module != null && own_module.UseWireless && !own_module.IsTransmitter;
        if (blocks != null && !the_games)
        {
            if (_hold_seen == null)
                _hold_seen = blocks.GetBlockDefinition(port_throttle);
            if (_hold_seen != null && _hold_seen.MUSlaveBlock != held)
            {
                blocks.MUSlaveBlockAllControls(held);
                ++_holds_put_back;
            }
        }
        if (_leader == null)
            return;
        foreach (string port in leader_sets)
        {
            float theirs = _leader.read(port);
            if (_ports.ContainsKey(port) && Mathf.Abs(read(port) - theirs) > 0.0005f)
                write(port, theirs);
        }
    }
    // what a vigilance device or a train stop must ask before it does anything (eu07_power_circuit.cs, train_protection_live)
    public bool train_protection_live => _power != null && _power.train_protection_live(_low.live);
    private static readonly string[] isolator_ports = { "motorIsolator12.EXT_IN", "motorIsolator34.EXT_IN" };
    private int _isolators_set = -1;          // which pairs are out as the circuit has them (bit 0: 1-2, bit 1: 3-4); under 0: not yet given
    private bool _hv_was_open = false;
    // the lamps over the meters
    public const string port_instrument_light = "instrumentLight.EXT_IN";
    private readonly eu07_sim.pantograph_air _air = new();
    private float      _air_kept = -1.0f;     // under 0: not yet read from the save
    private Transform? _room_lights;

    private TrainCar?               _car;
    private DV.Damage.DamageController? _damage;
    private eu07_audio?             _audio;
    private eu07_sim.power_circuit? _power;
    private readonly Dictionary<string, Port> _ports = [];
    private readonly Dictionary<string, Fuse> _fuses = [];

    private int   _field_wanted = 0;
    private float _breaker_switch = 0.0f, _reset_switch = 0.0f, _high_switch = 0.0f, _line_off_switch = -1.0f, _line_off_press = 0.0f;
    private bool  _line_off_key = false;
    // THE PANEL ON THE SCREEN, AND THE KEYPAD. They were there from the first crude desk and
    // had grown into a second set of instruments that says what is wrong and what to do about
    // it. "Honestly. i think the debug screen we have now, should not stay. every other modded
    // loco doesnt have one of these, and people will learn it the hard way" (the author,
    // 2026-10-11). So for a player there is neither: the locomotive tells him what its desk,
    // its lamps and its gauges tell him. Both remain a DEVELOPER's - where the mod's folder
    // has the "debug" folder that also turns the status file and the command hook on - and
    // there the panel starts hidden: keypad 3 shows it.
    private bool _panel = false;
    private bool _developer = false;
    private string _problem = "";
    private bool _desk_v2 = false;

    // Presses, latched where they happen (a port's event, the keypad) and taken in
    // FixedUpdate: a short click or key tap can fall between two physics steps. Ints for
    // Interlocked, in case a port is set from another thread.
    private int _want_close, _want_open, _want_reset, _want_aux_reset;
    private readonly List<Action> _unhook = [];

    // PLP, the wheel-slip brake button: read and shown, but nothing in the circuit takes it
    // yet (a slip brake is the brake system's business, not power_circuit's).
    public bool slip_button { get; private set; } = false;

    public static readonly List<eu07_driver> all = [];
    public TrainCar? car => _car;

    void OnDestroy()
    {
        foreach (Action undo in _unhook)
            undo();
        _unhook.Clear();
        all.Remove(this);
        unhush_vigilance();
        _audio?.destroy();
    }

    public void attach(TrainCar car)
    {
        _car = car;
        all.Add(this);
        StringBuilder found = new();
        foreach (Port? port in car.SimController.SimulationFlow.AllPorts)
        {
            if (port != null)
            {
                _ports[port.id] = port;
                found.Append(port.id).Append(' ');
            }
        }
        Main.release_log($"EU07 {car.ID} ({car.carLivery.id}): ports {found}");
        found.Clear();
        foreach (Fuse? fuse in car.SimController.SimulationFlow.AllFuses)
        {
            if (fuse != null)
            {
                _fuses[fuse.id] = fuse;
                found.Append(fuse.id).Append(' ');
            }
        }
        Main.release_log($"EU07 {car.ID}: fuses {found}");
        foreach (SimComponent part in car.SimController.SimulationFlow.OrderedSimComps)
        {
            if (part is ElectricCompressor own)
                _compressor_rate = own.maxBarLiterProductionRate;
            if (part is HeatReservoir tank && tank.id == "tmHeat")
                _reservoir_capacity = tank.heatCapacity;
        }
        Main.release_log($"EU07 {car.ID}: compressors give {_compressor_rate:F0} bar l/s together");
        // The wipers. The game wires ONE wiper control for a vehicle; this one has four wipers
        // with a valve each. Each controller is called WiperLeftA ... and its control is the
        // same with a small letter.
        foreach (DV.Rain.WiperController wiper in car.GetComponentsInChildren<DV.Rain.WiperController>(true))
        {
            string control = char.ToLowerInvariant(wiper.name[0]) + wiper.name.Substring(1) + ".EXT_IN";
            if (_ports.ContainsKey(control))
            {
                _wipers.Add((wiper, control));
                _wiper_speeds.Add(-1);
            }
        }
        Main.release_log($"EU07 {car.ID}: {_wipers.Count} wipers");

        // Control voltage is the 110 V's (low_voltage_step), where the prefab has its breakers;
        // control air is simply there still. The auxiliary compressor is to come.
        Fuse control_power = new("eu07", new FuseDefinition("CONTROL_POWER", true));
        _control_power = control_power;
        _has_breakers = _fuses.ContainsKey(breaker_fuses[0]) && _fuses.ContainsKey(fed_headlights);
        _developer = eu07_debug.wanted;
        // the game's own judge of the brakes (what its locomotives' brake lamps ask)
        DV.Simulation.Brake.BrakeWarningChecker brakes = new();
        brakes.SetTrainCar(car);
        _brake_checker = brakes;
        // (as the game shuts down its keeper of coroutines is gone before this, and letting go threw: an exception,
        // and a ping, at every quitting - debug/errors.txt, 2026-10-12)
        _unhook.Add(() =>
        {
            try
            {
                brakes.SetTrainCar(null);
            }
            catch (Exception)
            {
            }
        });
        _room_lights = car.GetComponentsInChildren<Transform>(true).FirstOrDefault(one => one.name == "EU07RoomLights");
        Main.release_log($"EU07 {car.ID}: the 110 V breakers {(_has_breakers ? "are in the prefab" : "are NOT in the prefab: control voltage is simply there")}");
        Fuse control_air   = new("eu07", new FuseDefinition("CONTROL_AIR"  , true));

        if (!_ports.TryGetValue(port_wheel_RPM, out Port wheel_RPM))
            throw new InvalidOperationException($"the prefab has no {port_wheel_RPM}");
        int seed = 0;
        foreach (char letter in car.ID)
            seed = seed * 31 + letter;
        _power = new eu07_sim.power_circuit(control_power, control_air, [wheel_RPM], null, seed);
        // Without switches of their own, the converter and compressors are simply on.
        if (!_ports.ContainsKey(port_converter_switch))
            _power.converter_switch = true;
        if (!_ports.ContainsKey(port_compressor_switch))
            _power.compressor_switch[0] = _power.compressor_switch[1] = true;

        _desk_v2 = _ports.ContainsKey(port_breaker_on);
        string[] wanted_ports = _desk_v2
            ? [port_throttle, port_reverser, port_supply_volts, port_torque, port_line_current,
               port_breaker_on, port_breaker_off, port_relay_reset, port_aux_reset, port_line_off, port_slip_button,
               port_high_start, port_converter_switch, port_compressor_switch, port_heating_switch, port_field_lever, lamp_heating,
               port_battery_switch, control_battery, port_battery_charge, lamp_battery]
            : [port_throttle, port_reverser, port_supply_volts, port_torque, port_line_current];
        foreach (string wanted in wanted_ports)
        {
            if (!_ports.ContainsKey(wanted))
                _problem += wanted + " ";
        }
        Main.release_log($"EU07 {car.ID}: desk {(_desk_v2 ? "v2" : _ports.ContainsKey(port_fast_breaker) ? "v1" : "none, keypad only")}");
        if (_problem.Length > 0)
            Main.release_log($"EU07 {car.ID}: the prefab lacks {_problem}");

        if (_desk_v2)
        {
            on_press(port_breaker_on , () => Interlocked.Exchange(ref _want_close, 1));
            on_press(port_breaker_off, () => Interlocked.Exchange(ref _want_open, 1));
            on_press(port_relay_reset, () => Interlocked.Exchange(ref _want_reset, 1));         // OPN: reset_relays only
            on_press(port_fault_ack  , () => Interlocked.Exchange(ref _want_fault_ack, 1));
            on_press(port_vigilance  , () => Interlocked.Exchange(ref _want_vigilance, 1));
            on_press(port_aux_reset  , () => Interlocked.Exchange(ref _want_aux_reset, 1));     // WJ1: reset_auxiliary_relays only
        }
    }

    // A button's press is the rising edge of its port, caught from the port's own event:
    // Port.Value's setter raises ValueUpdatedInternally on every change, whoever sets it
    // (the desk, a key, the debug hook). Polled in FixedUpdate a short press could be lost.
    private void on_press(string id, Action pressed)
    {
        if (!_ports.TryGetValue(id, out Port port))
            return;
        float last = port.Value;
        void changed(float value)
        {
            if (value > 0.5f && last <= 0.5f)
                pressed();
            last = value;
        }
        port.ValueUpdatedInternally += changed;
        _unhook.Add(() => port.ValueUpdatedInternally -= changed);
    }

    private static bool take(ref int wanted) => Interlocked.Exchange(ref wanted, 0) != 0;

    private static void set(Fuse? fuse, bool on)
    {
        if (fuse != null && fuse.State != on)
            fuse.ChangeState(on);
    }

    // A desk lamp: dark without the 110 V, whatever it has to say.
    // (a lamp of a follower whose cab is cut out lights on its leader's desk too)
    private void light(string id, bool lit)
    {
        bool fed = !_has_breakers || _low.live;
        _own_lamps[id] = lit && fed;
        bool theirs = false;
        foreach (eu07_driver follower in _followers)
            theirs |= follower != null && follower._own_lamps.TryGetValue(id, out bool shows) && shows;
        write(id, (lit || theirs) && fed);
    }

    // THE 110 V, every step, before the power circuit's own: what the battery's switch and the
    // breakers under the flap say, what hangs on the bus, and from that whether there is
    // control voltage at all and which circuits are fed.
    private void low_voltage_step(eu07_sim.power_circuit power, float delta_seconds)
    {
        if (!_has_breakers)
            return;
        eu07_sim.low_voltage low = _low;
        if (_low_kept < 0.0f && _ports.ContainsKey(control_low_battery))
        {
            _low_kept = Mathf.Clamp01(read(control_low_battery));
            low.charge = _low_kept;
        }
        low.battery_switch = !_fuses.TryGetValue(fuse_battery, out Fuse master) || master.State;
        for (int index = 0; index < breaker_fuses.Length; ++index)
            low.breaker[index] = !_fuses.TryGetValue(breaker_fuses[index], out Fuse breaker) || breaker.State;
        // a running converter holds the bus and charges; so does the last-mile battery's own converter while its switch is on
        low.supplied = power.converter_charging || (power.battery_wanted && power.battery_joules > 0.0f);

        // the machine room's lamps: on the lighting's breaker, by their switch
        bool room_lit = low.fed(eu07_sim.low_breaker.lighting) && (read(port_room_light_a) > 0.5f) != (read(port_room_light_b) > 0.5f);
        bool meters_lit = low.fed(eu07_sim.low_breaker.lighting) && read(port_instrument_light) > 0.5f;
        if (_car != null && _car.interior != null)
        {
            // (the interior comes and goes with the player: looked for each time, it is a handful of children)
            foreach (Transform found in _car.interior.GetComponentsInChildren<Transform>(true))
                if (found.name == "EU07InstrumentLights" && found.gameObject.activeSelf != meters_lit)
                    found.gameObject.SetActive(meters_lit);
        }
        if (_room_lights != null && _room_lights.gameObject.activeSelf != room_lit)
            _room_lights.gameObject.SetActive(room_lit);

        float watts = 0.0f;
        if (low.live)
        {
            watts = eu07_sim.low_voltage.desk_watts;
            foreach ((string _, string valve) in pantograph_ports)
                if (read(valve) > 0.5f)
                    watts += eu07_sim.low_voltage.valve_watts;
            if (power.fast_breaker_closed)
                watts += eu07_sim.low_voltage.breaker_watts;
            if (power.line_contactors_closed)
                watts += eu07_sim.low_voltage.contactors_watts;
            if (low.fed(eu07_sim.low_breaker.headlights))
            {
                foreach (string end in new[] { "A", "B" })
                {
                    foreach (string lamp in new[] { "Left", "Top", "Right" })
                        if (read($"light{lamp}{end}.EXT_IN") > 0.5f)
                            watts += read($"lightDim{lamp}{end}.EXT_IN") > 0.5f ? eu07_sim.low_voltage.headlight_dim_watts : eu07_sim.low_voltage.headlight_watts;
                    foreach (string lamp in new[] { "Left", "Right" })
                        if (read($"red{lamp}{end}.EXT_IN") > 0.5f)
                            watts += eu07_sim.low_voltage.tail_lamp_watts;
                }
            }
            if (low.fed(eu07_sim.low_breaker.lighting))
                watts += eu07_sim.low_voltage.cab_light_watts * Mathf.Clamp01(read("cabLight.EXT_IN"));
            if (low.fed(eu07_sim.low_breaker.sanding) && read("sander.CONTROL_EXT_IN") > 0.5f)
                watts += eu07_sim.low_voltage.sanding_watts;
            if (_air.pump_running)
                watts += eu07_sim.pantograph_air.pump_watts;
            if (room_lit)
                watts += 4.0f * 25.0f;
            if (meters_lit)
                watts += 7.0f * 5.0f;
        }
        low.load_watts = watts;
        low.simulate(delta_seconds);
        write(port_low_volts, low.volts);
        float celsius = read(port_motor_celsius, power.motor_celsius);
        // The motors' breaker out counts only while traction is ASKED for - the reverser off
        // neutral and the wheel off 0 - which is the game's own rule for its lamp
        // (TractionMotorSet.ConnectionRequested): a breaker that is simply out on a standing
        // locomotive is not a fault, and does not blink and buzz at whoever switches the battery on.
        bool motors_out = _fuses.TryGetValue(fuse_motors, out Fuse motors_breaker) && !motors_breaker.State
                       && power.contactors.direction != 0 && power.contactors.wanted > 0;
        bool motor_dead = read("tm.WORKING_TRACTION_MOTORS", eu07_sim.motors) < eu07_sim.motors - 0.5f || power.health <= 0.0f;
        bool[] said =
        {
            // (the grids: what running on the resistors too long costs - eu07_power_circuit.cs, "the grids that stay in".
            // Steady from 400 C: get off the resistors, or ease the current. Blinking, with the sound, from 600: they are wearing.)
            motors_out, celsius >= motors_hot_celsius, power.grid_celsius >= eu07_sim.power_circuit.grids_hot_celsius,
            // (and what a unit of the train that is not ours says of itself, as its own kind would show it: a diesel's motors hot)
            (_foreign_says & DV.Simulation.Cars.MultipleUnitStateObserver.TemperatureState.Critical) != 0,
            motor_dead, celsius >= motors_warm_celsius, power.grid_celsius >= eu07_sim.power_circuit.grids_warm_celsius,
            (_foreign_says & DV.Simulation.Cars.MultipleUnitStateObserver.TemperatureState.Warning) != 0,
            // the main breaker in and the converter not giving its 110 V for a quarter of a minute: no charging, no
            // compressors, no air through the motors - whatever stopped it (its switch, its breaker, its relay, its knife)
            (_not_charging_for = power.fast_breaker_closed && !power.converter_charging ? _not_charging_for + Time.fixedDeltaTime : 0.0f) >= 15.0f,
            _brake_checker != null && _brake_checker.BrakeWarningState,
            read("sand.NORMALIZED", 1.0f) < sand_low,
            low.charge < battery_low,
            power.battery_wanted && power.battery_charge < battery_low,
        };
        int causes = said.Count(one => one);
        bool acknowledge = take(ref _want_fault_ack);
        for (int urgent = 0; urgent < _fault_seen.Length; ++urgent)
            _fault_seen[urgent] = said[urgent] && (_fault_seen[urgent] || acknowledge);
        bool blinking = false;
        for (int urgent = 0; urgent < _fault_seen.Length; ++urgent)
            blinking |= said[urgent] && !_fault_seen[urgent];
        int level = !low.live || causes == 0 ? 0 : blinking ? 2 : 1;
        // The lamp's codes are the game's: 1 on, 3 blinking, and 2 and 4 the same WITH its sound,
        // which is heard once as the code is given. So the sound as it gets worse - a level up,
        // or one more cause at the same level, where the code must be taken away for a step
        // and given again to be heard.
        float quiet = level == 0 ? 0.0f : level == 1 ? 1.0f : 3.0f, loud = level == 1 ? 2.0f : 4.0f;
        bool worse = level > _fault_level || (level == _fault_level && level > 0 && causes > _fault_causes);
        if (level == 0)
        {
            _fault_code = 0.0f;
            _fault_buzz_owed = false;
        }
        else if (worse)
        {
            _fault_buzz_owed = _fault_code == loud;
            _fault_code = _fault_buzz_owed ? quiet : loud;
        }
        else if (_fault_buzz_owed)
        {
            _fault_code = loud;
            _fault_buzz_owed = false;
        }
        else if (level != _fault_level)
            _fault_code = quiet;
        _fault_level = level;
        _fault_causes = causes;
        float shown_fault = _fault_code;
        foreach (eu07_driver follower in _followers)
            shown_fault = Mathf.Max(shown_fault, follower != null ? follower._fault_code : 0.0f);
        write(lamp_fault, low.live ? shown_fault : 0.0f);

        set(_control_power, low.live);
        if (_fuses.TryGetValue(fed_headlights, out Fuse headlights))
            set(headlights, low.fed(eu07_sim.low_breaker.headlights));
        if (_fuses.TryGetValue(fed_lighting, out Fuse lighting))
            set(lighting, low.fed(eu07_sim.low_breaker.lighting));
        if (_fuses.TryGetValue(fed_sanding, out Fuse sanding))
            set(sanding, low.fed(eu07_sim.low_breaker.sanding));
        if (_fuses.TryGetValue(fed_gadgets, out Fuse gadget_feed))
            // HAULED, OR A FOLLOWER, IT IS OUT ("Now to check if Ciagniona/Multiple unit isolates the Vigilance", the
            // author, 2026-10-13). Hauled it was, from the start (train_protection_live). A follower it was NOT: a
            // box in the second locomotive of a pair went on counting with nobody in that cab, and would have
            // emptied the train's pipe. A locomotive whose desk is held - its cab cut out, or led by another desk,
            // ours or a foreign one - feeds no vigilance device.
            set(gadget_feed, low.fed(eu07_sim.low_breaker.control) && power.train_protection_live(low.live) && !_desk_held && !_foreign_led);
        watch_vigilance(low.live);

        // what each breaker holds back of the desk's own switches (the "control" one is the
        // game's motor fuse, which holds the line contactors out: below)
        power.converter_switch &= low.breaker[(int) eu07_sim.low_breaker.converter];
        if (_ports.ContainsKey(port_converter_knife))
            power.converter_knife_open = read(port_converter_knife) > 0.5f;         // (not the desk's switch: pulled under load it arcs - eu07_power_circuit.cs)
        // The interlock cock: opened, the line breaker goes out at once and the pantographs come
        // down (below). And the isolators as their handles stand, given to the circuit when it
        // will take them - with the line breaker out and the wheel at 0, which is also when the
        // game lets the handles be moved at all.
        bool hv_open = _ports.ContainsKey(port_hv_interlock) && read(port_hv_interlock) < 0.5f;
        if (hv_open && !_hv_was_open)
            power.open_fast_breaker();
        _hv_was_open = hv_open;
        if (_ports.ContainsKey(isolator_ports[0]) && _ports.ContainsKey(isolator_ports[1]))
        {
            int wanted = (read(isolator_ports[0]) > 0.5f ? 1 : 0) | (read(isolator_ports[1]) > 0.5f ? 2 : 0);
            if (wanted != _isolators_set && power.cut_out_motors((wanted & 1) != 0, (wanted & 2) != 0))
            {
                Main.release_log($"EU07 {_car?.ID}: motor isolators: pair 1-2 {((wanted & 1) != 0 ? "OUT" : "in")}, pair 3-4 {((wanted & 2) != 0 ? "OUT" : "in")}");
                _isolators_set = wanted;
            }
        }
        // (and with the MAIN RESERVOIR cock shut the main compressor has nowhere to deliver: it is not run)
        bool delivery_open = read(port_cock_main_res, 1.0f) > 0.5f;
        power.compressor_switch[0] &= low.breaker[(int) eu07_sim.low_breaker.compressors] && delivery_open;
        power.compressor_switch[1] &= low.breaker[(int) eu07_sim.low_breaker.compressors] && delivery_open;
        power.fans_fed = low.breaker[(int) eu07_sim.low_breaker.fans];
        power.cab_heating_switch = read(port_cab_heating) > 0.5f && low.breaker[(int) eu07_sim.low_breaker.cab_heating];
        // THE AIR. A prefab without the small compressor's switch has air as it always had.
        bool has_air = _ports.ContainsKey(port_small_compressor) && _ports.ContainsKey(control_panto_air);
        eu07_sim.pantograph_air air = _air;
        if (has_air)
        {
            if (_air_kept < 0.0f)
            {
                // a save from before this kept nothing: as much as the main reservoir would have given it
                float kept = read(control_panto_air);
                air.bar = kept >= 1.0f ? kept - 1.0f : Mathf.Min(Mathf.Max(power.main_reservoir_bar, 0.0f), eu07_sim.pantograph_air.feed_bar);
                _air_kept = air.bar;
            }
            air.main_reservoir_bar = power.main_reservoir_bar;
            air.cock_to_small = read(port_panto_cock) > 0.5f;
            air.feed_open = read(port_cock_control_air, 1.0f) > 0.5f;
            air.pantographs_open = read(port_cock_panto_air, 1.0f) > 0.5f;
            air.drain_open = read(port_cock_drain) > 0.5f;
            // the small compressor: on the battery, and only with the line breaker open
            air.pump_wanted = read(port_small_compressor) > 0.5f && low.live && !power.fast_breaker_closed;
        }
        // The pantographs' valves hold only while they are fed: no 110 V, or their breaker out,
        // and both come down whatever the desk's switches ask. And they want AIR: so much to go
        // up, less to stay up.
        int raised = 0;
        foreach ((string ask, string valve) in pantograph_ports)
        {
            if (!_ports.ContainsKey(ask) || !_ports.ContainsKey(valve))
                continue;
            bool is_up = read(valve) > 0.5f;
            bool up = read(ask) > 0.5f && !hv_open && low.fed(eu07_sim.low_breaker.pantographs) && (!has_air || (is_up ? air.can_hold : air.can_raise));
            if (is_up != up)
                write(valve, up);
            if (up)
                ++raised;
        }
        if (has_air)
        {
            air.pantographs_up = raised;
            air.simulate(delta_seconds);
            power.pantograph_air_ok = air.breaker_air;
            write(port_panto_air, air.bar);
            if (Mathf.Abs(air.bar - _air_kept) > 0.02f)
            {
                _air_kept = air.bar;
                write(control_panto_air, _air_kept + 1.0f);
            }
            if (_audio != null)
                _audio.small_compressor = air.pump_running;
        }
        if (_low_kept >= 0.0f && Mathf.Abs(low.charge - _low_kept) > 0.0005f)
        {
            _low_kept = low.charge;
            write(control_low_battery, _low_kept);
        }
    }

    private double _metres_run = -1.0;        // what the recorder's counter shows; under 0: not yet read from the save
    private float  _since_attach = 0.0f;
    private int    _arcs_seen = -1, _flashovers_seen = -1;      // how many of each the circuit had counted when last looked at
    // The doors: each leaf with the frame it hangs in, its handles as they rest and which way
    // lowers each one's lever, and where it stood a frame ago.
    private sealed class door_watch
    {
        public string name = "";
        public Transform frame = null!, leaf = null!;
        public Transform[] handles = [];
        public Quaternion[] rest = [];
        public float[] lowers = [];
    }
    private readonly List<door_watch> _doors = [];
    private GameObject? _doors_of;

    // THE CONTACTORS, SEEN ("contactor animation and overhaul", the author, 2026-10-12: "these contactors just
    // sitting here after almost melting to bits look a bit off for a deep fried electrical system"). The build
    // gives each of the cage's 48 an arm and an arc chute under an object called Contactor_<name>, the name the
    // circuit's own (SL1, IS, SR7, SP3 ...). The arm stands shut or has fallen open as the circuit's contactor
    // of that name stands, and goes from one to the other in a twentieth of a second - an air cylinder throws
    // it, a spring throws it back.
    //
    // WHAT EACH ONE HAS BEEN THROUGH IS ITS OWN: its soot, if it has had to break a current, and its heat, if it
    // stood shut while damage was done - eu07_contactor_marks.cs has who gets what, and why. Here they are KEPT
    // (in sixteen controls nobody can touch, contactorMarks0-15, which the game saves) and SHOWN.
    private readonly eu07_sim.contactor_marks _marks = new();
    private bool _marks_read = false;
    private static string marks_port(int number) => $"contactorMarks{number}.EXT_IN";

    // every step of the simulation, cage in sight or not: `damage` is what the circuit has just handed the game
    private void mark_contactors(eu07_sim.power_circuit power, float damage)
    {
        if (!_marks_read)
        {
            _marks_read = true;
            for (int number = 0; number < eu07_sim.contactor_marks.numbers && _ports.ContainsKey(marks_port(number)); ++number)
                _marks.unpack(number, Mathf.RoundToInt(read(marks_port(number))));
        }

        _marks.step(power, Time.fixedDeltaTime, damage, _damage != null && _damage.electricalPT != null ? Mathf.Clamp01(_damage.electricalPT.HealthPercentage) : -1.0f);
        if (_marks.changed)
        {
            _marks.changed = false;
            for (int number = 0; number < eu07_sim.contactor_marks.numbers && _ports.ContainsKey(marks_port(number)); ++number)
            {
                int packed = _marks.packed(number);
                if (Mathf.RoundToInt(read(marks_port(number))) != packed)
                    write(marks_port(number), packed);
            }
        }
    }

    private sealed class contactor_watch
    {
        public string name = "";
        public int index;                           // in eu07_sim.contactor_marks.names: whose marks
        public Transform? arm;
        public Renderer? chute, arm_shown;
        public float shut, soot_shown = -1.0f, heat_shown = -1.0f;
        public int stage = -1;                      // which of the build's sooted materials its chute wears
    }
    private readonly List<contactor_watch> _contactors = [];
    private GameObject? _contactors_of;
    private MaterialPropertyBlock? _tint;
    // the chute's material at each stage of its sooting, clean first: the build's, kept on a hidden object (ContactorSoot)
    private Material[] _soot_stages = [];
    public const float contactor_opens = 24.0f, contactor_seconds = 0.05f;
    // heat on the cement: browned. On the arm's copper: red-brown, purple-blue, grey scale. Soot on the contact: black.
    private static readonly Color chute_toasted = new(0.80f, 0.66f, 0.48f);
    private static readonly Color[] copper_by_heat = [new(0.47f, 0.27f, 0.18f), new(0.36f, 0.16f, 0.12f), new(0.23f, 0.17f, 0.31f), new(0.11f, 0.11f, 0.12f)];
    private static readonly Color contact_burnt = new(0.045f, 0.042f, 0.040f);

    private static Color heated_copper(float heat)
    {
        float along = Mathf.Clamp01(heat) * (copper_by_heat.Length - 1);
        int from = Mathf.Min((int) along, copper_by_heat.Length - 2);
        return Color.Lerp(copper_by_heat[from], copper_by_heat[from + 1], along - from);
    }

    private void watch_contactors(eu07_sim.power_circuit power)
    {
        GameObject? parts = _car == null ? null : _car.loadedExternalInteractables != null ? _car.loadedExternalInteractables : _car.loadedDummyExternalInteractables;
        if (parts != _contactors_of)
        {
            _contactors_of = parts;
            _contactors.Clear();
            _soot_stages = [];
            if (parts != null)
            {
                foreach (Transform found in parts.GetComponentsInChildren<Transform>(true))
                {
                    if (found.name == "ContactorSoot" && found.GetComponent<Renderer>() is Renderer keeper)
                        _soot_stages = keeper.sharedMaterials;
                    if (!found.name.StartsWith("Contactor_"))
                        continue;
                    string name = found.name.Substring("Contactor_".Length);
                    int index = eu07_sim.contactor_marks.index_of(name);
                    if (index < 0 || !power.contactor_locations.ContainsKey(name))
                        continue;
                    Transform? arm = found.Find("Arm"), chute = found.Find("Chute");
                    _contactors.Add(new contactor_watch { name = name, index = index, arm = arm, chute = chute != null ? chute.GetComponent<Renderer>() : null,
                        arm_shown = arm != null ? arm.GetComponent<Renderer>() : null, shut = power.contactor_locations[name].is_contactor_on(name) ? 1.0f : 0.0f });
                }
                Main.release_log($"EU07 {_car?.ID}: {_contactors.Count} contactors found in the cage to move, {_soot_stages.Length} stages of soot for their chutes");
            }
        }
        if (_contactors.Count == 0)
            return;
        _tint ??= new MaterialPropertyBlock();
        foreach (contactor_watch one in _contactors)
        {
            bool on = power.contactor_locations[one.name].is_contactor_on(one.name);
            float shut = Mathf.MoveTowards(one.shut, on ? 1.0f : 0.0f, Time.deltaTime / contactor_seconds);
            if ((shut != one.shut || one.soot_shown < 0.0f) && one.arm != null)
                one.arm.localRotation = Quaternion.Euler(contactor_opens * (1.0f - shut), 0.0f, 0.0f);
            one.shut = shut;
            float black = _marks.soot[one.index], hot = _marks.heat[one.index];
            if (Mathf.Abs(black - one.soot_shown) < 0.01f && Mathf.Abs(hot - one.heat_shown) < 0.01f)
                continue;
            one.soot_shown = black;
            one.heat_shown = hot;
            if (one.chute != null)
            {
                // the chute: the painted stage its soot has come to, browned over by its heat
                int stage = _soot_stages.Length > 1 ? Mathf.Clamp(Mathf.RoundToInt(black * (_soot_stages.Length - 1)), 0, _soot_stages.Length - 1) : -1;
                if (stage >= 0 && stage != one.stage)
                {
                    one.stage = stage;
                    one.chute.sharedMaterial = _soot_stages[stage];
                }
                _tint.SetColor("_Color", Color.Lerp(Color.white, chute_toasted, hot));
                one.chute.SetPropertyBlock(_tint);
            }
            if (one.arm_shown != null)
            {
                // the arm: its bar by its heat; its contact (the second material) by both, the soot winning
                Color bar = heated_copper(hot);
                _tint.SetColor("_Color", bar);
                one.arm_shown.SetPropertyBlock(_tint, 0);
                if (one.arm_shown.sharedMaterials.Length > 1)
                {
                    _tint.SetColor("_Color", Color.Lerp(bar, contact_burnt, Mathf.Clamp01(black * 1.6f)));
                    one.arm_shown.SetPropertyBlock(_tint, 1);
                }
            }
        }
    }
    public const float handle_press_degrees = 28.0f;
    // The wipers: each one's controller, the control of its valve, and the speed it was last given.
    private readonly List<(DV.Rain.WiperController wiper, string port)> _wipers = [];
    private readonly List<int> _wiper_speeds = [];
    // The motors' heat: the game's own reservoir for it (its lamp, its wear and its blown fuse go
    // by that), how much heat a degree of it is, and whether what the save had has been read.
    public const string port_motor_celsius = "tmHeat.TEMPERATURE";
    public const string fuse_motors        = "fusebox.TM_POWER";
    private float  _reservoir_capacity = 500.0f;
    private bool   _motor_heat_read = false;
    private float  _battery_kept = -1.0f;     // what the save has of the battery's charge; under 0: not yet read from it
    private const float game_reservoir_limit_bar = 8.0f;      // over the atmosphere: the most the game holds in a main reservoir
    private float  _compressor_rate = 80.0f;  // bar litres a second from both compressors: the game's own compressor's figure, read from it

    private float read(string id, float otherwise = 0.0f) => _ports.TryGetValue(id, out Port port) ? port.Value : otherwise;

    private void write(string id, float value)
    {
        if (_ports.TryGetValue(id, out Port port))
            port.Value = value;
    }

    private void write(string id, bool lit) => write(id, lit ? 1.0f : 0.0f);

    // ---- for mod/eu07_debug.cs: the same handles, by name ---------------------------------
    public string command(string[] words)
    {
        eu07_sim.power_circuit? power = _power;
        if (power == null || _car == null)
            return "no power circuit";
        float number = words.Length > 1 && float.TryParse(words[words.Length - 1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float parsed) ? parsed : 0.0f;
        switch (words[0])
        {
            case "ws"       : if (number > 0.5f) power.close_fast_breaker(); else power.open_fast_breaker(); return $"WS {power.fast_breaker_closed}";
            case "reset"    : power.reset_auxiliary_relays(); return $"relays clear {power.reset_relays()}";
            case "field"    : _field_wanted = Mathf.Clamp((int) number, 0, eu07_sim.field_positions - 1); return $"field lever {_field_wanted}";
            case "high"     : power.high_start = number > 0.5f; return $"high start {power.high_start}";
            case "odometer" : _metres_run = Math.Max(0.0, number) * 1000.0; write(control_odometer, number); return $"counter at {_metres_run / 1000.0:F2} km";
            case "marks"    :
            {
                // marks <a contactor's name | in | all> <soot 0-1> <heat 0-1>: what a contactor has been through, put on by hand ("in": those standing shut now)
                if (words.Length != 4 || !float.TryParse(words[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float soot_given)
                                      || !float.TryParse(words[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float heat_given))
                    return "marks <name | in | all> <soot 0-1> <heat 0-1>";
                int marked = 0;
                for (int index = 0; index < eu07_sim.contactor_marks.names.Length; ++index)
                {
                    string name = eu07_sim.contactor_marks.names[index];
                    if (words[1] != "all" && words[1] != name && !(words[1] == "in" && power.contactor_locations.TryGetValue(name, out var branch) && branch.is_contactor_on(name)))
                        continue;
                    _marks.put(index, soot_given, heat_given);
                    ++marked;
                }
                return $"{marked} contactors marked: soot {soot_given:F2}, heat {heat_given:F2}";
            }
            case "grids"    : power.grid_celsius = Mathf.Clamp(number, eu07_sim.power_circuit.ambient_celsius, eu07_sim.power_circuit.grids_melt_celsius); return $"the resistor grids at {power.grid_celsius:F0} C";
            case "health"   :
                // health <0-1>: the game's electrical drivetrain pool, to put back what a test of the above took
                if (_damage == null || _damage.electricalPT == null || words.Length != 2)
                    return "health <0-1>, with a damage controller";
                _damage.electricalPT.SetCurrentHealthPercentage(Mathf.Clamp01(number));
                return $"drivetrain health {_damage.electricalPT.HealthPercentage:F3}";
            case "battery"  : power.battery_wanted = number > 0.5f; return $"battery switch {power.battery_wanted} (a desk with the switch overrules this)";
            case "control"  :
                // control <object name> <0-1>: a handle of the loaded interior that has no port (a door, the flap)
                if (words.Length != 3 || _car.interior == null)
                    return "control <object name> <0-1>, with the interior loaded";
                foreach (DV.CabControls.ControlImplBase handle in _car.interior.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true))
                {
                    if (handle.name == words[1])
                    {
                        handle.SetValue(number);
                        return $"{words[1]} = {handle.Value:F2}";
                    }
                }
                return "no such handle";
            case "ray"      :
            {
                // ray <from x y z> <to x y z>, in the vehicle's own frame: what the game's own pointer
                // would take hold of along that line - for checking that a handle can be reached
                if (words.Length != 7)
                    return "ray <from x y z> <to x y z>";
                float[] at = words.Skip(1).Select(word => float.Parse(word, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Vector3 from = _car.transform.TransformPoint(at[0], at[1], at[2]), to = _car.transform.TransformPoint(at[3], at[4], at[5]);
                DV.Interaction.GrabberRaycasterDV pointer = FindObjectOfType<DV.Interaction.GrabberRaycasterDV>();
                if (pointer == null)
                    return "no pointer";
                DV.Interaction.AGrabHandler taken = pointer.RaycastPassThrough(new Ray(from, (to - from).normalized));
                Collider met = pointer.CurrentlyHit.collider;
                return $"takes {(taken == null ? "nothing" : taken.name)}; last met {(met == null ? "nothing" : met.name + " (layer " + LayerMask.LayerToName(met.gameObject.layer) + ")")}, of {(to - from).magnitude:F2} m";
            }
            case "air"      : _air.bar = Mathf.Clamp(number, 0.0f, 10.0f); return $"pantograph air at {_air.bar:F1} bar";
            case "low"      : _low.charge = Mathf.Clamp01(number); return $"110 V battery at {_low.charge * 100.0f:F0} %";
            case "charge"   : power.battery_joules = Mathf.Clamp01(number) * eu07_sim.power_circuit.battery_kWh * 3.6e+6f; return $"battery at {power.battery_charge * 100.0f:F0} %";
            case "audio"    : if (_audio == null) return "no audio"; _audio.muted = number < 0.5f; return $"our sounds muted {_audio.muted}";
            case "board"    :
                // board: the player into cab A (board <metres>: that far to the right of the
                // locomotive, on the ground) - for listening runs with nobody at the keyboard.
                if (words.Length == 2)
                    PlayerManager.TeleportPlayer(_car.transform.TransformPoint(number, 0.3f, 0.0f), _car.transform.rotation, null, useRotation: true);
                else
                    PlayerManager.TeleportPlayer(_car.transform.TransformPoint(0.35f, 1.72f, 6.0f), _car.transform.rotation, _car.interior, useRotation: true);
                return "player moved";
            case "group"    : return _audio == null || words.Length != 2 ? "group <name>" : _audio.try_group(words[1]);
            case "handbrake": _car.brakeSystem.SetHandbrakePosition(number, forced: true); return $"handbrake {number}";
            case "paint"    :
            {
                // paint <theme>: what a can of the game's would do (DVRT, DVRT_New, Null, Relic, Relic_Rusty), with no sprayer
                // (paint <theme> inside: the inside's paint)
                DV.Customization.Paint.TrainCarPaint? outside = words.Length == 3 && words[2] == "inside" ? _car.PaintInterior : _car.PaintExterior;
                if (outside == null)
                    return "this car has no paint to change";
                if (words.Length < 2 || !DV.Customization.Paint.PaintTheme.TryLoad(words[1], out DV.Customization.Paint.PaintTheme theme) || theme == null)
                    return $"paint <theme> [inside]: it is {_car.PaintExterior?.CurrentTheme?.AssetName} outside, {_car.PaintInterior?.CurrentTheme?.AssetName} inside";
                bool takes = outside.IsSupported(theme);
                outside.CurrentTheme = theme;
                return $"paint {theme.AssetName} (offered for this car: {takes}); the body wears {_painted_body?.sharedMaterial?.name}";
            }
            case "fuse"     :
                if (words.Length != 3 || !_fuses.TryGetValue(words[1], out Fuse fuse))
                    return "no such fuse";
                fuse.ChangeState(number > 0.5f);
                return $"{words[1]} = {fuse.State}";
            case "scroll"   :
                // scroll <control object on the desk> <taps, signed>: the very call a key tap or a
                // tick of the mouse wheel makes, at a held key's rhythm - so that "do the throttle
                // keys step the wheel" can be answered with nobody at the keyboard.
                if (words.Length != 3)
                    return "scroll <control> <taps>";
                GameObject? desk = _car.loadedExternalInteractables;
                if (desk == null)
                    return "the desk is not loaded (the player is not near)";
                Transform? found = desk.GetComponentsInChildren<Transform>(true).FirstOrDefault(part => part.name == words[1]);
                IScrollable? scrollable = found != null ? found.GetComponent<IScrollable>() : null;
                if (scrollable == null)
                    return "no scrollable control " + words[1];
                StartCoroutine(taps(scrollable, (int) number));
                return $"scrolling {words[1]} by {(int) number}";
            case "stand"    :
            {
                // stand <right> <up> <forward> <yaw>: the player at that place in the locomotive's own
                // frame (metres from its middle, from the rail), turned that many degrees from its
                // nose - so that look.py --player can photograph anything, from anywhere.
                if (words.Length != 5)
                    return "stand <right> <up> <forward> <yaw>";
                float[] place = words.Skip(1).Select(word => float.Parse(word, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                bool inside = Mathf.Abs(place[0]) < 1.4f && Mathf.Abs(place[2]) < 7.3f && place[1] > 1.5f;
                PlayerManager.TeleportPlayer(_car.transform.TransformPoint(place[0], place[1], place[2]),
                    _car.transform.rotation * Quaternion.Euler(0.0f, place[3], 0.0f), inside ? _car.interior : null, useRotation: true);
                return "player moved";
            }
            case "set"      :
            {
                // set <control object on the desk> <0..1>: puts a control there outright (a door, say)
                if (words.Length != 3)
                    return "set <control> <0..1>";
                GameObject? parts = _car.loadedExternalInteractables;
                Transform? wanted = parts != null ? parts.GetComponentsInChildren<Transform>(true).FirstOrDefault(part => part.name == words[1]) : null;
                DV.CabControls.ControlImplBase? item = wanted != null ? wanted.GetComponent<DV.CabControls.ControlImplBase>() : null;
                if (item == null)
                    return "no control " + words[1];
                item.SetValue(number);
                return $"{words[1]} set to {number}, reads {item.Value:F2}";
            }
            case "port"     :
                if (words.Length != 3 || !_ports.TryGetValue(words[1], out Port port))
                    return "no such port";
                if (port.type == PortType.EXTERNAL_IN)
                    port.ExternalValueUpdate(number);
                else
                    port.Value = number;
                return $"{words[1]} = {number}";
            default: return "unknown: " + words[0];
        }
    }

    private static IEnumerator taps(IScrollable control, int count)
    {
        for (int index = 0; index < Mathf.Abs(count); ++index)
        {
            control.Scroll(count > 0 ? ScrollAction.ScrollUp : ScrollAction.ScrollDown);
            yield return new WaitForSeconds(index == 0 ? 0.25f : 0.125f);      // the keyboard's own delays
        }
        yield return new WaitForSeconds(0.3f);
        control.Scroll(ScrollAction.Release);
    }

    public string status()
    {
        eu07_sim.power_circuit? power = _power;
        if (power == null || _car == null)
            return "no power circuit";
        StringBuilder text = new();
        Vector3 at = _car.transform.position;
        text.AppendLine($"car {_car.ID} livery {_car.carLivery.id} speed_kmh {_car.GetForwardSpeed() * 3.6f:F2} derailed {_car.derailed} at {at.x:F0} {at.y:F0} {at.z:F0}");
        text.AppendLine($"wire_V {power.line_volts:F0} line_A {power.line_current:F1} WS {power.fast_breaker_closed} undervoltage {power.undervoltage}");
        text.AppendLine($"position {power.contactors.position} wheel {power.contactors.wanted} field {power.contactors.field} direction {power.contactors.direction} line_contactors {power.line_contactors_closed}");
        text.AppendLine($"ammeter_12 {power.ammeter_12:F0} ammeter_34 {power.ammeter_34:F0} torque_Nm {power.wheel_torque:F0} effort_kN {power.wheel_torque / 625.0f:F1} drag_kN {running_drag_newtons * 0.001f:F1}");
        text.AppendLine(_audio == null ? "audio none" : $"audio loops {_audio.loops} playing {_audio.playing} events {_audio.events} mixer {_audio.group} heard_dB {_audio.heard_dB:F1} peak_dB {_audio.peak_dB:F1} ours_muted {_audio.muted} culled {_audio.culled}");
        text.AppendLine($"plugging {power.plugging_stress:F2} flashovers {power.flashovers} drivetrain_health {power.health:F3}");
        text.AppendLine($"contactors_shown {_contactors.Count} shut {_contactors.Count(one => one.shut > 0.5f)}: {string.Join(" ", _contactors.Where(one => one.shut > 0.5f).Select(one => one.name).Take(40))}");
        text.AppendLine($"contactor_marks (soot/heat, of {_soot_stages.Length} painted stages; kept in the save {_ports.ContainsKey(marks_port(0))}): {string.Join(" ", Enumerable.Range(0, eu07_sim.contactor_marks.names.Length).Where(index => _marks.soot[index] > 0.02f || _marks.heat[index] > 0.02f).Select(index => $"{eu07_sim.contactor_marks.names[index]}:{_marks.soot[index]:F2}/{_marks.heat[index]:F2}"))}");
        text.AppendLine($"contactor_marks_kept {string.Join(" ", Enumerable.Range(0, eu07_sim.contactor_marks.numbers).Select(number => read(marks_port(number)).ToString("F0")))}");
        text.AppendLine($"grids_C {power.grid_celsius:F0} on_resistors {power.on_resistors} burnt {power.grids_burnt} earth_fault {power.earth_fault_tripped}");
        text.AppendLine($"wipers {_wipers.Count}: {string.Join(" ", _wiper_speeds)}");
        text.AppendLine($"resistors_C {power.resistor_celsius:F0} fan_by_current {power.fan_by_current:F2} fan_by_heat {power.fan_by_heat:F2} motors_C {power.motor_celsius:F1} motors_kW {power.motor_heat * 0.001f:F1}");
        text.AppendLine($"battery_last_trip amps {power.battery_trip_amps:F0} of them traction {power.battery_trip_traction_amps:F0}");
        text.AppendLine($"battery switch {power.battery_wanted} contactor {power.battery_contactor} tripped {power.battery_tripped} charge {power.battery_charge:F4} " +
            $"volts {power.battery_volts:F0} amps {power.battery_amps:F0} charging {power.battery_charging}");
        text.AppendLine($"low_V {_low.volts:F1} live {_low.live} switch {_low.battery_switch} supplied {_low.supplied} battery_A {_low.battery_amps:F2} charge {_low.charge:F4} load_W {_low.load_watts:F0} " +
            $"breakers {string.Concat(_low.breaker.Select(one => one ? '1' : '0'))} in_prefab {_has_breakers} cab_heating {power.cab_heating_on}");
        text.AppendLine($"fault_lamp code {_fault_code:F0} level {_fault_level} causes {_fault_causes} seen {_fault_seen[0]} {_fault_seen[1]} {_fault_seen[2]} {_fault_seen[3]} brake_warning {_brake_checker?.BrakeWarningState} sand {read("sand.NORMALIZED", 1.0f):F2} " +
            $"motors_breaker_out {(_fuses.TryGetValue(fuse_motors, out Fuse shown) && !shown.State)} working_motors {read("tm.WORKING_TRACTION_MOTORS", eu07_sim.motors):F0}");
        text.AppendLine($"hauled {power.hauled} train_protection_live {power.train_protection_live(_low.live)}");
        text.AppendLine($"vigilance box {(_vigilance != null ? "aboard" : "none")} fed {(_fuses.TryGetValue(fed_gadgets, out Fuse? feeds) && feeds != null ? feeds.State.ToString() : "no such fuse")} " +
            (_vigilance != null ? $"on {_vigilance.IsOn} counting {_vigilance.IsActive} count_s {_vigilance.currentTimer:F1} of {_vigilance.CurrentRegimeTimerLength:F0} " : "") +
            $"asks {_vigilance_asks} buzzes {_vigilance_buzzes} stops {_vigilance_stops} cock_open {read(port_vigilance_cock, 1.0f) > 0.5f} vents_the_pipe {_vigilance_vents} answered {_vigilance_answers} own_buzz_hushed {_vigilance_hushed.Count} gadgets_aboard {(_car.Customization != null ? _car.Customization.Customizers.Count : -1)}");
        text.AppendLine($"shp box {(_shp != null ? "aboard" : "none")} " +
            (_shp != null ? $"on {_shp.IsOn} moving {_shp.IsActive} count_s {_shp.currentTimer:F2} of {_shp.CurrentRegimeTimerLength:F0} warns_for {_shp.warningTime:F1} " : "") +
            $"asks {_shp_asks} lamp {_shp_lit} buzzes {_shp_buzzes} stops {_shp_stops} answered {_shp_answers} refused {_shp_refused}");
        text.AppendLine($"multiple_unit group {string.Join(" ", multiple_unit_group().Select(one => one._car?.ID))} cab_cut_out {_cab_cut_out} desk_held {_desk_held} " +
            $"leader {(_leader != null ? _leader._car?.ID : "-")} followers [{string.Join(" ", _followers.Select(one => one._car?.ID))}]");
        text.AppendLine($"said_to_the_train {_said_celsius:F0} C refusing_power {refusing_power} ({_refused_for:F1} s) observer {(_car.TryGetComponent(out DV.Simulation.Cars.MultipleUnitStateObserver own_says) ? own_says.CarTemperatureState.ToString() : "NONE")} foreign_units_say {_foreign_says}");
        text.AppendLine($"train_lights front {read(port_train_lights_front, -1.0f):F2} rear {read(port_train_lights_rear, -1.0f):F2} (-1: no such port) lamps_number front {read("headlightDecoder.FRONT_HEADLIGHTS_EXT_IN") * 255.0f:F0} rear {read("headlightDecoder.REAR_HEADLIGHTS_EXT_IN") * 255.0f:F0}");
        text.AppendLine($"desk_hold wanted {_desk_held} stands {(_hold_seen != null ? _hold_seen.MUSlaveBlock.ToString() : "?")} put_back {_holds_put_back} times; converter_knife_open {power.converter_knife_open} knife_arcs {power.knife_arcs} not_charging_for {_not_charging_for:F0} s");
        text.AppendLine($"foreign_led {_foreign_led} foreign_in_group {_foreign_in_group} throttle {read(port_throttle):F3} notching_by_itself stage {_auto.stage} wheel {_auto.wheel} field {_auto.field} " +
            $"a_notch_may_bring {_auto.may_bring:F0} series_only {_auto.series_only} shut_off {_auto.shut_off} may_take_power {power.may_take_power} feed_V {power.feed_volts:F0}");
        text.AppendLine($"hv_interlock_open {(_ports.ContainsKey(port_hv_interlock) && read(port_hv_interlock) < 0.5f)} isolators_out {_isolators_set} converter_knife_pulled {read(port_converter_knife) > 0.5f} " +
            $"instrument_light {read(port_instrument_light) > 0.5f} locks {(_car.SimController != null && _car.SimController.controlsBlocker != null ? _car.SimController.controlsBlocker.entries.Length : -1)}");
        text.AppendLine($"desk_paint skins {_skins.Length} as {_skins_painted_as?.AssetName} fittings {(_car.loadedExternalInteractables != null ? "real" : _car.loadedDummyExternalInteractables != null ? "stand-in" : "none")} " +
            $"inside_theme {_car.PaintInterior?.CurrentTheme?.AssetName} first_skin_wears {(_skins.Length > 0 && _skins[0] != null ? _skins[0].sharedMaterial?.name : "-")}");
        text.AppendLine($"frame_cocks control_air {_air.feed_open} pantograph_air {_air.pantographs_open} drain {_air.drain_open} main_reservoir {read(port_cock_main_res, 1.0f) > 0.5f}");
        text.AppendLine($"panto_air_bar {_air.bar:F2} cock_to_small {_air.cock_to_small} pump {_air.pump_running} can_raise {_air.can_raise} can_hold {_air.can_hold} breaker_air {power.pantograph_air_ok} " +
            $"room_lights {(_room_lights == null ? "none" : _room_lights.gameObject.activeSelf.ToString())}");
        text.AppendLine($"tripped overcurrent {power.overcurrent_tripped} earth {power.earth_fault_tripped} converter {power.converter_tripped} compressor {power.compressor_tripped}");
        text.AppendLine($"converter {power.converter_speed:F2} compressors {power.compressor_running[0]} {power.compressor_running[1]} reservoir_bar {power.main_reservoir_bar:F2} fans {power.fan_speed:F2} resistors_kW {power.resistor_heat * 0.001f:F0}");
        text.AppendLine($"desk {(_desk_v2 ? "v2" : "v1")} converter_switch {power.converter_switch} compressor_switch {power.compressor_switch[0]} heating_switch {power.heating_switch} heating_on {power.heating_on} slip_button {slip_button} high_start {power.high_start} line_off {power.line_contactor_cutout}");
        text.AppendLine($"brake_cylinder_bar {_car.brakeSystem.brakeCylinderPressure:F2} brake_pipe_bar {_car.brakeSystem.brakePipePressure:F2} handbrake {_car.brakeSystem.handbrakePosition:F2} " +
            $"air_rate {_car.brakeSystem.compressorProductionRate:F1} of {_compressor_rate:F0}");
        foreach (KeyValuePair<string, Fuse> fuse in _fuses)
            text.AppendLine($"  fuse {fuse.Key} = {fuse.Value.State}");
        foreach (KeyValuePair<string, Port> port in _ports)
            text.AppendLine($"  {port.Key} = {port.Value.Value:G5}");
        if (_problem.Length > 0)
            text.AppendLine("prefab lacks: " + _problem);
        return text.ToString();
    }

    private bool player_aboard => _car != null && PlayerManager.Car == _car;

    void FixedUpdate()
    {
        eu07_sim.power_circuit? power = _power;
        if (power == null || _car == null)
            return;

        power.line_volts         = read(port_supply_volts);
        // The game's pressures have the atmosphere in them: 1.0 is empty. And the game will not
        // hold more than 8 bar in a main reservoir (BrakeSystem.SimulateCompressor clamps it), which
        // is exactly where our pressure switch cuts out - and the figure it shows only creeps up
        // toward that, so the switch never saw 8 and the compressors ran on for good ("i can
        // still hear the compressor, even under wire and charged to 8bar", the author,
        // 2026-10-11). Within 0.03 bar of the game's limit is AT it.
        float reservoir = _car.brakeSystem != null ? _car.brakeSystem.mainReservoirPressure - 1.0f : 0.0f;
        power.main_reservoir_bar = reservoir >= game_reservoir_limit_bar - 0.03f ? Mathf.Max(reservoir, eu07_sim.power_circuit.compressor_cut_out_bar) : reservoir;

        float reverser = read(port_reverser);
        power.set_reverser(reverser > 0.5f ? 1 : reverser < -0.5f ? -1 : 0);
        power.set_controller(Mathf.RoundToInt(Mathf.Clamp01(read(port_throttle)) * (eu07_sim.controller_positions - 1)));
        if (_desk_v2)
        {
            // PLW and PLP are held: levels. The keypad's 0 holds PLW as well.
            // (and a follower's with its leader's: the leader's device empties the pipe of the whole train, and the
            // follower must not go on pulling against the brakes)
            power.line_contactor_cutout = _line_off_key || read(port_line_off) > 0.5f || _vigilance_vents || (_leader != null && _leader._vigilance_vents);
            power.hauled = read(port_hauled) > 0.5f;
            slip_button = read(port_slip_button) > 0.5f;
        }
        else
            read_desk_v1(power);
        float high = read(port_high_start, _high_switch);
        if ((high > 0.5f) != (_high_switch > 0.5f))
            power.high_start = high > 0.5f;
        _high_switch = high;
        if (_ports.ContainsKey(port_field_lever))
            _field_wanted = Mathf.RoundToInt(Mathf.Clamp01(read(port_field_lever)) * (eu07_sim.field_positions - 1));
        power.set_field_weakening(_field_wanted);
        if (_ports.ContainsKey(port_converter_switch))
            power.converter_switch = read(port_converter_switch) > 0.5f;
        if (_ports.ContainsKey(port_compressor_switch))
            power.compressor_switch[0] = power.compressor_switch[1] = read(port_compressor_switch) > 0.5f;
        if (_ports.ContainsKey(port_heating_switch))
            power.heating_switch = read(port_heating_switch) > 0.5f;
        multiple_unit_step();
        // what the others are told (see port_said_celsius)
        bool asked = power.contactors.direction != 0 && read(port_throttle) > 0.005f && !power.hauled;
        _refused_for = asked && !power.may_take_power ? _refused_for + Time.fixedDeltaTime : 0.0f;
        float said_celsius = read(port_motor_celsius, power.motor_celsius);
        if (refusing_power || power.grid_celsius >= eu07_sim.power_circuit.grids_hot_celsius)
            said_celsius = Mathf.Max(said_celsius, said_critical_celsius);
        else if (power.grid_celsius >= eu07_sim.power_circuit.grids_warm_celsius)
            said_celsius = Mathf.Max(said_celsius, said_warning_celsius);
        if (Mathf.Abs(said_celsius - _said_celsius) >= 0.5f)
        {
            _said_celsius = said_celsius;
            write(port_said_celsius, said_celsius);
        }
        if (_foreign_led)
        {
            // the throttle is HOW MUCH is wanted, not a notch: the wheel and the field lever are worked for it
            _auto.step(Time.fixedDeltaTime, Mathf.Clamp01(read(port_throttle)), power.pair_amps_12, power.pair_amps_34, power.feed_volts,
                       power.contactors.position, power.contactors.field, power.may_take_power, power.slip_detected, power.grid_celsius);
            power.set_controller(_auto.wheel);
            power.set_field_weakening(_auto.field);
        }
        else
        {
            _auto.reset();
            // cut out and led by nobody: the wheel is not this desk's to turn, and nobody else turns it - no power,
            // wherever the wheel was left when the switch went up
            if (_cab_cut_out && _leader == null)
                power.set_controller(0);
        }
        low_voltage_step(power, Time.fixedDeltaTime);
        // The game's fuse for the traction motors - its switch on the fuse plate, and what the
        // game throws out when the motors overheat - holds the line contactors out. It did
        // nothing: the fuse was his motors', which are off the wheels.
        if (_fuses.TryGetValue(fuse_motors, out Fuse motors_fuse) && !motors_fuse.State)
            power.line_contactor_cutout = true;
        if (!_motor_heat_read && _ports.ContainsKey(port_motor_celsius))
        {
            power.motor_celsius = Mathf.Max(eu07_sim.power_circuit.ambient_celsius, read(port_motor_celsius));     // as the save had it
            _motor_heat_read = true;
        }
        // The wipers are air motors: each goes as its valve stands, and not at all under 2 bar.
        for (int index = 0; index < _wipers.Count; ++index)
        {
            (DV.Rain.WiperController wiper, string control) = _wipers[index];
            if (wiper == null)
                continue;
            int speed = power.main_reservoir_bar < 2.0f ? 0 : Mathf.RoundToInt(Mathf.Clamp01(read(control)) * (wiper.speeds.Length - 1));
            if (speed != _wiper_speeds[index])
            {
                wiper.SetSpeed(speed);
                _wiper_speeds[index] = speed;
            }
        }
        if (_ports.ContainsKey(port_battery_switch))
            power.battery_wanted = read(port_battery_switch) > 0.5f;
        if (_battery_kept < 0.0f && _ports.ContainsKey(control_battery))
        {
            _battery_kept = Mathf.Clamp01(read(control_battery));
            power.battery_joules = _battery_kept * eu07_sim.power_circuit.battery_kWh * 3.6e+6f;
        }

        // The presses, from the v2 buttons and the keypad alike. OFF after ON, so that both
        // at once leave the breaker open.
        if (take(ref _want_close))
            power.close_fast_breaker();
        if (take(ref _want_open))
            power.open_fast_breaker();
        if (take(ref _want_reset))
        {
            // said in the log, with what stood in its way: a press that did nothing left no trace
            // ("i cant reset motor ground fault when on battery", the author, 2026-10-11)
            bool was = power.overcurrent_tripped || power.earth_fault_tripped;
            bool clear = power.reset_relays();
            if (was)
                Main.release_log($"EU07 {_car.ID}: the relays' reset button: {(clear ? "reset" : "NOT reset")} (reverser {power.contactors.direction}, wheel {power.contactors.wanted}, " +
                    $"contactors {power.contactors.position}; overload {power.overcurrent_tripped}, earth fault {power.earth_fault_tripped})");
        }
        if (take(ref _want_aux_reset))
            power.reset_auxiliary_relays();

        power.simulate(Time.fixedDeltaTime);

        // THE AIR. The railcar's own compressor makes it, and that one knows only the bus bar:
        // it ran whenever there were volts on the wire, whatever our switch, relay and pressure
        // switch said, and not at all on the battery, where ours could be heard working and
        // the reservoir stayed where it was ("the main res isnt recharging with the compressor
        // audibly working", the author, 2026-10-11, on the battery with a train on). The brake
        // system takes its filling rate from a field the game's compressor writes only when
        // its own output changes; so the rate is written here every step, and is OURS: the
        // game's figure for the whole, half of it for each of the two compressors that runs.
        if (_car.brakeSystem != null)
            _car.brakeSystem.compressorProductionRate = _compressor_rate * 0.5f * ((power.compressor_running[0] ? 1.0f : 0.0f) + (power.compressor_running[1] ? 1.0f : 0.0f));

        // Damage the simulation has run up (plugging, a flashover) goes on the game's electrical
        // drivetrain pool, scaled by the player's damage setting as the game's own is; and that
        // pool's health comes back, for what it does to the motors and the earth-fault relay.
        if (_damage == null)
            _damage = _car.GetComponent<DV.Damage.DamageController>();
        float damage = power.take_damage();
        if (_damage != null && _damage.electricalPT != null)
        {
            if (damage > 0.0f)
                _damage.electricalPT.ApplyDamage(damage * DV.Globals.G.GameParams.DamageSensitivityModifier);
            power.health = _damage.electricalPT.HealthPercentage;
        }
        mark_contactors(power, damage);

        // Running resistance. The game gives a vehicle rolling resistance only (2 N a kN of weight:
        // 1.6 kN for this one, whatever the speed), and alone she coasted from 108 km/h losing
        // 0.015-0.035 m/s2 ("surprisingly, solo she survives a long way", the author,
        // 2026-10-10; debug/audio_trace.csv). A locomotive of this size and bluntness loses
        // about 0.08 at 100 km/h: the air (some 4 kN there, with the square of the speed) and
        // the flanges, bearings and gears (about 1 kN, with the speed) - the B and C of a
        // Davis formula for a four-axle 83 t electric locomotive. Put on as torque against the
        // wheels' turning, which is where the game takes force from this vehicle.
        float forward = _car.GetForwardSpeed(), kmh = Mathf.Abs(forward) * 3.6f;
        running_drag_newtons = (drag_newtons_per_kmh * kmh + drag_newtons_per_kmh2 * kmh * kmh) * Mathf.Clamp01(kmh / 2.0f);
        write(port_torque       , power.wheel_torque - Mathf.Sign(forward) * running_drag_newtons * wheel_radius_metres);
        write(port_line_current , power.line_current);
        // The lights: the game shows ONE setup an end, by number; the desk has a switch a lamp.
        // The vehicle has a setup for every way six switches can stand (eu07_build.cs,
        // make_lights): left 1, top 2, right 4, red left 8, red right 16, and not dimmed 32, 64,
        // 128 for the left, top and right white.
        // LED BY A FOREIGN DESK, her lamps are that desk's to set ("the MU light controller in our loco doesnt listen
        // to given commands", the author, 2026-10-12). The game sends its selector's place, one of six -
        // 0 and 0.2 red, 0.4 off, 0.6, 0.8 and 1 white, weak to full - for each end, to two ports that
        // hold nothing else (eu07_build.cs, trainLightsFront); here that is made lamps: both reds; none;
        // the two lower whites dimmed; all three dimmed; all three full. Her own switches are then
        // passed over, and are hers again when the leader is gone.
        foreach ((string cab, string control) in new[] { ("A", "headlightDecoder.FRONT_HEADLIGHTS_EXT_IN"), ("B", "headlightDecoder.REAR_HEADLIGHTS_EXT_IN") })
        {
            if (!_ports.ContainsKey($"lightLeft{cab}.EXT_IN"))
                continue;
            string said = cab == "A" ? port_train_lights_front : port_train_lights_rear;
            if (_foreign_led && _ports.ContainsKey(said))
            {
                int place = Mathf.RoundToInt(Mathf.Clamp01(read(said)) * 5.0f);
                float lamps = (place <= 1 ? 8 + 16 : place == 2 ? 0 : place == 3 ? 1 + 4 : place == 4 ? 1 + 2 + 4 : 1 + 2 + 4 + 32 + 64 + 128) / 255.0f;
                if (Mathf.Abs(read(control) - lamps) > 0.001f)
                    write(control, lamps);
                continue;
            }
            int setup = 0, bit = 1;
            foreach (string name in new[] { "lightLeft", "lightTop", "lightRight", "redLeft", "redRight" })
            {
                if (read($"{name}{cab}.EXT_IN") > 0.5f)
                    setup |= bit;
                bit <<= 1;
            }
            foreach (string name in new[] { "lightDimLeft", "lightDimTop", "lightDimRight" })
            {
                if (read($"{name}{cab}.EXT_IN") <= 0.5f)
                    setup |= bit;
                bit <<= 1;
            }
            float wanted = setup / 255.0f;
            if (Mathf.Abs(read(control) - wanted) > 0.001f)
                write(control, wanted);
        }
        // The speed recorder ("wanna do the Km counter and inbuilt clock next?", the author,
        // 2026-10-11). Its clock goes by the GAME's time, asked of the game's own clocks'
        // keeper. Its counter counts what this locomotive has run: six drums, the lowest for
        // hundreds of metres and turning all the while, each of the others standing on its
        // figure and turning on to the next while every drum under it goes from 9 to 0. The
        // kilometres are written to a control the game saves, and read back from it once.
        DV.TimeKeeping.WorldClockController keeper = DV.Utils.SingletonBehaviour<DV.TimeKeeping.WorldClockController>.Instance;
        if (keeper != null)
        {
            (bool known, float hour_angle, float minute_angle, DateTime _) = keeper.GetCurrentAnglesAndTimeOfDay();
            if (known)
            {
                write(port_clock_hours, hour_angle / 30.0f);
                write(port_clock_minutes, minute_angle / 6.0f);
            }
        }
        if (_metres_run < 0.0)
            _metres_run = Math.Max(0.0, read(control_odometer)) * 1000.0;
        if (kmh > 0.05f)
        {
            _metres_run += Mathf.Abs(forward) * Time.fixedDeltaTime;
            write(control_odometer, (float) (_metres_run / 1000.0));
        }
        double hundreds = _metres_run / 100.0;
        for (int drum = 0; drum < 6; ++drum)
        {
            double unit = Math.Pow(10.0, drum), stands_on = Math.Floor(hundreds / unit) % 10.0;
            double turning = drum == 0 ? hundreds % 1.0 : Math.Max(0.0, hundreds % unit - (unit - 1.0));
            write(port_odometer + drum, (float) (stands_on + turning));
        }
        write(port_ammeter_12   , power.ammeter_12);
        write(port_ammeter_34   , power.ammeter_34);
        write(port_position     , power.contactors.position);
        write(port_field        , power.contactors.field);
        write(port_fan_speed    , power.fan_speed);
        write(port_converter    , power.converter_speed);
        write(port_resistor_heat, power.resistor_heat);
        // NOT the motors' losses any more but what makes the game's temperature for them follow
        // ours, half a second behind: so much heat as the difference is worth.
        write(port_motor_heat   , (power.motor_celsius - read(port_motor_celsius, power.motor_celsius)) * _reservoir_capacity / 0.5f);
        light(lamp_fast_breaker , power.fast_breaker_closed);
        light(lamp_line_open    , !power.line_contactors_closed);
        light(lamp_resistors    , power.on_resistors);
        light(lamp_high_start   , power.high_start_active);
        light(lamp_overcurrent  , power.overcurrent_tripped);
        light(lamp_earth_fault  , power.earth_fault_tripped);
        light(lamp_slip         , power.slip_detected);
        light(lamp_fans         , power.fans_failed);
        light(lamp_auxiliary    , power.converter_tripped || power.heating_tripped);
        light(lamp_compressor   , power.compressor_tripped);
        light(lamp_heating      , power.heating_on);
        // ARCS. "do the arcing, but if i remember correctly, the game already does arcing ... tm
        // arcing is from the main game" (the author, 2026-10-11): the game shows a dead traction
        // motor sparking, and that effect is what is borrowed - the vehicle's own
        // DeadTractionMotorsController carries the sparks, with their sound. They stood at the
        // motors for an hour: "maybe lets relocate em to the cabinet, since there are no
        // contactors on the motors visibly". So when our protection breaks a heavy current
        // (the circuit counts those) the sparks are in a HIGH-VOLTAGE CABINET, behind the two
        // windows next to a cab where such arcs are seen from the lineside (cabinet_arc), with
        // a flickering light of ours, the longer the more amperes were broken. A commutator's flashover IS at a motor, and
        // stays there: one motor, three seconds.
        if (_arcs_seen < 0)
        {
            _arcs_seen = power.arcs;
            _flashovers_seen = power.flashovers;
        }
        if (power.arcs != _arcs_seen)
        {
            _arcs_seen = power.arcs;
            float much = Mathf.Clamp01((power.arc_amps - eu07_sim.power_circuit.arc_from_amps) / 600.0f);
            // The game's effect is a looping one whose bright flash only comes 0.3 s in, and whose
            // sparks fly from 1.5 m either side of the axle, at wheel height: 0.3 to 1.2 s of it
            // was over before it could be seen ("didnt see any. where do they fire from?").
            // "off it pops. but probably too fast. id keep the instant flash" (the same, seeing it):
            // it lasted 1.2 to 2.8 s. Now 2.5 to 5 s, the flash at its start as it was.
            // A FLASH, not an arc that burns: "the arcing shouldnt take couple of seconds. its an
            // instant flash once the contactors open, not a long welding like arc" (the author,
            // 2026-10-11, of one I had drawn out to five seconds). A quarter to under half a second.
            cabinet_arc(0.25f + 0.20f * much, much, power.arc_at_breaker, $"{power.arc_amps:F0} A broken");
        }
        if (power.flashovers != _flashovers_seen)
        {
            _flashovers_seen = power.flashovers;
            motor_sparks(3.0f, false, "a flashover");
        }
        light(lamp_battery      , power.battery_contactor);
        float least_charge = power.battery_charge;
        foreach (eu07_driver follower in _followers)
            least_charge = Mathf.Min(least_charge, follower != null && follower._power != null ? follower._power.battery_charge : 1.0f);
        write(port_battery_charge, least_charge * 100.0f);
        if (_battery_kept >= 0.0f && Mathf.Abs(power.battery_charge - _battery_kept) > 0.0005f)
        {
            _battery_kept = power.battery_charge;
            write(control_battery, _battery_kept);
        }
    }

    // The v1 desk, as it was: switches all.
    private void read_desk_v1(eu07_sim.power_circuit power)
    {
        // The desk. The fast breaker's switch is read for its LEVEL: while it is on, a
        // breaker that has fallen out goes back in by itself as soon as it may (volts back
        // on the wire, no relay holding it). The real desk has two impulse buttons, and a
        // breaker that fell out stays out; but a switch that sits at "on" over an open
        // breaker and does nothing cost the author a minute of flipping the wrong ones
        // (2026-10-10). The other switches are read for their edges.
        float breaker = read(port_fast_breaker, _breaker_switch), reset = read(port_relay_reset, _reset_switch);
        if (_ports.ContainsKey(port_fast_breaker))
        {
            if (breaker > 0.5f && !power.fast_breaker_closed)
                power.close_fast_breaker();
            else if (breaker <= 0.5f && power.fast_breaker_closed && _breaker_switch > 0.5f)
                power.open_fast_breaker();
        }
        if ((reset > 0.5f) != (_reset_switch > 0.5f))
        {
            power.reset_relays();
            power.reset_auxiliary_relays();
        }
        _breaker_switch = breaker;
        _reset_switch   = reset;
        // PLW is a push-button and this is a toggle. Read as a level it had a position that
        // left the locomotive dead, and the author found it three times (2026-10-10). So
        // each flip, either way, is one press: the contactors fall out, and come back
        // when the wheel has been to 0, as after the real button.
        float line_off = read(port_line_off, _line_off_switch);
        if (_line_off_switch >= 0.0f && (line_off > 0.5f) != (_line_off_switch > 0.5f))
            _line_off_press = 0.3f;
        _line_off_switch = line_off;
        _line_off_press  = Mathf.Max(0.0f, _line_off_press - Time.fixedDeltaTime);
        power.line_contactor_cutout = _line_off_key || _line_off_press > 0.0f;
    }

    // Where the arcs are, in the vehicle (x right, y over the rail, z toward cab A). "ive seen arcing
    // from locos in the first two windows. that general area are the contactors, so they should
    // be there" (the author, 2026-10-11, with a picture of the side): behind the two windows next
    // to a cab stands that end's high-voltage cabinet, and the model's contactors on its lower
    // shelf come at 0.27 m steps along it (model/eu07_blockout.py) - one of them behind each
    // window. So the line contactors' arcs are at those two, in cab A's cabinet; the main
    // breaker's at the breaker itself, at the head of cab B's cabinet, and at the contactor
    // behind the window next to it.
    private static readonly Vector3[] contactor_chutes = [new(-0.575f, 2.95f, 2.29f), new(-0.575f, 2.95f, 4.72f)];
    private static readonly Vector3[] breaker_chutes   = [new(0.57f, 3.00f, -1.75f), new(0.575f, 2.95f, -2.44f)];

    // An arc in a high-voltage cabinet: the game's sparks, its two emitters (made for a bogie, a
    // metre and a half either side of the middle) put at the two places and made smaller, and at
    // each a blue-white light of ours that flickers while they last.
    private void cabinet_arc(float seconds, float much, bool breaker, string why)
    {
        if (_car == null)
            return;
        Vector3[] at = breaker ? breaker_chutes : contactor_chutes;
        // (the line contactors' own chutes, when the cage's fittings are there to say where they stand)
        Vector3[] line = _contactors.Where(one => (one.name == "SL1" || one.name == "SL3") && one.chute != null)
                                    .Select(one => _car.transform.InverseTransformPoint(one.chute!.bounds.center)).ToArray();
        if (!breaker && line.Length == 2)
            at = line;
        DV.Simulation.Controllers.DeadTractionMotorsController? dead = _car.GetComponentInChildren<DV.Simulation.Controllers.DeadTractionMotorsController>(true);
        GameObject place = new("EU07 arc");
        place.transform.SetParent(_car.transform, false);
        place.transform.localPosition = at[0];
        int emitters = 0;
        if (dead != null && dead.sparksPrefab != null)
        {
            GameObject sparks = Instantiate(dead.sparksPrefab, place.transform);
            sparks.transform.localPosition = Vector3.zero;
            sparks.transform.localRotation = Quaternion.identity;
            foreach (Transform emitter in sparks.transform)
            {
                if (!emitter.name.StartsWith("ElectricSparks"))        // its two emitters; the third child is its sound
                    continue;
                emitter.position = _car.transform.TransformPoint(at[Math.Min(emitters, at.Length - 1)]);
                emitter.localScale = Vector3.one * 0.6f;
                ++emitters;
            }
            sparks.SetActive(true);
        }
        foreach (Vector3 spot in at)
        {
            GameObject lamp = new("glow");
            lamp.transform.SetParent(place.transform, false);
            lamp.transform.position = _car.transform.TransformPoint(spot);
            Light glow = lamp.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = new Color(0.62f, 0.78f, 1.0f);
            glow.range = 4.0f;
            glow.intensity = 0.0f;
            glow.shadows = LightShadows.None;
            StartCoroutine(flash(glow, seconds, 5.0f + 6.0f * much));
        }
        // the game's sparks begin a moment after they are made: left a little longer than the light, for the spray to be seen
        Destroy(place, seconds + 0.45f);
        Main.release_log($"EU07 {_car.ID}: an arc ({why}) in the {(breaker ? "main breaker" : "line contactors")}: a flash of {seconds:F2} s at two places, the game's sparks ({emitters} emitters) after it");
    }

    // One flash: at its brightest at once, held for a twentieth of a second, then gone smoothly
    // by the end of its time. No flicker - that was what made it a welder's arc.
    private static IEnumerator flash(Light glow, float seconds, float strength)
    {
        for (float time = 0.0f; time < seconds && glow != null; time += Time.deltaTime)
        {
            float fade = Mathf.Clamp01((time - 0.05f) / Mathf.Max(seconds - 0.05f, 0.01f));
            glow.intensity = strength * (1.0f - fade) * (1.0f - fade);
            yield return null;
        }
        if (glow != null)
            glow.intensity = 0.0f;
    }

    // The game's own sparks at the traction motors, for so many seconds: at every motor, or at one.
    private void motor_sparks(float seconds, bool every, string why)
    {
        DV.Simulation.Controllers.DeadTractionMotorsController? dead = _car != null ? _car.GetComponentInChildren<DV.Simulation.Controllers.DeadTractionMotorsController>(true) : null;
        LocoSim.Implementations.Wheels.PoweredWheelsManager? wheels = _car != null && _car.SimController != null ? _car.SimController.poweredWheels : null;
        if (dead == null || dead.sparksPrefab == null || wheels == null || wheels.poweredWheels == null || wheels.poweredWheels.Length == 0)
        {
            Main.release_log($"EU07 {_car?.ID}: an arc ({why}), and no sparks of the game's to show it with");
            return;
        }
        int only = UnityEngine.Random.Range(0, wheels.poweredWheels.Length), shown = 0;
        for (int index = 0; index < wheels.poweredWheels.Length; ++index)
        {
            LocoSim.Implementations.Wheels.PoweredWheel wheel = wheels.poweredWheels[index];
            if (wheel == null || wheel.wheelTransform == null || (!every && index != only))
                continue;
            GameObject made = Instantiate(dead.sparksPrefab, wheel.wheelTransform.position, wheels.transform.rotation, dead.transform);
            made.SetActive(true);
            Destroy(made, seconds);
            ++shown;
        }
        Main.release_log($"EU07 {_car?.ID}: an arc ({why}): the game's sparks at {shown} motor(s) for {seconds:F1} s");
    }

    // THE DOORS' HANDLES ("a nice detail would be to see the handle move down as part of the
    // opening and closing animation", the author, 2026-10-11; their sound is the game's own,
    // put on the hinge by the build). The doors come and go with the cab's fittings, so they are looked for
    // whenever those change: every object called Door... with a leaf C_Door... and, on the leaf,
    // the turning parts the build calls DoorHandle. A handle is down while the latch is in the
    // frame's way - the leaf between 1 and 9 degrees open - and up otherwise: it dips as the
    // door is pulled open and again as it comes home.
    // THE OUTSIDE DOORS WEAR THE BODY'S PAINT. The game's paint cans repaint what is in the
    // vehicle's own prefab; the hinged doors come with the cab's fittings, in another, where
    // the cans cannot be told of them. So whenever the body's painted material is not the one
    // the doors were last given - a can was used, or the doors have just come - they get it.
    private const string painted_parts = "Parts_outside", painted_material = "EU07_paint_eu07_ext_b";
    private Renderer? _painted_body;
    private Renderer[] _painted_doors = Array.Empty<Renderer>();
    private Material? _doors_painted_as;
    // THE DESK'S PAINTED SURFACES WEAR THE INSIDE'S PAINT, likewise: the build's "Skin..." pieces are
    // given what the vehicle's inside theme has in place of the material they were built with.
    private Renderer[] _skins = Array.Empty<Renderer>();
    private Material[] _skins_built = Array.Empty<Material>();
    private DV.Customization.Paint.PaintTheme? _skins_painted_as;

    private void watch_doors()
    {
        // (the cab's fittings as the game has them just now: the real ones, or - with nobody near - its stand-ins for them)
        GameObject? parts = _car == null ? null : _car.loadedExternalInteractables != null ? _car.loadedExternalInteractables : _car.loadedDummyExternalInteractables;
        if (parts != _doors_of)
        {
            _painted_doors = parts == null ? Array.Empty<Renderer>()
                : parts.GetComponentsInChildren<Renderer>(true).Where(part => part.sharedMaterial != null && part.sharedMaterial.name.StartsWith(painted_material)).ToArray();
            _doors_painted_as = null;
            _skins = parts == null ? Array.Empty<Renderer>() : parts.GetComponentsInChildren<Renderer>(true).Where(part => part.name.StartsWith("Skin") && part.sharedMaterial != null).ToArray();
            _skins_built = _skins.Select(part => part.sharedMaterial).ToArray();
            _skins_painted_as = null;
        }
        DV.Customization.Paint.TrainCarPaint? inside_paint = _car != null ? _car.PaintInterior : null;
        if (inside_paint != null && inside_paint.CurrentTheme != null && inside_paint.CurrentTheme != _skins_painted_as && _skins.Length > 0)
        {
            _skins_painted_as = inside_paint.CurrentTheme;
            for (int index = 0; index < _skins.Length; ++index)
            {
                if (_skins[index] != null)
                    _skins[index].sharedMaterial = _skins_painted_as.TryGetSubstitute(_skins_built[index], out DV.Customization.Paint.PaintTheme.Substitution swap) && swap.substitute != null ? swap.substitute : _skins_built[index];
            }
        }
        if (_painted_body == null && _car != null)
            _painted_body = _car.GetComponentsInChildren<Renderer>(true).FirstOrDefault(part => part.name == painted_parts);
        if (_painted_body != null && _painted_body.sharedMaterial != _doors_painted_as && _painted_doors.Length > 0)
        {
            _doors_painted_as = _painted_body.sharedMaterial;
            foreach (Renderer door_part in _painted_doors)
            {
                if (door_part != null)
                    door_part.sharedMaterial = _doors_painted_as;
            }
        }
        if (parts != _doors_of)
        {
            _doors_of = parts;
            _doors.Clear();
            if (parts != null)
            {
                Transform[] every = parts.GetComponentsInChildren<Transform>(true);
                foreach (Transform frame in every)
                {
                    Transform? leaf = frame.name.StartsWith("Door") ? every.FirstOrDefault(part => part.name == "C_" + frame.name) : null;
                    if (leaf == null)
                        continue;
                    door_watch door = new() { name = frame.name, frame = frame, leaf = leaf };
                    door.handles = leaf.GetComponentsInChildren<Transform>(true).Where(part => part.name == "DoorHandle").ToArray();
                    door.rest = door.handles.Select(handle => handle.localRotation).ToArray();
                    // a lever lying toward its own +x is lowered by turning it backward about z; one turned over to lie the other way, forward
                    door.lowers = door.handles.Select(handle => handle.Find("Lever") is Transform lever && lever.localScale.x < 0.0f ? 1.0f : -1.0f).ToArray();
                    _doors.Add(door);
                }
            }
        }
        foreach (door_watch door in _doors)
        {
            if (door.frame == null || door.leaf == null)
                continue;
            float open = Quaternion.Angle(door.frame.rotation, door.leaf.rotation);
            float press = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(0.3f, 2.5f, open)) * (1.0f - Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(4.0f, 9.0f, open)));
            for (int index = 0; index < door.handles.Length; ++index)
            {
                if (door.handles[index] != null)
                    door.handles[index].localRotation = door.rest[index] * Quaternion.AngleAxis(door.lowers[index] * handle_press_degrees * press, Vector3.forward);
            }
        }
    }

    private static bool key_down(UnityModManagerNet.KeyBinding key)
    {
        if (key == null || key.keyCode == KeyCode.None || !Input.GetKeyDown(key.keyCode))
            return false;
        bool control = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        return ((key.modifiers & 1) == 0 || control) && ((key.modifiers & 2) == 0 || shift) && ((key.modifiers & 4) == 0 || alt);
    }

    // THE CAB STAYS ON THE BODY ("locos and cars for some reason oscillate back and fourth, detaching the body from
    // the functioning model", the author, 2026-10-14, with the cab's cocks standing off their pipes). Read in the
    // game: a car's interior - the cab, our desk, cupboards and cocks with it - is not a child of the car; the game
    // puts it where the car is every LateUpdate, but only while it counts the car as MOVING, and a car that has
    // stayed within 2 mm for a second it counts as stationary and stops looking at. Measured the same day (debug
    // hook "watch", a braked train of ten loaded tank cars standing): every car rocked, 0.4 to 4 times a second,
    // and the train crept 14 cm in two minutes - no car still long enough for the game to freeze the train, each
    // still long enough, now and then, to be counted stationary. This locomotive was counted stationary 88 % of
    // the time, its interior left up to 17 mm behind. So on one of ours the interior is put back on the body every
    // frame the game is not doing it itself. (Why a braked train rocks at all is the game's physics, and is not
    // touched here.)
    private TrainCarInteriorPhysics? _interior_physics;

    void LateUpdate()
    {
        if (_car == null || (_car.loadedInterior == null && _car.loadedExternalInteractables == null))
            return;
        if (_interior_physics == null)
            _interior_physics = _car.GetComponent<TrainCarInteriorPhysics>();
        if (_interior_physics != null && !_interior_physics.enabled)
            _interior_physics.SyncPosition();
    }

    void Update()
    {
        eu07_sim.power_circuit? power = _power;
        if (power != null && _car != null)
            watch_doors();
        if (power != null && _car != null)
            watch_contactors(power);
        if (power != null && _car != null)
        {
            // the sounds, whether or not anybody is aboard
            _audio ??= new eu07_audio(_car, System.IO.Path.Combine(eu07_mod.folder, "audio"));
            // The horn: how far its lever stands from the middle (the railcar's own horn gives
            // that, less its fuse), which way (under a half the low tone, over it the high), and
            // air to blow it - it fades out under 3 bar. Not in the first seconds: the
            // control comes up from 0 to its middle, which would be a blast at every load.
            _since_attach += Time.deltaTime;
            // And not for the first sixth of the lever's travel: let go, it comes to rest a hair
            // off its middle (0.509 seen), which was a horn sounding faintly for good.
            float lever = read("hornControl.CONTROL", 0.5f), travel = Mathf.Clamp01((read("horn.HORN") - 0.15f) / 0.85f);
            float blown = _since_attach > 3.0f ? travel * Mathf.Clamp01(power.main_reservoir_bar / 3.0f) : 0.0f;
            _audio.horn_low  = lever < 0.5f ? blown : 0.0f;
            _audio.horn_high = lever > 0.5f ? blown : 0.0f;
            _audio.sand      = read("sander.SAND_FLOW");
            _audio.panto_wanted[0] = read("pantographSwitch.EXT_IN") > 0.5f;
            _audio.panto_wanted[1] = read("pantograph2Switch.EXT_IN") > 0.5f;
            _audio.update(power, _car, read(port_supply_volts), Time.deltaTime,
                [read("pantograph.PANTOGRAPH_IN_CONTACT") > 0.5f, read("pantograph2.PANTOGRAPH_IN_CONTACT") > 0.5f],
                [read("pantograph.PANTOGRAPH_RAISE_NORMALIZED"), read("pantograph2.PANTOGRAPH_RAISE_NORMALIZED")]);
        }
        // the vigilance key (eu07_mod.settings: the mod manager's page): the yellow button's own press, for whoever is aboard
        // (Not the mod manager's own KeyBinding.Down(): that wants the modifier keys to stand EXACTLY as the binding
        // says, so a binding of Left Shift alone - the author's - can never be down: Shift is held, and the binding
        // says no Shift. "our vigillance ack button doesnt actually act when key pressed". Here the key going down
        // is the press, with whatever modifiers the binding names held, and any others let be.)
        // ("You know whats missing? 2 sounds. the button press sound when i press the keybind ..." - and, of the
        // sound of our own it was first given: "Make it use the stock button click sound we use for the other
        // buttons.") The key PRESSES THE BUTTON: the game's own Use() of the yellow button on the desk, which is
        // what a hand's click is - it goes down and comes back, it makes the game's click, and its port rises,
        // which is the answer (on_press). The flag is set as well, for a desk that is not there to be pressed.
        // (ONE Use() left it standing in: "sounds, but when i click it toggles. it needs to be momentary". The desk's
        // buttons are held buttons - the game's mouse gives one Use() as it goes down on one and another as it lets
        // go (ButtonNonVR: Pressed, Released). So does the key: in while it is held, out when it is let go, or the
        // driver is aboard no longer.)
        if (power != null && player_aboard && key_down(eu07_mod.settings.vigilance))
        {
            Interlocked.Exchange(ref _want_vigilance, 1);
            if (_vigilance_button == null && _car != null && _car.loadedExternalInteractables != null)
                _vigilance_button = _car.loadedExternalInteractables.GetComponentsInChildren<DV.CabControls.ButtonBase>(true).FirstOrDefault(one => one.name == "C_Vigilance");
            if (_vigilance_button != null && !_vigilance_button.IsOn)
            {
                _vigilance_button.Use();
                _vigilance_key_holds = true;
            }
        }
        if (_vigilance_key_holds && (!player_aboard || !Input.GetKey(eu07_mod.settings.vigilance.keyCode)))
        {
            if (_vigilance_button != null && _vigilance_button.IsOn)
                _vigilance_button.Use();
            _vigilance_key_holds = false;
        }
        if (power == null || !player_aboard || !_developer)
            return;
        if (Input.GetKeyDown(KeyCode.Keypad7))
            Interlocked.Exchange(ref _want_close, 1);
        if (Input.GetKeyDown(KeyCode.Keypad1))
            Interlocked.Exchange(ref _want_open, 1);
        if (Input.GetKeyDown(KeyCode.Keypad5))
        {
            Interlocked.Exchange(ref _want_reset, 1);
            Interlocked.Exchange(ref _want_aux_reset, 1);
        }
        _line_off_key = Input.GetKey(KeyCode.Keypad0);
        if (Input.GetKeyDown(KeyCode.KeypadPlus))
            _field_wanted = Math.Min(_field_wanted + 1, eu07_sim.field_positions - 1);
        if (Input.GetKeyDown(KeyCode.KeypadMinus))
            _field_wanted = Math.Max(_field_wanted - 1, 0);
        if (Input.GetKeyDown(KeyCode.Keypad9))
            power.high_start = !power.high_start;
        if (Input.GetKeyDown(KeyCode.Keypad3))
            _panel = !_panel;
    }

    void OnGUI()
    {
        eu07_sim.power_circuit? power = _power;
        if (power == null || !_panel || !_developer || !player_aboard)
            return;

        static string lamp(bool lit, string name) => lit ? $"<color=yellow><b>[{name}]</b></color> " : $"<color=grey>[{name}]</color> ";

        StringBuilder text = new();
        text.AppendLine($"<b>EU07</b>   wire {power.line_volts:F0} V   line {power.line_current:F0} A");
        text.AppendLine($"position <b>{power.contactors.position}</b> (wheel {power.contactors.wanted})   field {power.contactors.field} (lever {_field_wanted})   " +
            $"{(power.contactors.direction > 0 ? "FORWARD" : power.contactors.direction < 0 ? "REVERSE" : "neutral")}");
        text.AppendLine($"motors 1-2  <b>{power.ammeter_12,5:F0} A</b>     motors 3-4  <b>{power.ammeter_34,5:F0} A</b>     relays at {eu07_sim.power_circuit.overcurrent_normal:F0}     " +
            (power.motor_celsius >= 100.0f ? $"<color=yellow><b>motors {power.motor_celsius:F0} C</b></color>" : $"motors {power.motor_celsius:F0} C"));
        if (_fuses.TryGetValue(fuse_motors, out Fuse motors_fuse) && !motors_fuse.State)
            text.AppendLine("<color=yellow><b>[the \"Control\" breaker is out (the game's motor fuse): no traction until it is put back in, under the flap in the assistant's desk]</b></color>");
        if (_has_breakers)
        {
            if (!_low.live)
                text.AppendLine(!_low.battery_switch
                    ? "<color=yellow><b>[no 110 V: the battery's switch is off - under the flap in the assistant's desk]</b></color>"
                    : "<color=yellow><b>[no 110 V: its battery is flat - the traction battery's switch brings it back]</b></color>");
            else
            {
                string[] names = { "fans", "converter", "compressors", "cab heating", "pantographs", "sanding", "control", "window heating", "lighting", "headlights" };
                string out_now = string.Join(", ", names.Where((_, index) => !_low.breaker[index]));
                text.AppendLine($"110 V: <b>{_low.volts:F0} V</b>, battery {_low.charge * 100.0f:F0} %{(_low.charging ? ", charging" : _low.supplied ? "" : $", giving {_low.battery_amps:F1} A")}" +
                    $"   pantograph air <b>{_air.bar:F1} bar</b>{(_air.pump_running ? ", the small compressor running" : "")}" +
                    (out_now.Length > 0 ? $"   <color=yellow>breakers out: {out_now}</color>" : ""));
                bool asked = pantograph_ports.Any(one => read(one.ask) > 0.5f), any_up = pantograph_ports.Any(one => read(one.valve) > 0.5f);
                if (_ports.ContainsKey(port_small_compressor) && ((asked && !any_up && !_air.can_raise) || (any_up && !power.pantograph_air_ok)))
                    text.AppendLine(!_air.cock_to_small && power.main_reservoir_bar >= eu07_sim.pantograph_air.raise_bar
                        ? "<color=yellow><b>[no air for the pantographs yet: it is coming from the main reservoir]</b></color>"
                        : "<color=yellow><b>[no air for the pantographs and the line breaker. In the machine room, cab A's end: the three-way cock on the pneumatic frame UP " +
                          "(to the small compressor), the small compressor's switch on the low-voltage board; it stops by itself at 4.8 bar]</b></color>");
                if (read(port_small_compressor) > 0.5f && !_air.cock_to_small)
                    text.AppendLine("<color=yellow><b>[the small compressor's switch is on and does nothing for the pantographs: the three-way cock is on the main reservoir]</b></color>");
                else if (_air.cock_to_small && power.main_reservoir_bar >= eu07_sim.pantograph_air.feed_bar)
                    text.AppendLine("<color=yellow><b>[the three-way cock is still on the small compressor: turn it back DOWN to the main reservoir, or the pantographs come down when the small vessel has leaked away]</b></color>");
            }
        }
        text.AppendLine($"effort {power.wheel_torque / 625.0f:F0} kN, drag {running_drag_newtons * 0.001f:F1} kN   resistors {power.resistor_heat * 0.001f:F0} kW, {power.resistor_celsius:F0} C, fans {power.fan_speed:F2}   " +
            $"converter {power.converter_speed:F2}   compressors {(power.compressor_running[0] ? 1 : 0) + (power.compressor_running[1] ? 1 : 0)}");
        text.Append(lamp(power.fast_breaker_closed, "main breaker"));
        text.Append(lamp(!power.line_contactors_closed, "line contactors open"));
        text.Append(lamp(power.on_resistors, "on resistors"));
        text.AppendLine(lamp(power.high_start_active, "high start"));
        text.Append(lamp(power.overcurrent_tripped, "motor overload"));
        text.Append(lamp(power.earth_fault_tripped, "earth fault"));
        text.Append(lamp(power.slip_detected, "wheel slip"));
        text.AppendLine(lamp(power.fans_failed, "resistor fans"));
        text.Append(lamp(power.converter_tripped || power.heating_tripped, "converter overload"));
        text.Append(lamp(power.compressor_tripped, "compressor overload"));
        text.Append(lamp(power.heating_on, "ogrzewanie pociagu"));
        text.AppendLine(lamp(power.undervoltage, "no line voltage"));
        // The last-mile battery, while its switch is on or it is charging; and why its contactor
        // is not in, which from the desk is only a lamp that stays dark.
        if (power.battery_wanted || power.battery_charging)
        {
            text.Append($"battery <b>{power.battery_charge * 100.0f:F0} %</b>   {power.battery_volts:F0} V   {power.battery_amps:F0} A of {eu07_sim.power_circuit.battery_max_amps:F0}   ");
            text.Append(lamp(power.battery_contactor, "on battery"));
            text.AppendLine(lamp(power.battery_charging, "charging"));
            if (power.battery_wanted && !power.battery_contactor)
                text.AppendLine("<color=yellow><b>[" + (power.battery_tripped ? "the battery's breaker has opened: its switch off and on again"
                    : power.battery_charge <= 0.0f ? "the battery is flat: it charges under the wire, with the converter running"
                    : power.fast_breaker_closed ? "to go onto the battery: main breaker off, wheel at 0"
                    : "to go onto the battery: wheel at 0") + "]</b></color>");
            else if (power.battery_contactor)
                text.AppendLine($"<color=grey>on the battery: series only (to {eu07_sim.power_circuit.battery_top_position}), the field whole; the main breaker will not close until its switch is off</color>");
        }
        // The reset button's own circuit runs through the reverser off neutral and the wheel at 0:
        // pressed in neutral or with the wheel off 0 it does nothing, which from the desk looks
        // like a relay that will not reset ("stuck at ground fault. im neutral, under way" - the
        // author, 2026-10-10; it was the motor overload, and it cleared in forward).
        if ((power.overcurrent_tripped || power.earth_fault_tripped) && power.health > 0.0f
            && (power.contactors.direction == 0 || power.contactors.wanted != 0 || power.contactors.position != 0))
            text.AppendLine("<color=yellow><b>[to reset: reverser forward or back, wheel at 0, then the reset button]</b></color>");
        // The earth-fault relay has ONE condition under which it will not reset: a drivetrain with
        // nothing left. Said here, because from the desk it only looks like a dead button.
        text.AppendLine(power.health <= 0.0f
            ? "<color=yellow><b>[electrical drivetrain destroyed: the earth fault will not reset until it is repaired]</b></color>"
            : $"<color=grey>electrical drivetrain {power.health * 100.0f:F0} %   (a flashover costs {eu07_sim.power_circuit.flashover_damage:F0} hit points; at 0 % the earth fault stays)</color>");
        if (_desk_v2)
        {
            text.AppendLine($"<color=grey>desk v2   PLP (wheel slip) held: {slip_button}, not wired to anything yet</color>");
            text.AppendLine("<color=grey>keys: WS on = Starter, WS off = Fuel cutoff, OPN = Starter fuse; keypad 7 1 5 0 9 still work, 3 hides this</color>");
        }
        else
        {
            text.AppendLine("<color=grey>desk switches, right of the pantographs: fast breaker, relay reset, line contactors off, high start</color>");
            text.AppendLine("<color=grey>field lever between throttle and brake (dynamic brake keys); keypad 7 1 5 0 9 still work, 3 hides this</color>");
        }
        if (_problem.Length > 0)
            text.AppendLine($"<color=red>prefab lacks: {_problem}</color>");

        GUIStyle style = new(GUI.skin.box) { alignment = TextAnchor.UpperLeft, richText = true, fontSize = 14, wordWrap = false };
        style.padding = new RectOffset(10, 10, 8, 8);
        GUIContent content = new(text.ToString());
        Vector2 size = style.CalcSize(content);
        GUI.Box(new Rect(12.0f, 12.0f, size.x, size.y), content, style);
    }
}
