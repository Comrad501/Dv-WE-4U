// EU07 / 303E high-voltage side, no Unity.
// Circuit, contactors, four motors, plus (303E DTR, ch. 7):
//   WS        fast breaker; held by control volts, air, PZN (out <1900 V, in >2100 V real; halved here)
//   PON1,2,3  motor overcurrent: 600 A per group, 750 A on 1-2; WWR blocks 600 A in series;
//             opens line contactors (not WS), LS8, reset OPN
//   PLW       line contactors open by hand
//   SWO       resistor fans across R28+R30: stop by themselves on 28 and 43
//   SCR1,2    slip relays: EMF of the two armatures of a pair (LS9)
//   PRG       earth fault: opens WS, LS7, reset OPN
//   MP        converter: 3 kV motor -> 110 V generator + motor blowers; relay opens WS (LSPO)
//   MS1,2     compressors on the converter's 110 V, switch WCC; relays light LSC
//   SGP       train heating behind WS; relay opens WS
// Auxiliaries: line-current load, not solved (as Knsgf's unit_A_sim compressor).
// Relays with no simulated cause: trip() from outside (game damage, bench).
// In-game class binds ports and handles; bench/eu07_bench.cs drives it bare.

using System;
using System.Collections.Generic;

using LocoSim.Implementations;

