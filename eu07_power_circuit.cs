// EU07 / 303E - the high-voltage side of the locomotive, as one object with no Unity in it.
//
// It owns the compiled circuit, the contactors and the four motors, and adds what stands
// around them on the real locomotive (303E DTR, chapter 7):
//
//   WS        the fast breaker. Closed by an impulse, held by control voltage, air and the
//             undervoltage relay PZN, which lets go below 1900 V and will not let WS close
//             below 2100 V (real volts; halved here).
//   PON1,2,3  the motors' overcurrent relays. 600 A on each motor group, and a 750 A element
//             on group 1-2. "High start" (WWR) blocks the 600 A ones while the motors are
//             in series. A trip opens the LINE contactors, not WS, lights LS8, and is reset
//             with OPN, which works only with a direction set and the wheel at 0.
//   PLW       the button that opens the line contactors by hand.
//   MP        two ammeters, one a motor group; MN the line voltmeter.
//   SWO       the resistor fans, fed from the drop across R28 and R30, so they run on every
//             resistance position and stop by themselves on 28 and 43.
//
//   SCR1,2    the slip relays: each compares the EMFs of the two armatures of a pair, which
//             differ when one axle runs away. Lamp LS9. They want a speed an axle.
//   PRG       the earth-fault relay. Opens WS, lights LS7, reset with OPN.
//   MP        the converter: a 3 kV motor turning a 110 V generator and, on the same shaft,
//             the traction motors' blowers. Its overcurrent relay opens WS (lamp LSPO).
//   MS1,2     the main compressors, 110 V motors fed by the converter, governed by the
//             pressure switch WCC; their overcurrent relays light LSC.
//   SGP       train heating, straight off the line behind WS; its relay opens WS too.
//
// The auxiliaries are not in the solved circuit: they are a load added to the line
// current, as his unit_A_sim adds its compressor. Nothing in the simulation yet CAUSES an
// earth fault or a burnt-out compressor, so those relays are tripped from outside, with
// trip(): by the game's damage, or by the bench.
//
// The in-game class binds this to ports and handles; bench/eu07_bench.cs drives it bare.

using System;
using System.Collections.Generic;

using LocoSim.Implementations;