using electric_sim.circuit_sim;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class power_circuit
    {
        // real settings, scaled to this model's amps and volts
        public const float overcurrent_normal   =  600.0f * current_ratio;
        public const float overcurrent_high     =  750.0f * current_ratio;
        public const float undervoltage_dropout = 1900.0f * line_voltage_ratio;
        public const float undervoltage_pickup  = 2100.0f * line_voltage_ratio;
        // no-volt hold: rides the game's ~20 m dead catenary joints above ~40 km/h (not real)
        public const float undervoltage_hold_seconds = 2.0f;
        private float _undervoltage_seconds = 0.0f;
        public const float high_start_allowed_seconds = 300.0f;      // then buzzer DN

        // auxiliaries keep real power: half volts, double amps, relays at double
        public const float converter_overcurrent   =   21.0f / line_voltage_ratio;   // NPR, 21 A real
        public const float heating_overcurrent     =  200.0f / line_voltage_ratio;   // NGP, 200 A real
        public const float converter_minimum_volts = 2000.0f * line_voltage_ratio;   // below: set runs down
        public const float blower_watts = 20.2e+3f, compressor_watts = 110.0f * 142.0f, control_watts = 2.0e+3f;
        public const float converter_efficiency = 0.9f, converter_run_up_seconds = 4.0f;
        // gauge bar; real WCC cut-out 8.5, game reservoir tops out at 8.0
        public const float compressor_cut_out_bar = 8.0f;
        public const float compressor_cut_in_bar  = 7.0f;            // assumed: DTR gives only the cut-out

        // resistor fans: four series motors, 189 V / 52 A real, across R28 + R30; LS6 under 16.5 A
        public const float fan_rated_volts  = 189.0f * line_voltage_ratio, fan_rated_amps = 52.0f / line_voltage_ratio;
        public const float fan_minimum_amps = 16.5f / line_voltage_ratio;

        // assumed (not in DTR): EMF of ~4 km/h between two axles
        public const float slip_relay_volts = 60.0f;

        public enum relay { earth_fault, converter, heating, compressor_1, compressor_2 }

        // per-tick branch-current filter (Knsgf): stops series motor / lagging solver ringing
        private const float current_filter = 0.02f;
        // relays not instant; solver's first step after a contactor can overshoot
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

        // plugging: reversed under power, EMF adds to the line, torque brakes; allowed (reverser
        // interlocked with wheel only). Free up to rated power; excess integrated -> flashover. Rates chosen
        public const float rated_power_watts    = 2000.0e+3f * line_voltage_ratio * current_ratio;   // four motors, model scale
        public const float plugging_free        = 1.0f;     // x rated power: free up to here
        public const float flashover_excess     = 0.5f;     // rated-power-seconds over that before flashover
        public const float flashover_damage     = 30.0f;    // hp of the game's 1000
        private float _plugging_excess = 0.0f;

        // last-mile traction battery (not on the real loco); feeds the traction circuit instead of the line
        //   - wheel at 0 and WS open only; never both; series (<=28), full field; own breaker; carries 110 V; charges under the line
        public const float battery_kWh          = 60.0f;                     // usable
        public const float battery_full_volts   = 430.0f, battery_empty_volts = 380.0f;      // open circuit: full, flat
        public const float battery_ohms         = 0.10f;                     // internal
        public const float battery_max_amps     = 700.0f;
        public const float battery_trip_seconds = 0.3f;
        public const float battery_charge_watts = 100.0e+3f;                 // from the line while charging
        public const float battery_charge_gain  = 0.9f;                      // fraction stored
        public const int   battery_top_position = series_running;
        public bool  battery_wanted     { get; set; } = false;               // switch
        public float battery_joules     { get; set; } = battery_kWh * 3.6e+6f;    // stored energy; saved by the owner
        public bool  battery_contactor  { get; private set; } = false;       // in: traction on battery
        public bool  battery_tripped    { get; private set; } = false;
        public float battery_trip_amps  { get; private set; } = 0.0f;        // at last trip: total, traction share
        public float battery_trip_traction_amps { get; private set; } = 0.0f;
        public float battery_volts      { get; private set; } = battery_full_volts;   // terminal volts
        public float battery_amps       { get; private set; } = 0.0f;
        public bool  battery_charging   { get; private set; } = false;
        public float battery_charge     => Math.Max(0.0f, Math.Min(1.0f, battery_joules / (battery_kWh * 3.6e+6f)));
        private float _battery_over = 0.0f, _battery_low_voltage_watts = 0.0f;
        private bool  _solver_caught_up = false;
        private int   _first_steps = 0;
        // solver still has contactor moves to work in? (reflection: no public API)
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
        public float traction_volts        { get; private set; }         // fed to traction circuit (see simulate)
        public bool  supply_follows_field  { get; set; } = true;         // false: raw line (bench comparison)
        // max loop gain filter -> field -> EMF -> current per tick (see simulate); infinity: no limit
        public float field_loop_gain_limit { get; set; } = 0.2f;
        // reverse current unmakes the field at once (eu07_traction_motor.unmake_field)
        public bool  field_unmade_by_reversal { get; set; } = true;
        public float relay_amps            { get; private set; }         // overcurrent relays: greater group, the current as it flows (filtered)
        public bool  high_start            { get; set; } = false;        // WWR
        public bool  line_contactor_cutout { get; set; } = false;        // PLW, while held
        // hauled switch (jazda ciagniona): control contactor open, towed with battery on, no power
        // (ZNTK Olesnica EP07 instruction, SimRail EU07 notes; NOTES)
        public bool  hauled                { get; set; } = false;
        // hauled or 110 V dead: vigilance/SHP must not act
        public bool  train_protection_live(bool low_voltage_live) => low_voltage_live && !hauled;
        public bool  protection_bypassed   { get; set; } = false;        // bench only
        public bool  pantograph_air_ok     { get; set; } = true;         // WCP: no air, no WS
        public bool  converter_switch      { get; set; } = false;        // WDG
        // converter knife switch (LV cabinet): no arc chute; opened running -> arc, earth fault, damage
        public bool  converter_knife_open  { get; set; } = false;
        public const float knife_arc_damage = 40.0f;                     // hit points of the game's 1000
        public int   knife_arcs            { get; private set; } = 0;
        private bool _knife_was_open = false, _converter_was_driven = false;
        private float _converter_amps_were = 0.0f;
        public bool  heating_switch        { get; set; } = false;        // WDO3,4
        // cab heaters: HV behind WS, 3 A fuse per cab; load only
        public const float cab_heating_watts = 3.0e+3f;
        public bool  cab_heating_switch    { get; set; } = false;
        public bool  cab_heating_on        { get; private set; } = false;
        // resistor fan relays' own 110 V breaker (WSW): out, fans stop
        public bool  fans_fed              { get; set; } = true;
        public float heating_load_watts    { get; set; } = 0.0f;         // train load
        public float main_reservoir_bar    { get; set; } = 0.0f;         // gauge bar, for WCC
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
        public float fan_speed            { get; private set; }          // resistor fans, 1 = rated

        // resistor bank heat, one lump; fans also run by heat and run on after (not real: makes hard work heard)
        // all figures round guesses
        public const float resistor_heat_capacity = 3.0e+5f;         // J/K, ~600 kg of grids
        public const float resistor_still_air     = 300.0f;          // W/K, fans stopped
        public const float resistor_blown_air     = 3500.0f;         // W/K extra, fans at rated
        public const float ambient_celsius        = 20.0f;
        public const float fans_from_celsius      = 35.0f;           // fans start
        public const float fans_full_celsius      = 110.0f;          // fans at rated; max 1.3x
        public const float fans_by_heat_most      = 1.3f;
        public const float resistor_most_celsius  = 800.0f;          // cap
        public const float resistor_fan_watts     = 4.0f * 8.4e+3f;  // four fan motors at rated, heat-driven: off the line
        private float _fan_heat_watts = 0.0f;
        public float resistor_celsius     { get; private set; } = ambient_celsius;

        // one grid cut out last (full motor current every notch): I^2 heat, air + T^4 glow cooling
        // ~5 min at 750 A / ~10 at 600 A to grids_hot; 450 A + fans holds; wear from hot, earth fault at fail (NOTES)
        public const float grid_reference_amps    = 500.0f * current_ratio;
        public const float grid_kelvin_per_second = 2.6f;            // at that current; scales with I^2
        public const float grid_still_air         = 0.0003f;         // 1/s of excess over ambient, fans stopped
        public const float grid_blown_air         = 0.0010f;         // 1/s extra, fans at rated
        public const float grid_glow              = 7.0e-13f;        // K/s per K^4 (radiation)
        public const float grids_warm_celsius     = 400.0f;          // warning to driver
        public const float grids_hot_celsius      = 600.0f;          // dull red: wear starts
        public const float grids_fail_celsius     = 800.0f;          // bright red: grid sags, earth fault
        public const float grids_melt_celsius     = 1150.0f;         // melts
        public const float grid_wear_per_second   = 0.5f;            // hp of 1000 at grids_fail; proportional from grids_hot
        public const float grid_burn_damage       = 200.0f;          // hp per burnt grid
        public float grid_celsius         { get; set; } = ambient_celsius;      // settable: bench, debug hook
        // line contactors would close off 0 (asked by eu07_auto_notching.cs)
        public bool  may_take_power       { get; private set; } = false;
        public float pair_amps_12         { get; private set; }                 // per pair, unfiltered (as relays see it)
        public float pair_amps_34         { get; private set; }
        public float feed_volts => battery_contactor ? battery_volts : line_volts;     // traction feed: line or battery
        public int   grids_burnt          { get; private set; } = 0;
        private bool _grid_down = false;                             // a grid burnt, not below grids_hot since

        // motor heat, one lump: ~0.19 ohm x I^2, cooled by converter blowers; game scale wear 60 C, fuse 120 C
        // 510 A -> 75 C, 600 A -> 96 C, ~690 A -> 120 C; tau ~20 min blown, ~90 not (EU07_BENCH_MOTOR_HEAT)
        public const float motor_heat_capacity = 1.056e+6f;          // J/K
        public const float motor_still_air     = 180.0f;             // W/K, blowers stopped
        public const float motor_blown_air     = 700.0f;             // W/K extra, converter at speed
        public const float motor_most_celsius  = 300.0f;
        public float motor_celsius        { get; set; } = ambient_celsius;    // saved by the owner
        public float fan_by_current       { get; private set; }      // from the real circuit
        public float fan_by_heat          { get; private set; }      // from the heat
        public float traction_current     { get; private set; }          // A, motors' share of line_current
        public float auxiliary_current    { get; private set; }          // A, converter and heating
        public float converter_speed      { get; private set; } = 0.0f;  // 0-1; also the motor blowers
        public bool  earth_fault_tripped  { get; private set; } = false; // lamp LS7
        public float plugging_stress      { get; private set; } = 0.0f;  // shaft power taken in / rated power
        public int   flashovers           { get; private set; } = 0;
        // arc: protection breaks >= arc_from_amps with line contactors in (not the wheel shutting off)
        public const float arc_from_amps = 300.0f;
        public int   arcs                 { get; private set; } = 0;
        public float arc_amps             { get; private set; } = 0.0f;
        public bool  arc_at_breaker       { get; private set; } = false;        // last arc: WS (else line contactors)
        private void arc_if_loaded(bool breaker)
        {
            if (line_contactors_closed && relay_amps >= arc_from_amps)
            {
                arc_amps = relay_amps;
                arc_at_breaker = breaker;
                ++arcs;
            }
        }
        // 0-1 game drivetrain health: <0.5 less torque; 0 earth fault stays
        public float health               { get; set; } = 1.0f;
        public bool  converter_tripped    { get; private set; } = false; // lamp LSPO, with heating
        public bool  heating_tripped      { get; private set; } = false;
        public bool  heating_on           { get; private set; } = false; // lamp LSGP
        public bool  slip_detected        { get; private set; } = false; // lamp LS9
        public readonly bool[] compressor_running = new bool[2];

        // converter: only 110 V source besides the battery
        public bool converter_charging => converter_speed >= 0.9f;
        public bool on_resistors       => line_contactors_closed                                         // lamp LS5
                                       && contactors.position != series_running && contactors.position != parallel_running;
        public bool fans_failed        => on_resistors && (!fans_fed || fan_by_current * fan_rated_amps < fan_minimum_amps);  // lamp LS6 (real circuit only)
        public bool compressor_tripped => _compressor_tripped[0] || _compressor_tripped[1];               // lamp LSC

        public bool undervoltage           => line_volts < undervoltage_dropout;
        public bool line_contactors_closed => is_on("SL1");                              // LS10 = not this
        public bool high_start_active      => high_start && !is_on("SL3") && contactors.direction != 0;   // lamp LS4

        // wheel_RPM: one port, or one per axle (one: slip relays have nothing to compare)
        public power_circuit(Fuse control_power, Fuse control_air, Port[] wheel_RPM, Action<bool>? contactor_sound = null,
            int? random_seed = null)
        {
            if (wheel_RPM.Length != 1 && wheel_RPM.Length != motors)
                throw new ArgumentException("one wheel speed, or one an axle");
            _control_power = control_power;
            _control_air   = control_air;

            // as Knsgf's unit_A_sim: element values +-1%
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

        // WS and battery contactor: one solver element, source = its EMF (simulate)
        private void feed()
        {
            contactor_locations["WS"].toggle_contactor("WS", fast_breaker_closed || battery_contactor);
        }

        // ---- the handles and buttons -------------------------------------------------------
        public void set_controller(int position)  => contactors.set_controller(battery_contactor ? Math.Min(position, battery_top_position) : position);
        public void set_field_weakening(int step) => contactors.set_field_weakening(battery_contactor ? 0 : step);
        public bool set_reverser(int direction)   => contactors.set_reverser(direction);

        // WJ7,8: needs control volts, air, line volts
        public bool close_fast_breaker()
        {
            if (!fast_breaker_closed && !battery_contactor && _control_power.State && _control_air.State && pantograph_air_ok
                && line_volts >= undervoltage_pickup && !earth_fault_tripped && !converter_tripped && !heating_tripped)
                throw_fast_breaker(true);
            return fast_breaker_closed;
        }

        // WJ5,6
        public void open_fast_breaker()
        {
            if (fast_breaker_closed)
                throw_fast_breaker(false);
        }

        // OPN: overcurrent + earth fault reset; reverser off neutral, wheel at 0 only
        public bool reset_relays()
        {
            if (contactors.direction != 0 && contactors.wanted == 0 && contactors.position == 0)
            {
                overcurrent_tripped = false;
                earth_fault_tripped = health <= 0.0f;        // dead drivetrain: stays faulted
            }
            return !overcurrent_tripped && !earth_fault_tripped;
        }

        // damage since last call, for the game's pool
        public float take_damage()
        {
            float damage = _pending_damage;
            _pending_damage = 0.0f;
            return damage;
        }

        // WJ1-WJ4: converter, compressor, heating relays
        public void reset_auxiliary_relays()
        {
            converter_tripped = heating_tripped = _compressor_tripped[0] = _compressor_tripped[1] = false;
        }

        // trip from outside: game damage, bench test
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

        // OS1-OS4, machine room: dead locomotive only
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

            // battery: contactor, breaker, terminal volts (open-circuit for charge, less internal drop)
            if (!battery_wanted)
                battery_tripped = false;
            float battery_open = battery_empty_volts + (battery_full_volts - battery_empty_volts) * battery_charge;
            bool  battery_may  = battery_wanted && _control_power.State && !fast_breaker_closed && battery_joules > 0.0f
                              && !battery_tripped && !earth_fault_tripped;
            if (battery_contactor && !battery_may)
                throw_battery(false);
            else if (!battery_contactor && battery_may && contactors.position == 0 && contactors.wanted == 0)
                throw_battery(true);
            // 110 V load constant power: floor at half open-circuit volts, no runaway amps on a sag
            battery_amps  = battery_contactor ? traction_current + _battery_low_voltage_watts / Math.Max(battery_volts, 0.5f * battery_open) : 0.0f;
            battery_volts = Math.Max(0.0f, battery_open - battery_amps * battery_ohms);
            if (battery_contactor)
            {
                battery_joules = Math.Max(0.0f, battery_joules - battery_open * battery_amps * delta_seconds);
                _battery_over  = battery_amps > battery_max_amps ? _battery_over + delta_seconds : 0.0f;
                if (_battery_over >= battery_trip_seconds && !protection_bypassed && !battery_tripped)
                {
                    battery_tripped = true;
                    battery_trip_amps = battery_amps;               // for diagnostics
                    battery_trip_traction_amps = traction_current;
                }
            }
            else
                _battery_over = 0.0f;
            float source_volts = battery_contactor ? battery_volts : line_volts;
            // no volts until solver caught up (it works on another thread; fresh diagram = dead short)
            if (!_solver_caught_up)
            {
                _solver_caught_up = ++_first_steps > 2 && !solver_behind();
                source_volts = 0.0f;
                _battery_over = 0.0f;
            }

            // relays: the motors' current as it flows - the filtered currents, which stand for the
            // windings' inductance and make the torque, the heat and the meters. NOT the solver's
            // raw branch current: that is the circuit with no inductance at all, computed a step or
            // more late on its own thread, and it spikes at every notch, at every slip and regrip,
            // and backwards at 29 -> 28 before unmake_field answers. In the game those spikes last
            // longer than the bench's single tick and tripped the relays with the meters at
            // 500-700 A (a player's report, 2026-11-11, after the three fixes of 2026-10-10).
            float now_12 = Math.Abs(currents["MA1"]), now_34 = Math.Abs(currents["MA3"]);
            relay_amps = Math.Max(now_12, now_34);
            // the auto-notching for a foreign throttle stays on the raw current it was tuned on:
            // its spikes make it wait a notch, and on the filtered one it notches into 877 A
            pair_amps_12 = Math.Abs(named["MA1"].current);
            pair_amps_34 = Math.Abs(named["MA3"].current);
            bool  over   = now_12 >= overcurrent_high
                        || !high_start_active && (now_12 >= overcurrent_normal || now_34 >= overcurrent_normal);
            _over_time = over && !protection_bypassed ? _over_time + delta_seconds : 0.0f;
            if (_over_time >= overcurrent_delay && !overcurrent_tripped)
            {
                overcurrent_tripped = true;
                arc_if_loaded(false);
            }

            // PWL, PRL: hold the line contactors in; WS acts directly
            bool permitted = (fast_breaker_closed || battery_contactor) && !overcurrent_tripped && !line_contactor_cutout && !hauled
                          && contactors.direction != 0 && !(_pair_12_out && _pair_34_out);
            may_take_power = permitted && !contactors.dropped_out;
            contactors.simulate(delta_seconds, permitted);

            high_start_seconds = high_start_active && contactors.position > 0 ? high_start_seconds + delta_seconds : 0.0f;
            high_start_buzzer  = high_start_seconds >= high_start_allowed_seconds;

            // Knsgf's loop: filtered currents -> motors, EMFs -> solver, one solve
            // line contactors in: traction sees the line through the field's filter (no inductance in solver)
            int in_series = contactors.position > 28 ? 2 : (_pair_12_out ? 0 : 2) + (_pair_34_out ? 0 : 2);
            float share = traction_volts / Math.Max(1, in_series), stiffest = current_filter;
            foreach (eu07_traction_motor motor in traction_motors)
            {
                if (motor != null)
                    stiffest = Math.Min(stiffest, field_loop_gain_limit / (1.0f + motor.loop_gain(currents, share)));
            }
            traction_volts = line_contactors_closed && supply_follows_field ? traction_volts + (source_volts - traction_volts) * stiffest : source_volts;
            named["OCS"].EMF = traction_volts;
            // filter capped: keeps the delayed field/solver loop stable (gain filter x (1 + loop_gain))
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
                        // contactors out: no loop, current stops at once
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
                shaft_power -= motor.EMF * motor.load_current;      // EMF against current: work done
                taken_in    += Math.Max(0.0f, motor.EMF * motor.load_current);   // EMF with current: taken in (plugging)
            }
            plugging_stress = taken_in / rated_power_watts;
            if (plugging_stress > plugging_free && !earth_fault_tripped)                 // one flashover: its trip ends it
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
                _plugging_excess = Math.Max(0.0f, _plugging_excess - flashover_excess * delta_seconds);    // decays in ~1 s
            solver.simulate();

            ammeter_12       = Math.Abs(currents["MA1"]);
            ammeter_34       = Math.Abs(currents["MA3"]);
            traction_current = Math.Abs(currents["OCS"]);
            line_current     = (battery_contactor ? 0.0f : traction_current) + auxiliary_current;      // on battery: traction not from the line
            wheel_torque     = torque * Math.Min(1.0f, Math.Max(0.0f, health) * 2.0f);
            motor_heat       = heat;
            // power in - shaft work - motor and line losses = resistor (and shunt) heat
            resistor_heat = Math.Max(0.0f, source_volts * traction_current - shaft_power - heat
                                         - traction_current * traction_current * _line_resistance);
            fan_volts = ammeter_34 * ((is_on("SR28") ? 0.0f : _fan_resistor_28) + (is_on("SR30") ? 0.0f : _fan_resistor_30));
            fan_by_current = Math.Min(1.5f, fan_volts / fan_rated_volts);
            fan_speed = fans_fed ? Math.Max(fan_by_current, fan_by_heat) : 0.0f;

            // slip relays: one per armature pair, unfiltered
            slip_detected = Math.Abs(Math.Abs(named["MA1"].EMF) - Math.Abs(named["MA2"].EMF)) >= slip_relay_volts
                         || Math.Abs(Math.Abs(named["MA3"].EMF) - Math.Abs(named["MA4"].EMF)) >= slip_relay_volts;
        }

        private void simulate_auxiliaries(float delta_seconds)
        {
            bool control = _control_power.State;

            // resistor heat -> fan speed (fans need a supply)
            resistor_celsius = Math.Min(resistor_most_celsius, resistor_celsius
                + (resistor_heat - (resistor_still_air + resistor_blown_air * fan_speed) * (resistor_celsius - ambient_celsius)) / resistor_heat_capacity * delta_seconds);
            fan_by_heat = control && fans_fed && (fast_breaker_closed || battery_contactor)
                ? Math.Max(0.0f, Math.Min(fans_by_heat_most, (resistor_celsius - fans_from_celsius) / (fans_full_celsius - fans_from_celsius))) : 0.0f;

            // last grid: I^2 heat, wear when hot, fails at grids_fail
            // not with bench protection bypassed
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

            // converter: runs up on the line, down without
            if (converter_knife_open && !_knife_was_open && _converter_was_driven)
            {
                ++knife_arcs;
                ++arcs;
                arc_amps = Math.Max(arc_from_amps, _converter_amps_were);
                arc_at_breaker = false;
                _pending_damage += knife_arc_damage;
                trip(relay.earth_fault);
            }
            _knife_was_open = converter_knife_open;
            bool driven = fast_breaker_closed && control && converter_switch && !converter_knife_open && !converter_tripped
                       && line_volts >= converter_minimum_volts;
            converter_speed = Math.Max(0.0f, Math.Min(1.0f, converter_speed + (driven ? delta_seconds : -delta_seconds) / converter_run_up_seconds));

            // compressors on its 110 V
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
            // heat-driven fans: own load, line or battery
            _fan_heat_watts = fan_by_heat > fan_by_current ? resistor_fan_watts * fan_by_heat * fan_by_heat * fan_by_heat : 0.0f;

            float converter_watts = !driven ? 0.0f
                : (blower_watts * converter_speed * converter_speed * converter_speed + (converter_charging ? low_voltage_load : 0.0f)) / converter_efficiency;
            float converter_amps  = converter_watts / Math.Max(line_volts, 1.0f);
            _converter_was_driven = driven;
            _converter_amps_were = converter_amps;
            // constant power: sagging line -> more amps -> trip
            if (converter_amps >= converter_overcurrent && !protection_bypassed)
                converter_tripped = true;

            heating_on = fast_breaker_closed && control && heating_switch && !heating_tripped;
            float heating_amps = heating_on ? heating_load_watts / Math.Max(line_volts, 1.0f) : 0.0f;
            if (heating_amps >= heating_overcurrent && !protection_bypassed)
                heating_tripped = true;

            // battery: carries 110 V load; under the line with converter up, charges
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