using electric_sim.circuit_sim;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class power_circuit
    {
        // Real settings, scaled to this locomotive's amps and volts.
        public const float overcurrent_normal   =  600.0f * current_ratio;
        public const float overcurrent_high     =  750.0f * current_ratio;
        public const float undervoltage_dropout = 1900.0f * line_voltage_ratio;
        public const float undervoltage_pickup  = 2100.0f * line_voltage_ratio;
        // The no-volt relay lets go only when the wire has been dead for this long. The valley's
        // wire has joints of some 20 m with nothing in them - 1.3 s at 54 km/h - and each one
        // threw the main breaker out, a thud for no reason the driver could see ("i still get
        // random panto drop audio"; it was the breaker). Asked: "2 second hold". A joint taken
        // at 40 km/h and over is ridden through; a dead stretch, or a joint crawled over, still
        // opens it. NOT the real relay, which has no such patience - nor has the real wire such joints.
        public const float undervoltage_hold_seconds = 2.0f;
        private float _undervoltage_seconds = 0.0f;
        public const float high_start_allowed_seconds = 300.0f;      // then the buzzer DN

        // The auxiliaries keep their real POWER, so on half the volts they take twice the amps
        // and their relays are set at twice the real figure.
        public const float converter_overcurrent   =   21.0f / line_voltage_ratio;   // NPR, 21 A real
        public const float heating_overcurrent     =  200.0f / line_voltage_ratio;   // NGP, 200 A real
        public const float converter_minimum_volts = 2000.0f * line_voltage_ratio;   // below it the set runs down
        public const float blower_watts = 20.2e+3f, compressor_watts = 110.0f * 142.0f, control_watts = 2.0e+3f;
        public const float converter_efficiency = 0.9f, converter_run_up_seconds = 4.0f;
        // GAUGE pressure, as on the locomotive's manometer. The real switch WCC lets go at
        // 8.5 bar (0.85 MPa); the game's main reservoir stops at 8.0 bar gauge (it counts
        // 9.0 with the atmosphere in), so 8.5 would never come and the compressors would
        // never stop. Cut out at the game's ceiling instead (the author, 2026-10-10).
        public const float compressor_cut_out_bar = 8.0f;
        public const float compressor_cut_in_bar  = 7.0f;            // assumed, accepted: the DTR gives only the cut-out

        // Resistor fans: four series motors, 189 V and 52 A real, across R28 + R30; a relay
        // on each lights LS6 under 16.5 A. Here the same motors on half the volts.
        public const float fan_rated_volts  = 189.0f * line_voltage_ratio, fan_rated_amps = 52.0f / line_voltage_ratio;
        public const float fan_minimum_amps = 16.5f / line_voltage_ratio;

        // ASSUMED: the DTR gives the slip relay's resistors, not what difference it answers
        // to. This is the EMF of about 4 km/h between the two axles of a bogie.
        public const float slip_relay_volts = 60.0f;

        public enum relay { earth_fault, converter, heating, compressor_1, compressor_2 }

        // His: the motors and meters see branch currents through this, a tick. It is what
        // keeps a series motor and a solver one step behind it from ringing.
        private const float current_filter = 0.02f;
        // A relay armature is not instant, and the solver's first step after a contactor
        // closes can overshoot.
        private const float overcurrent_delay = 0.06f;

        public readonly circuit                                  solver;
        public readonly Dictionary<string, circuit.branch_user> named, contactor_locations;
        public readonly Dictionary<string, float>                currents = [];
        public readonly eu07_contactors                          contactors;
        public readonly eu07_traction_motor[]                    traction_motors = new eu07_traction_motor[motors];

        private readonly Fuse  _control_power, _control_air;
        private readonly float _line_resistance, _fan_resistor_28, _fan_resistor_30;

        private bool  _pair_12_out = false, _pair_34_out = false;
        private float _over_time = 0.0f, _pending_damage = 0.0f;

        // PLUGGING. Thrown into reverse while still rolling and given power, a motor is driven
        // against its own rotation: its EMF now helps the wire instead of opposing it, the
        // current is far above what the same notch gives from a stand, and the torque brakes.
        // The reverser is interlocked with the wheel only, so the real machine lets its driver
        // do this - and pays in its commutators. So does this one (the author, 2026-10-10:
        // "this is an analog loco. so let the player do it. at a cost of the electrical
        // drivetrain damage"). The measure is the power the motors are made to take in through
        // their shafts, against what they are rated to give out. THE RATES ARE CHOSEN, not
        // found in any document.
        //
        // It cost the drivetrain from the first watt, and a commutator flashed over by chance
        // from 0.6 of rated power, until he had run 18 km braking so: "the field reversal trick
        // is very useful. so E-Braking, id let the players do it, but only up to a speed where
        // it doesnt cause a trip" (2026-10-11). So it is FREE, and what ends it is a TRIP, at a
        // limit that is the same every time:
        //   - the overcurrent relays, as ever (they hold on notch 1 to 100 km/h, on 2 to 50, on
        //     3 to 30, on 4 to 20, on 5 and 6 to 10, and nowhere from 8 up: the bench's table,
        //     EU07_BENCH_PLUGGING);
        //   - and the commutators: the motors may take in through their shafts what they are
        //     rated to give out, plugging_free. Above that the excess is counted, and at
        //     flashover_excess of it (a second at one and a half times rated power) one flashes
        //     over: an earth fault, WS out, and a lump off the drivetrain. That is notch 1 to
        //     50 km/h (96 kN), 2 and 3 to 30 (110 and 173 kN), 4 to 20 (177 kN), 5 and 6 at 10
        //     (158 and 193 kN).
        public const float rated_power_watts    = 2000.0e+3f * line_voltage_ratio * current_ratio;   // the four motors, at this model's scale
        public const float plugging_free        = 1.0f;     // of rated power: up to this, braking against the motors costs nothing
        public const float flashover_excess     = 0.5f;     // rated powers above that, times seconds, that a commutator stands
        public const float flashover_damage     = 30.0f;    // hit points a flashover costs, of the game's pool of 1000
        private float _plugging_excess = 0.0f;

        // THE LAST-MILE BATTERY ("we need a last mile battery ... we could do it physics based.
        // since last mile does make sense with a consist behind it", the author, 2026-10-11).
        // Not on the real locomotive. A traction battery beside the line breaker: its own
        // contactor puts it on the traction circuit IN PLACE of the wire, and from there on it
        // is the same circuit - the same starting resistors, the same four motors in series -
        // on rather more than a quarter of the wire's volts. Nothing about speed, pull or range is set
        // here: they are whatever a source of these volts, this resistance and this much
        // energy gives with the load there is.
        //   - it closes only with the wheel at 0 and the line breaker out, and the line
        //     breaker will not close while it is in: never both;
        //   - on it the wheel goes no further than 28, the end of series, and the field stays
        //     whole: the battery is not wired to the parallel connection or the shunts (with
        //     them a light engine ran to 80 km/h on it, which is no last mile);
        //   - its own breaker opens it above battery_max_amps; switching it off and on resets that;
        //   - it carries the 110 V load too, so the compressors can run on it;
        //   - under the wire, with the converter running, it charges.
        public const float battery_kWh          = 60.0f;                     // usable
        public const float battery_full_volts   = 430.0f, battery_empty_volts = 380.0f;      // open circuit, full and flat
        public const float battery_ohms         = 0.10f;                     // inside it
        public const float battery_max_amps     = 700.0f;
        public const float battery_trip_seconds = 0.3f;
        public const float battery_charge_watts = 100.0e+3f;                 // taken from the wire while it charges
        public const float battery_charge_gain  = 0.9f;                      // of that, what stays in it
        public const int   battery_top_position = series_running;
        public bool  battery_wanted     { get; set; } = false;               // its switch
        public float battery_joules     { get; set; } = battery_kWh * 3.6e+6f;    // what is in it; whoever keeps it over a save sets it
        public bool  battery_contactor  { get; private set; } = false;       // in: the traction circuit is on the battery
        public bool  battery_tripped    { get; private set; } = false;
        public float battery_trip_amps  { get; private set; } = 0.0f;        // at its last opening: all of it, and the traction circuit's share
        public float battery_trip_traction_amps { get; private set; } = 0.0f;
        public float battery_volts      { get; private set; } = battery_full_volts;   // at its terminals
        public float battery_amps       { get; private set; } = 0.0f;
        public bool  battery_charging   { get; private set; } = false;
        public float battery_charge     => Math.Max(0.0f, Math.Min(1.0f, battery_joules / (battery_kWh * 3.6e+6f)));
        private float _battery_over = 0.0f, _battery_low_voltage_watts = 0.0f;
        private bool  _solver_caught_up = false;
        private int   _first_steps = 0;
        // Has the solver contactor moves it has not yet worked into its matrices? (Its own
        // business, read as the bench reads it: there is no public word for it.)
        private static readonly System.Reflection.FieldInfo? _solver_task = typeof(circuit).GetField("_matrices_recalculation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
            _solver_changed = typeof(circuit).GetField("_branch_conductances_changed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance),
            _solver_staged = typeof(circuit).GetField("_branch_conductances_staged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private bool solver_behind()
        {
            if (_solver_task == null || _solver_changed == null || _solver_staged == null)
                return false;
            return _solver_task.GetValue(solver) != null
                || (_solver_changed.GetValue(solver) as System.Collections.ICollection)?.Count > 0
                || (_solver_staged.GetValue(solver) as System.Collections.ICollection)?.Count > 0;
        }
        private readonly bool[] _compressor_wanted = new bool[2], _compressor_tripped = new bool[2];

        // ---- in ---------------------------------------------------------------------------
        public float line_volts            { get; set; }                 // at the pantograph
        public float traction_volts        { get; private set; }         // what the traction circuit is given of it (see simulate)
        public bool  supply_follows_field  { get; set; } = true;         // false: the wire as it is, for the bench to show what that did
        // the most the loop filter -> field -> EMF -> current may gain in a tick (see simulate); infinity: as it was
        public float field_loop_gain_limit { get; set; } = 0.2f;
        // a current against the field's own unmakes the field at once (eu07_traction_motor.unmake_field); false: as it was
        public bool  field_unmade_by_reversal { get; set; } = true;
        public float relay_amps            { get; private set; }         // what the overcurrent relays see: the greater group's, unfiltered
        public bool  high_start            { get; set; } = false;        // WWR
        public bool  line_contactor_cutout { get; set; } = false;        // PLW, while held
        // JAZDA CIAGNIONA ("Right. Make it", the author, 2026-10-12): the switch on the cab's back wall
        // behind the driver, "jazda pociagowa" (train running, as it stands) or "jazda ciagniona"
        // (hauled). Hauled, a contactor of the control circuit stays open and the wheel's first
        // position closes nothing: the locomotive can be towed with its battery on and cannot
        // take power, whoever touches the desk. (What it is: ZNTK Olesnica's EP07 instruction
        // names the position, the SimRail team's account of their EU07 says what it does; NOTES.)
        // The motors need nothing done to them: open-circuit, a series motor carries no current
        // and gives no torque either way.
        public bool  hauled                { get; set; } = false;
        // WHAT THE TRAIN PROTECTION WILL ASK, when there is one ("Add an endpoiny for shp when we
        // eventually make it"): the real switch's other job is to keep the vigilance device and
        // the train stop (SHP) from venting the brake pipe of a locomotive that is only being
        // hauled. Neither is built. Whatever builds them asks THIS before it sounds, counts or
        // brakes, with whether the 110 V is live: hauled, or dead, they are out.
        public bool  train_protection_live(bool low_voltage_live) => low_voltage_live && !hauled;
        public bool  protection_bypassed   { get; set; } = false;        // bench only
        public bool  pantograph_air_ok     { get; set; } = true;         // WCP: no air, no WS
        public bool  converter_switch      { get; set; } = false;        // WDG
        // THE CONVERTER'S KNIFE SWITCH, in the low-voltage cabinet: open, the converter has no feed. It is an
        // ISOLATOR - blades in jaws, nothing to put an arc out - and is opened on a converter that has been
        // switched off at the desk, whose contactor has broken the current. Pulled on a converter that is
        // RUNNING ("the loco doesnt get mad and spew faults when i open the converter circuit in the LV cabinet
        // when the loco is running", the author, 2026-10-12: it only ran down, as if switched off) the
        // blades draw the arc themselves: it goes to the cabinet's frame, the earth-fault relay answers and
        // throws the main breaker out, and the burnt blades are damage.
        public bool  converter_knife_open  { get; set; } = false;
        public const float knife_arc_damage = 40.0f;                     // hit points of the game's 1000
        public int   knife_arcs            { get; private set; } = 0;
        private bool _knife_was_open = false, _converter_was_driven = false;
        private float _converter_amps_were = 0.0f;
        public bool  heating_switch        { get; set; } = false;        // WDO3,4
        // The cab's own heaters: high voltage, behind WS, a 3 A fuse a cab. Nothing but a load.
        public const float cab_heating_watts = 3.0e+3f;
        public bool  cab_heating_switch    { get; set; } = false;
        public bool  cab_heating_on        { get; private set; } = false;
        // The resistor fans' relays have a breaker of their own on the 110 V (WSW): out, the fans stand.
        public bool  fans_fed              { get; set; } = true;
        public float heating_load_watts    { get; set; } = 0.0f;         // what the train behind takes
        public float main_reservoir_bar    { get; set; } = 0.0f;         // GAUGE bar, for the pressure switch WCC
        public readonly bool[] compressor_switch = new bool[2];          // WDC1, WDC2

        // ---- out --------------------------------------------------------------------------
        public bool  fast_breaker_closed  { get; private set; } = false; // lamp LSS
        public bool  overcurrent_tripped  { get; private set; } = false; // lamp LS8
        public bool  high_start_buzzer    { get; private set; } = false;
        public float high_start_seconds   { get; private set; } = 0.0f;
        public float ammeter_12           { get; private set; }          // A, motors 1 and 2
        public float ammeter_34           { get; private set; }          // A, motors 3 and 4
        public float line_current         { get; private set; }          // A drawn at the pantograph
        public float wheel_torque         { get; private set; }          // N m, all four axles
        public float resistor_heat        { get; private set; }          // W into the starting resistors
        public float motor_heat           { get; private set; }          // W into the four motors
        public float fan_volts            { get; private set; }          // across R28 + R30
        public float fan_speed            { get; private set; }          // 1 at rated volts, the resistor fans

        // THE STARTING RESISTORS' HEAT, roughly ("can we do a rough temp sim for the resistor
        // blowers? since when climbing a hill i dont hear much of fans. and the loco is
        // working", the author, 2026-10-11). The real fans hang across two of the resistors:
        // they turn with the current while the resistors are in, and stop the moment the last
        // is cut out, however hot the grids are. Here the grids are ONE lump that the lost
        // power heats and the air cools - a little standing, far more blown - and the fans are
        // ALSO driven by that heat: from fans_from up they turn, faster the hotter, and run on
        // after the resistors are out until the lump has cooled. That is NOT the real
        // circuit; it is what makes hard work heard. Every figure is a round guess.
        public const float resistor_heat_capacity = 3.0e+5f;         // J/K: some 600 kg of grids
        public const float resistor_still_air     = 300.0f;          // W/K lost with the fans standing
        public const float resistor_blown_air     = 3500.0f;         // W/K more with them at rated speed
        public const float ambient_celsius        = 20.0f;
        public const float fans_from_celsius      = 35.0f;           // the heat starts them here
        public const float fans_full_celsius      = 110.0f;          // and has them at rated speed here; three tenths faster is the most
        public const float fans_by_heat_most      = 1.3f;
        public const float resistor_most_celsius  = 800.0f;          // glowing; no hotter, whatever is asked of them
        public const float resistor_fan_watts     = 4.0f * 8.4e+3f;  // the four fan motors at rated speed, when it is the heat that drives them: off the line, not the converter
        private float _fan_heat_watts = 0.0f;
        public float resistor_celsius     { get; private set; } = ambient_celsius;

        // THE GRIDS THAT STAY IN: what running on the resistors costs ("Should we make a downside for
        // running on resistors for long amounts of time? Irl drivers can only make around 5-10
        // minutes before damage ... roughly 5 minutes. we could sim it tho. because the failure
        // point would be melting, no?", the author, 2026-10-12).
        //     The lump above is the whole bank, there for the fans to be heard, and blown as it
        // is it never gets hot. What fails is not the bank but A GRID. The sections cut out
        // last carry the motors' current from the first notch to the last, and a grid heats
        // with the SQUARE OF THAT CURRENT whatever the notch: its own resistance times I2 into
        // its own iron, the same for every grid of one cross-section. So this is one such
        // grid: heated by the greater ammeter, squared, while any resistance is in; cooled by
        // the air - a little of it standing, the fans' blast besides - and by its own glow,
        // which goes with the fourth power of its heat: next to nothing black, the most of it
        // red (so a red grid is black again in minutes, and a warm one stays warm for long).
        // The limit is then what it is on a real desk: the ammeter and the clock, not the notch.
        //     Set so that from cold it is 5 minutes at 750 A on the ammeter to grids_hot and 10 at
        // 600 A, and that with the fans turning 450 A can be held for good (the bench prints the
        // rest). 2.6 K a second at the real locomotive's 500 A is cast iron of some 160 mm2 carrying it (3 A/mm2: the size
        // of a traction grid), and the glow an eighth of what a bare strip of that size would
        // give off, since in its stack a grid mostly sees its neighbours; the air is a round
        // guess. No instruction to hand gives the times (NOTES).
        //     Melting is the END of it, not the failure (cast iron runs at about 1150 C). Long
        // before, red hot, a grid scales, grows and warps: WEAR, from grids_hot. And at
        // grids_fail one sags onto its neighbour or the frame: an earth fault - the relay out,
        // the main breaker with it - and a piece of the drivetrain's health gone with the grid.
        // (500 A of the REAL locomotive's: 750 on these ammeters, which like everything here read at this
        // model's scale - as the overload relays' 600 A is 900. It stood at a bare 500 at first, the
        // same day; and a heavy train's start, two or three minutes at 700 to 800 A, would have had
        // a grid gone before the series running notch. Found when notching by itself could not
        // get 1500 t up 4 per mille without easing off to a stand: eu07_auto_notching.cs.)
        public const float grid_reference_amps    = 500.0f * current_ratio;
        public const float grid_kelvin_per_second = 2.6f;            // at that; with the square of the current
        public const float grid_still_air         = 0.0003f;         // 1/s of what it is above the air, the fans standing
        public const float grid_blown_air         = 0.0010f;         // 1/s more with them at rated speed
        public const float grid_glow              = 7.0e-13f;        // K/s for each K4 between its absolute heat and the air's
        public const float grids_warm_celsius     = 400.0f;          // black still: the driver is told
        public const float grids_hot_celsius      = 600.0f;          // dull red: wear from here
        public const float grids_fail_celsius     = 800.0f;          // bright red: one sags and touches
        public const float grids_melt_celsius     = 1150.0f;         // no hotter: it runs
        public const float grid_wear_per_second   = 0.5f;            // hit points of the game's 1000, at grids_fail; in proportion from grids_hot, and beyond
        public const float grid_burn_damage       = 200.0f;          // hit points the grid that goes takes with it
        public float grid_celsius         { get; set; } = ambient_celsius;      // (set: by the bench and the debug hook)
        // the line contactors would close for a wheel moved off 0 (what notching by itself asks first: eu07_auto_notching.cs)
        public bool  may_take_power       { get; private set; } = false;
        public float pair_amps_12         { get; private set; }                 // each pair's current as the relays see it: undamped
        public float pair_amps_34         { get; private set; }
        public float feed_volts => battery_contactor ? battery_volts : line_volts;     // what feeds the traction circuit: the wire, or the battery
        public int   grids_burnt          { get; private set; } = 0;
        private bool _grid_down = false;                             // one has gone, and they have not been below grids_hot since

        // THE MOTORS' HEAT ("the trac motors are outside so the only thing we can do to
        // differentiate hot btwn cold is a heat fx, which the game already exposes", the author,
        // 2026-10-11). The four motors are one lump that their own losses heat (motor_heat:
        // about 0.19 ohm x the current squared) and the air cools - a little by itself, most of
        // it from the blowers on the converters' shafts, so with the converter stopped they
        // cook. The figures are set against the GAME's scale, which whoever drives this feeds:
        // it wears the motors from 60 C, harder the hotter, and at 120 C blows their fuse and
        // now and then a motor. So:
        //   - at the continuous current (510 A here: 340 A real) they settle at 75 C, at the
        //     hourly one (548 A) at 83 C, at 600 A at 96 C: wear of a per cent or so an hour;
        //   - 120 C is where some 690 A held for good ends; from 80 C, 750 A takes twenty-two
        //     minutes to get there and 880 A, just under the relays, ten;
        //   - on the continuous current with the converter stopped: three quarters of an hour from cold.
        // (The bench's figures: EU07_BENCH_MOTOR_HEAT.) The lump follows with a time of twenty
        // minutes blown, an hour and a half not.
        public const float motor_heat_capacity = 1.056e+6f;          // J/K
        public const float motor_still_air     = 180.0f;             // W/K with the blowers standing
        public const float motor_blown_air     = 700.0f;             // W/K more with the converter at speed
        public const float motor_most_celsius  = 300.0f;
        public float motor_celsius        { get; set; } = ambient_celsius;    // whoever keeps it over a save sets it
        public float fan_by_current       { get; private set; }      // what the real circuit gives them
        public float fan_by_heat          { get; private set; }      // what the heat asks of them
        public float traction_current     { get; private set; }          // A, the motors' share of line_current
        public float auxiliary_current    { get; private set; }          // A, converter and heating
        public float converter_speed      { get; private set; } = 0.0f;  // 0-1; also the motor blowers
        public bool  earth_fault_tripped  { get; private set; } = false; // lamp LS7
        public float plugging_stress      { get; private set; } = 0.0f;  // shaft power taken IN, as a part of rated power
        public int   flashovers           { get; private set; } = 0;
        // AN ARC ("maybe arcing when high ampers trip the overload and open the mains?", the author,
        // 2026-10-11): counted when the protection breaks a heavy current - the overload relays
        // going out, or the main breaker opened or thrown out, with the line contactors in and
        // arc_from_amps or more in the motors. NOT when the driver shuts off with the wheel:
        // that is what the contactors and their chutes are for. Whoever shows it reads these.
        public const float arc_from_amps = 300.0f;
        public int   arcs                 { get; private set; } = 0;
        public float arc_amps             { get; private set; } = 0.0f;
        public bool  arc_at_breaker       { get; private set; } = false;        // the last one: in the main breaker (else in the line contactors)
        private void arc_if_loaded(bool breaker)
        {
            if (line_contactors_closed && relay_amps >= arc_from_amps)
            {
                arc_amps = relay_amps;
                arc_at_breaker = breaker;
                ++arcs;
            }
        }
        // 0-1, the game's electrical drivetrain health, written by whoever owns it. Below a half
        // the motors give less, as the game's own do; at nothing the earth-fault relay is out for good.
        public float health               { get; set; } = 1.0f;
        public bool  converter_tripped    { get; private set; } = false; // lamp LSPO, with the next
        public bool  heating_tripped      { get; private set; } = false;
        public bool  heating_on           { get; private set; } = false; // lamp LSGP
        public bool  slip_detected        { get; private set; } = false; // lamp LS9
        public readonly bool[] compressor_running = new bool[2];

        // The converter is the only source of 110 V besides the battery.
        public bool converter_charging => converter_speed >= 0.9f;
        public bool on_resistors       => line_contactors_closed                                         // lamp LS5
                                       && contactors.position != series_running && contactors.position != parallel_running;
        public bool fans_failed        => on_resistors && (!fans_fed || fan_by_current * fan_rated_amps < fan_minimum_amps);  // lamp LS6: the real circuit's own
        public bool compressor_tripped => _compressor_tripped[0] || _compressor_tripped[1];               // lamp LSC

        public bool undervoltage           => line_volts < undervoltage_dropout;
        public bool line_contactors_closed => is_on("SL1");                              // lamp LS10 is its opposite
        public bool high_start_active      => high_start && !is_on("SL3") && contactors.direction != 0;   // lamp LS4

        // wheel_RPM: one port for the whole locomotive, or four, an axle each. With one the
        // slip relays have nothing to compare and never speak.
        public power_circuit(Fuse control_power, Fuse control_air, Port[] wheel_RPM, Action<bool>? contactor_sound = null,
            int? random_seed = null)
        {
            if (wheel_RPM.Length != 1 && wheel_RPM.Length != motors)
                throw new ArgumentException("one wheel speed, or one an axle");
            _control_power = control_power;
            _control_air   = control_air;

            // As his unit_A_sim: no two locomotives quite alike, one percent either way.
            Random? dice = random_seed is int seed ? new Random(seed) : null;
            float vary() => dice == null ? 1.0f : 0.99f + 0.02f * (float) dice.NextDouble();

            Dictionary<string, float> resistances = [];
            foreach (KeyValuePair<string, float> element in _base_element_resistances)
                resistances[element.Key] = element.Value * vary();
            _line_resistance = resistances["OCS"] + resistances["PANT"];
            _fan_resistor_28 = resistances["R28"];
            _fan_resistor_30 = resistances["R30"];

            solver = circuit_compiler.trace(resistances, circuit_diagram).set_up_simulation(out named, out contactor_locations, currents);
            foreach (string name in named.Keys)
                currents[name] = 0.0f;

            contactors = new eu07_contactors(contactor_locations, control_power, control_air, contactor_sound);
            for (int m = 1; m <= motors; ++m)
                traction_motors[m - 1] = new eu07_traction_motor(m, vary(), vary(), wheel_RPM[wheel_RPM.Length == 1 ? 0 : m - 1], named);
        }

        private bool is_on(string contactor) => contactor_locations[contactor].is_contactor_on(contactor);

        private void throw_fast_breaker(bool on)
        {
            if (!on && fast_breaker_closed)
                arc_if_loaded(true);
            fast_breaker_closed = on;
            feed();
        }

        private void throw_battery(bool on)
        {
            battery_contactor = on;
            feed();
        }

        // The wire's breaker and the battery's contactor feed the traction circuit at the same
        // place, never both: in the solver they are one element, and which source stands
        // behind it is the EMF given there (simulate).
        private void feed()
        {
            contactor_locations["WS"].toggle_contactor("WS", fast_breaker_closed || battery_contactor);
        }

        // ---- the handles and buttons -------------------------------------------------------
        public void set_controller(int position)  => contactors.set_controller(battery_contactor ? Math.Min(position, battery_top_position) : position);
        public void set_field_weakening(int step) => contactors.set_field_weakening(battery_contactor ? 0 : step);
        public bool set_reverser(int direction)   => contactors.set_reverser(direction);

        // WJ7,8. Refused without control voltage, air, or enough volts on the wire.
        public bool close_fast_breaker()
        {
            if (!fast_breaker_closed && !battery_contactor && _control_power.State && _control_air.State && pantograph_air_ok
                && line_volts >= undervoltage_pickup && !earth_fault_tripped && !converter_tripped && !heating_tripped)
                throw_fast_breaker(true);
            return fast_breaker_closed;
        }

        // WJ5,6.
        public void open_fast_breaker()
        {
            if (fast_breaker_closed)
                throw_fast_breaker(false);
        }

        // OPN: the motors' overcurrent relays and the earth-fault relay. The button's own
        // circuit runs through the reverser off neutral and the wheel at 0, so pressed
        // anywhere else it does nothing.
        public bool reset_relays()
        {
            if (contactors.direction != 0 && contactors.wanted == 0 && contactors.position == 0)
            {
                overcurrent_tripped = false;
                earth_fault_tripped = health <= 0.0f;        // a drivetrain with nothing left stays on its earth fault
            }
            return !overcurrent_tripped && !earth_fault_tripped;
        }

        // The hit points run up since this was last asked: for the game's damage pool.
        public float take_damage()
        {
            float damage = _pending_damage;
            _pending_damage = 0.0f;
            return damage;
        }

        // WJ1-WJ4: the relays of the converter, the compressors and the heating.
        public void reset_auxiliary_relays()
        {
            converter_tripped = heating_tripped = _compressor_tripped[0] = _compressor_tripped[1] = false;
        }

        // A relay made to speak from outside: damage in the game, a test on the bench.
        public void trip(relay which)
        {
            switch (which)
            {
                case relay.earth_fault : earth_fault_tripped    = true; break;
                case relay.converter   : converter_tripped      = true; break;
                case relay.heating     : heating_tripped        = true; break;
                case relay.compressor_1: _compressor_tripped[0] = true; break;
                case relay.compressor_2: _compressor_tripped[1] = true; break;
            }
        }

        // OS1-OS4, by hand in the machine room: only on a dead locomotive.
        public bool cut_out_motors(bool pair_12, bool pair_34)
        {
            if (fast_breaker_closed || contactors.position != 0)
                return false;
            _pair_12_out = pair_12;
            _pair_34_out = pair_34;
            contactors.cut_out_motors(pair_12, pair_34);
            return true;
        }

        // ---- every tick ---------------------------------------------------------------------
        public void simulate(float delta_seconds)
        {
            bool control = _control_power.State && _control_air.State;
            simulate_auxiliaries(delta_seconds);
            if (health <= 0.0f && !earth_fault_tripped)
                trip(relay.earth_fault);
            _undervoltage_seconds = undervoltage ? _undervoltage_seconds + delta_seconds : 0.0f;
            if (fast_breaker_closed && (!control || _undervoltage_seconds >= undervoltage_hold_seconds || !pantograph_air_ok
                                        || earth_fault_tripped || converter_tripped || heating_tripped))
                throw_fast_breaker(false);

            // The battery's contactor, its breaker, and what it gives at its terminals now: its
            // open-circuit volts for what is left in it, less what its own resistance takes.
            if (!battery_wanted)
                battery_tripped = false;
            float battery_open = battery_empty_volts + (battery_full_volts - battery_empty_volts) * battery_charge;
            bool  battery_may  = battery_wanted && _control_power.State && !fast_breaker_closed && battery_joules > 0.0f
                              && !battery_tripped && !earth_fault_tripped;
            if (battery_contactor && !battery_may)
                throw_battery(false);
            else if (!battery_contactor && battery_may && contactors.position == 0 && contactors.wanted == 0)
                throw_battery(true);
            // (The 110 V load is so many WATTS, and was divided by whatever the terminals showed, down
            // to one volt: a single step's surge as the contactor closed took the terminals to
            // nothing, the same watts then asked for thirty thousand amperes, which held them at
            // nothing - and the battery's breaker went out a quarter of a second into every load
            // of a save, which nobody saw until the fault lamp showed it, 2026-10-11. What takes
            // that load lets go of it under half the battery's volts.)
            battery_amps  = battery_contactor ? traction_current + _battery_low_voltage_watts / Math.Max(battery_volts, 0.5f * battery_open) : 0.0f;
            battery_volts = Math.Max(0.0f, battery_open - battery_amps * battery_ohms);
            if (battery_contactor)
            {
                battery_joules = Math.Max(0.0f, battery_joules - battery_open * battery_amps * delta_seconds);
                _battery_over  = battery_amps > battery_max_amps ? _battery_over + delta_seconds : 0.0f;
                if (_battery_over >= battery_trip_seconds && !protection_bypassed && !battery_tripped)
                {
                    battery_tripped = true;
                    battery_trip_amps = battery_amps;               // what it broke, for whoever asks why
                    battery_trip_traction_amps = traction_current;
                }
            }
            else
                _battery_over = 0.0f;
            float source_volts = battery_contactor ? battery_volts : line_volts;
            // NOT UNTIL THE SOLVER HAS THE CIRCUIT AS IT STANDS. A contactor's move is worked into
            // the solver's matrices on another thread, and until that is done it solves the
            // circuit as it was BEFORE - with the volts as they are now. Just made, "before" is
            // the diagram with nothing yet opened: a dead short. In the game, where the first
            // working-in takes some third of a second of a save's loading, the last-mile battery
            // (in from the first step, its switch being on) saw 36 000 A and its breaker went out
            // at every load; under the wire the same amperes went to the game's meter. (Found
            // by the fault lamp, 2026-10-11: it was lit after every load.) So no volts are given
            // to the circuit until its first working-in is finished.
            if (!_solver_caught_up)
            {
                _solver_caught_up = ++_first_steps > 2 && !solver_behind();
                source_volts = 0.0f;
                _battery_over = 0.0f;
            }

            // The relays look at what is in the wire now, not at what the meters show.
            float now_12 = Math.Abs(named["MA1"].current), now_34 = Math.Abs(named["MA3"].current);
            relay_amps = Math.Max(now_12, now_34);
            pair_amps_12 = now_12;
            pair_amps_34 = now_34;
            bool  over   = now_12 >= overcurrent_high
                        || !high_start_active && (now_12 >= overcurrent_normal || now_34 >= overcurrent_normal);
            _over_time = over && !protection_bypassed ? _over_time + delta_seconds : 0.0f;
            if (_over_time >= overcurrent_delay && !overcurrent_tripped)
            {
                overcurrent_tripped = true;
                arc_if_loaded(false);
            }

            // PWL and PRL, which hold the line contactors in. WS acts on them directly.
            bool permitted = (fast_breaker_closed || battery_contactor) && !overcurrent_tripped && !line_contactor_cutout && !hauled
                          && contactors.direction != 0 && !(_pair_12_out && _pair_34_out);
            may_take_power = permitted && !contactors.dropped_out;
            contactors.simulate(delta_seconds, permitted);

            high_start_seconds = high_start_active && contactors.position > 0 ? high_start_seconds + delta_seconds : 0.0f;
            high_start_buzzer  = high_start_seconds >= high_start_allowed_seconds;

            // His loop: filtered currents to the motors, their EMFs to the solver, one solve.
            //
            // The motors' field follows the current through that filter, about a second behind,
            // and the solver has no inductance: so a wire that sags for a fifth of a second and
            // comes back finds the field a little down, the back-EMF with it, and nothing to
            // slow the inrush - on position 43 at 106 km/h a steady 135 A dipped to nothing and
            // came back past the relays' 900 ("it tripped whilist at speed on position 43", the
            // author, 2026-10-10; debug/audio_trace.csv at 929 s). A real motor circuit cannot
            // change its current that fast. So while the line contactors are in, the traction
            // circuit sees the wire through the SAME filter as the field: the two move together
            // and a short sag passes. With the contactors out it sees the wire as it is, so that
            // closing them is not softened. The undervoltage relay keeps looking at the wire itself.
            // (and no faster than the stiffest motor's own filter, further down: the wire and the field must move TOGETHER)
            int in_series = contactors.position > 28 ? 2 : (_pair_12_out ? 0 : 2) + (_pair_34_out ? 0 : 2);
            float share = traction_volts / Math.Max(1, in_series), stiffest = current_filter;
            foreach (eu07_traction_motor motor in traction_motors)
            {
                if (motor != null)
                    stiffest = Math.Min(stiffest, field_loop_gain_limit / (1.0f + motor.loop_gain(currents, share)));
            }
            traction_volts = line_contactors_closed && supply_follows_field ? traction_volts + (source_volts - traction_volts) * stiffest : source_volts;
            named["OCS"].EMF = traction_volts;
            // THE GHOST OVERLOADS (the author, 2026-10-10: "the ghost motor overloads are back
            // again", with a steady wire). The filter below, the field it feeds and the solver
            // are a loop, a tick or more late at every turn - in the game the solver runs beside
            // the frame and its answer is older still. Its gain is the filter's 2 % times
            // (1 + the motor's loop_gain): harmless on resistance or near saturation, but with
            // little current at speed on 28 or 43 it passed what a late loop bears, the current
            // swung either way tick by tick, growing, and the relays - which look at the
            // unfiltered current - went (his trace: 55.7 km/h on 28, 117 A in the motors, the
            // relays' reading 147, 366, 565, 1018, 1755 A in half a second). So each motor's
            // currents are filtered no faster than keeps that gain at field_loop_gain_limit.
            // The steady state is the same; only how fast it is reached where the loop is stiff.
            if (field_unmade_by_reversal && line_contactors_closed)
            {
                foreach (eu07_traction_motor motor in traction_motors)
                    motor?.unmake_field(currents, named, share);
            }
            foreach (KeyValuePair<string, circuit.branch_user> branch in named)
            {
                float filter = current_filter;
                foreach (eu07_traction_motor motor in traction_motors)
                {
                    if (motor != null && motor.owns(branch.Key))
                    {
                        // With the line contactors out there is no loop and no current: it stops when they
                        // open, and the pull with it. Left to the filter - slowed, where the loop is stiff -
                        // the motors went on pulling for seven seconds at 128 km/h ("motors still provide
                        // power after loosing voltage", the author, 2026-10-10).
                        filter = line_contactors_closed ? Math.Min(filter, field_loop_gain_limit / (1.0f + motor.loop_gain(currents, share))) : 1.0f;
                    }
                }
                currents[branch.Key] += (branch.Value.current - currents[branch.Key]) * filter;
            }
            float torque = 0.0f, heat = 0.0f, shaft_power = 0.0f, taken_in = 0.0f;
            foreach (eu07_traction_motor motor in traction_motors)
            {
                motor.simulate(currents, named);
                torque      += motor.wheel_torque;
                heat        += motor.heat_emission;
                shaft_power -= motor.EMF * motor.load_current;      // an EMF against the current is work done
                taken_in    += Math.Max(0.0f, motor.EMF * motor.load_current);   // and one WITH it is work taken in: plugging
            }
            plugging_stress = taken_in / rated_power_watts;
            if (plugging_stress > plugging_free && !earth_fault_tripped)                 // one arc: the relay it trips ends it
            {
                _plugging_excess += (plugging_stress - plugging_free) * delta_seconds;
                if (_plugging_excess > flashover_excess)
                {
                    ++flashovers;
                    _pending_damage += flashover_damage;
                    _plugging_excess = 0.0f;
                    trip(relay.earth_fault);
                }
            }
            else
                _plugging_excess = Math.Max(0.0f, _plugging_excess - flashover_excess * delta_seconds);    // forgotten in a second
            solver.simulate();

            ammeter_12       = Math.Abs(currents["MA1"]);
            ammeter_34       = Math.Abs(currents["MA3"]);
            traction_current = Math.Abs(currents["OCS"]);
            line_current     = (battery_contactor ? 0.0f : traction_current) + auxiliary_current;      // on the battery nothing of it is the wire's
            wheel_torque     = torque * Math.Min(1.0f, Math.Max(0.0f, health) * 2.0f);
            motor_heat       = heat;
            // What comes in and is neither turned into work nor lost in the motors or on the
            // way in went into the starting resistors (and the field shunts).
            resistor_heat = Math.Max(0.0f, source_volts * traction_current - shaft_power - heat
                                         - traction_current * traction_current * _line_resistance);
            fan_volts = ammeter_34 * ((is_on("SR28") ? 0.0f : _fan_resistor_28) + (is_on("SR30") ? 0.0f : _fan_resistor_30));
            fan_by_current = Math.Min(1.5f, fan_volts / fan_rated_volts);
            fan_speed = fans_fed ? Math.Max(fan_by_current, fan_by_heat) : 0.0f;

            // Each relay across one pair of armatures. Unfiltered, like the overcurrent ones.
            slip_detected = Math.Abs(Math.Abs(named["MA1"].EMF) - Math.Abs(named["MA2"].EMF)) >= slip_relay_volts
                         || Math.Abs(Math.Abs(named["MA3"].EMF) - Math.Abs(named["MA4"].EMF)) >= slip_relay_volts;
        }

        private void simulate_auxiliaries(float delta_seconds)
        {
            bool control = _control_power.State;

            // The resistors' heat, and what it asks of the fans (which need something to run on).
            resistor_celsius = Math.Min(resistor_most_celsius, resistor_celsius
                + (resistor_heat - (resistor_still_air + resistor_blown_air * fan_speed) * (resistor_celsius - ambient_celsius)) / resistor_heat_capacity * delta_seconds);
            fan_by_heat = control && fans_fed && (fast_breaker_closed || battery_contactor)
                ? Math.Max(0.0f, Math.Min(fans_by_heat_most, (resistor_celsius - fans_from_celsius) / (fans_full_celsius - fans_from_celsius))) : 0.0f;

            // The grid that stays in: heated by the current squared, worn red hot, gone at grids_fail.
            // (not with the bench's protection out: it stands at currents no relay would allow, to read them)
            float grid_amps = on_resistors && !protection_bypassed ? Math.Max(ammeter_12, ammeter_34) / grid_reference_amps : 0.0f;
            float grid_kelvin = grid_celsius + 273.15f, air_kelvin = ambient_celsius + 273.15f;
            grid_celsius = Math.Min(grids_melt_celsius, grid_celsius + (grid_kelvin_per_second * grid_amps * grid_amps
                - (grid_still_air + grid_blown_air * fan_speed) * (grid_celsius - ambient_celsius)
                - grid_glow * (grid_kelvin * grid_kelvin * grid_kelvin * grid_kelvin - air_kelvin * air_kelvin * air_kelvin * air_kelvin)) * delta_seconds);
            if (grid_celsius < grids_hot_celsius)
                _grid_down = false;
            else
                _pending_damage += grid_wear_per_second * (grid_celsius - grids_hot_celsius) / (grids_fail_celsius - grids_hot_celsius) * delta_seconds;
            if (grid_celsius >= grids_fail_celsius && !_grid_down)
            {
                _grid_down = true;
                ++grids_burnt;
                _pending_damage += grid_burn_damage;
                trip(relay.earth_fault);
            }

            motor_celsius = Math.Min(motor_most_celsius, motor_celsius
                + (motor_heat - (motor_still_air + motor_blown_air * converter_speed) * (motor_celsius - ambient_celsius)) / motor_heat_capacity * delta_seconds);

            // The converter: runs up on the line, runs down without it.
            if (converter_knife_open && !_knife_was_open && _converter_was_driven)
            {
                ++knife_arcs;
                ++arcs;                                              // (shown as the cabinet's other arcs are)
                arc_amps = Math.Max(arc_from_amps, _converter_amps_were);
                arc_at_breaker = false;
                _pending_damage += knife_arc_damage;
                trip(relay.earth_fault);
            }
            _knife_was_open = converter_knife_open;
            bool driven = fast_breaker_closed && control && converter_switch && !converter_knife_open && !converter_tripped
                       && line_volts >= converter_minimum_volts;
            converter_speed = Math.Max(0.0f, Math.Min(1.0f, converter_speed + (driven ? delta_seconds : -delta_seconds) / converter_run_up_seconds));

            // The compressors hang on its 110 V.
            float low_voltage_load = control_watts;
            for (int index = 0; index < 2; ++index)
            {
                if (main_reservoir_bar >= compressor_cut_out_bar)
                    _compressor_wanted[index] = false;
                else if (main_reservoir_bar <= compressor_cut_in_bar)
                    _compressor_wanted[index] = true;
                compressor_running[index] = (converter_charging || battery_contactor) && control && compressor_switch[index]
                                         && _compressor_wanted[index] && !_compressor_tripped[index];
                if (compressor_running[index])
                    low_voltage_load += compressor_watts;
            }
            // the fans on the heat's account are a load of their own: on the line, or on the battery
            _fan_heat_watts = fan_by_heat > fan_by_current ? resistor_fan_watts * fan_by_heat * fan_by_heat * fan_by_heat : 0.0f;

            float converter_watts = !driven ? 0.0f
                : (blower_watts * converter_speed * converter_speed * converter_speed + (converter_charging ? low_voltage_load : 0.0f)) / converter_efficiency;
            float converter_amps  = converter_watts / Math.Max(line_volts, 1.0f);
            _converter_was_driven = driven;
            _converter_amps_were = converter_amps;
            // The same power on a sagging wire is more current: this is the relay that speaks
            // when both compressors run on a weak line.
            if (converter_amps >= converter_overcurrent && !protection_bypassed)
                converter_tripped = true;

            heating_on = fast_breaker_closed && control && heating_switch && !heating_tripped;
            float heating_amps = heating_on ? heating_load_watts / Math.Max(line_volts, 1.0f) : 0.0f;
            if (heating_amps >= heating_overcurrent && !protection_bypassed)
                heating_tripped = true;

            // On the battery the 110 V load is the battery's. Under the wire with the converter
            // up, a battery that is not full charges, which the wire pays for.
            _battery_low_voltage_watts = battery_contactor ? low_voltage_load + _fan_heat_watts : 0.0f;
            battery_charging = fast_breaker_closed && converter_charging && !battery_contactor && battery_charge < 1.0f;
            float charging_amps = 0.0f;
            if (battery_charging)
            {
                battery_joules = Math.Min(battery_kWh * 3.6e+6f, battery_joules + battery_charge_watts * battery_charge_gain * delta_seconds);
                charging_amps  = battery_charge_watts / Math.Max(line_volts, 1.0f);
            }
            cab_heating_on = fast_breaker_closed && control && cab_heating_switch;
            auxiliary_current = converter_amps + heating_amps + charging_amps + (cab_heating_on ? cab_heating_watts / Math.Max(line_volts, 1.0f) : 0.0f) + (fast_breaker_closed ? _fan_heat_watts / Math.Max(line_volts, 1.0f) : 0.0f);
        }
    }
}
