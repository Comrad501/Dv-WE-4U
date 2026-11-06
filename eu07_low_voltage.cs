// EU07 / 303E 110 V side: battery, its switch, breakers under the assistant's desk flap. No Unity.
//
// From sources: "Bateria" switch + two rows of paired breakers under the lid (reference cab);
// what each feeds, battery halves with 63 A fuses, converter charging via 100 A fuse + diode
// (303E schematics, NOTES).
// Assumed: each labelled pair = one two-pole breaker (unearthed 110 V); all figures round guesses;
// "Bateria" is the master (= the game's main-electrics fuse), off = 110 V dead even with the
// converter running; the last-mile battery's converter holds the 110 V (way back from flat).
using System;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    // order under the flap: far row, then near row, from the side wall
    internal enum low_breaker { fans, converter, compressors, cab_heating, pantographs, sanding, control, windscreens, lighting, headlights }

    internal class low_voltage
    {
        public const int   breakers       = 10;
        public const float nominal_volts  = 110.0f;
        public const float full_volts     = 118.0f, flat_volts = 100.0f;    // open circuit: full, flat
        public const float amp_hours      = 80.0f;                          // ASSUMED
        public const float ohms           = 0.15f;                          // internal
        public const float supplied_volts = 121.0f;                         // bus, converter charging
        public const float charger_amps   = 30.0f;                          // max charge current
        public const float charge_gain    = 0.85f;                          // fraction stored
        public const float pick_up_volts  = 77.0f, drop_out_volts = 66.0f;  // 0.7 / 0.6 of 110: relays and valves pick up / drop out

        // loads, W (assumed)
        public const float desk_watts       = 120.0f;       // relays, meters, lamps: whenever live
        public const float valve_watts      = 25.0f;        // pantograph valve, held
        public const float breaker_watts    = 80.0f;        // WS holding coil
        public const float contactors_watts = 250.0f;       // line contactors + those the wheel has in
        public const float headlight_watts  = 250.0f, headlight_dim_watts = 60.0f, tail_lamp_watts = 25.0f;
        public const float cab_light_watts  = 40.0f;
        public const float sanding_watts    = 30.0f;

        // ---- in ---------------------------------------------------------------------------
        public bool  battery_switch { get; set; } = true;
        public bool  supplied       { get; set; } = false;       // converter running (or last-mile battery's): holds bus, charges
        public float load_watts     { get; set; } = 0.0f;        // current load
        public float charge         { get; set; } = 1.0f;        // 0-1; saved by the owner
        public readonly bool[] breaker = { true, true, true, true, true, true, true, true, true, true };

        // ---- out --------------------------------------------------------------------------
        public float volts        { get; private set; } = 0.0f;  // on the bus
        public float battery_amps { get; private set; } = 0.0f;  // out of battery; negative: charging
        public bool  live         { get; private set; } = false; // enough for relays and valves
        public bool  charging     => battery_amps < 0.0f;

        // fed: bus live and breaker in
        public bool fed(low_breaker which) => live && breaker[(int) which];

        public void simulate(float delta_seconds)
        {
            float open = charge > 0.0f ? flat_volts + (full_volts - flat_volts) * charge : 0.0f;
            if (!battery_switch)
            {
                volts = battery_amps = 0.0f;
            }
            else if (supplied)
            {
                volts = supplied_volts;
                float into = charge >= 1.0f ? 0.0f : Math.Max(0.0f, Math.Min(charger_amps, (supplied_volts - open) / ohms));
                charge = Math.Min(1.0f, charge + into * charge_gain * delta_seconds / 3600.0f / amp_hours);
                battery_amps = -into;
            }
            else
            {
                // battery alone: V = open - I R, I = P / V
                float root = open * open - 4.0f * ohms * Math.Max(0.0f, load_watts);
                volts = open <= 0.0f ? 0.0f : root > 0.0f ? 0.5f * (open + (float) Math.Sqrt(root)) : 0.5f * open;
                battery_amps = volts > 1.0f ? Math.Max(0.0f, load_watts) / volts : 0.0f;
                charge = Math.Max(0.0f, charge - battery_amps * delta_seconds / 3600.0f / amp_hours);
            }
            live = live ? volts >= drop_out_volts : volts >= pick_up_volts;
        }
    }

    // pantograph air: raises and holds the pantographs; no air, no WS (WCP). Dead loco: main
    // reservoir empty, so a small 110 V compressor fills the pantograph circuit first.
    // From the 303E DTR and transportszynowy.pl (after the WKL book):
    //   - fed from the control reservoir (100 l) via filter, 5 bar reducing valve, check valve;
    //   - raise from 4.5 bar; WCP1,2 drop WS control under 3.5, restore at 4.5;
    //   - small compressor 1JS60 / KP1 (95-100 l/min, 0.67 kW), own switch WCT 3.8 in / 4.8 out;
    //     feeds the cylinders via a three-way cock on frame B; runs only with WS open.
    // Assumed: pump rate, cost of a raise, leak, drop pressure.
    internal class pantograph_air
    {
        public const float feed_bar          = 5.0f;        // from the main reservoir, via reducing + check valve
        public const float feed_per_second   = 0.5f;
        public const float pump_out_bar      = 4.8f, pump_in_bar = 3.8f;    // WCT
        public const float pump_per_second   = 0.16f;       // 0 -> 4.8 bar in ~30 s
        public const float pump_watts        = 670.0f;
        public const float raise_bar         = 4.5f, hold_bar = 2.5f;       // raises above, drops below
        public const float breaker_bar       = 4.5f, breaker_out_bar = 3.5f; // WCP
        public const float raising_costs_bar = 0.15f;       // one cylinder, on the small compressor
        public const float leak_per_second   = 0.0005f;     // ~2 bar/h

        // ---- in ---------------------------------------------------------------------------
        public float main_reservoir_bar { get; set; } = 0.0f;       // gauge
        public bool  pump_wanted        { get; set; } = false;      // switch; needs 110 V and WS open
        public int   pantographs_up     { get; set; } = 0;          // valves held open
        // three-way cock: pantograph circuit on the main reservoir or the small compressor, never
        // both (left on the compressor, the pantographs drop once its vessel leaks away)
        public bool  cock_to_small      { get; set; } = false;
        // frame cocks:
        //   CONTROL AIR     shut: no feed from the main reservoir (small compressor still works)
        //   PANTOGRAPH AIR  shut: pantographs drop
        //   DRAIN           open: circuit empties faster than it fills
        public bool  feed_open          { get; set; } = true;
        public bool  pantographs_open   { get; set; } = true;
        public bool  drain_open         { get; set; } = false;
        public const float drain_per_second = 0.8f;
        public float bar                { get; set; } = 0.0f;       // gauge; saved by the owner

        // ---- out --------------------------------------------------------------------------
        public bool pump_running { get; private set; } = false;
        public bool breaker_air  { get; private set; } = false;     // WCP: enough for WS
        // raise allowed while WCP is made (hysteresis 4.5 / 3.5), so no dead band above WCT's 3.8
        public bool can_raise    => breaker_air && pantographs_open;
        public bool can_hold     => bar >= hold_bar && pantographs_open;

        private int   _were_up = 0;
        private bool  _started = false;
        private bool  _pump_asked = true;       // WCT state (hysteresis)
        private float _delivery_bar = 0.0f;     // small compressor delivery pipe

        public void simulate(float delta_seconds)
        {
            if (!_started)
            {
                breaker_air = bar >= breaker_out_bar;       // on load: switches as they would be
                _started = true;
            }
            // WCT sees its own delivery: the pantograph circuit if the cock is on it, else a stub that fills in ~1-2 s
            if (cock_to_small)
                _delivery_bar = bar;
            if (_delivery_bar >= pump_out_bar)
                _pump_asked = false;
            else if (_delivery_bar <= pump_in_bar)
                _pump_asked = true;
            pump_running = pump_wanted && _pump_asked;
            if (pump_running && cock_to_small)
                bar = Math.Max(bar, bar + pump_per_second * delta_seconds);
            else if (pump_running)
                _delivery_bar += 3.0f * delta_seconds;
            if (!cock_to_small)
                _delivery_bar = Math.Max(0.0f, _delivery_bar - 0.002f * delta_seconds);
            float fed = cock_to_small || !feed_open ? 0.0f : Math.Min(main_reservoir_bar, feed_bar);
            bool  feeding = fed > bar;
            if (feeding)
                bar = Math.Min(fed, bar + feed_per_second * delta_seconds);
            if (pantographs_up > _were_up && !feeding)
                bar = Math.Max(0.0f, bar - raising_costs_bar * (pantographs_up - _were_up));
            _were_up = pantographs_up;
            bar = Math.Max(0.0f, bar - (leak_per_second + (drain_open ? drain_per_second : 0.0f)) * delta_seconds);
            breaker_air = breaker_air ? bar >= breaker_out_bar : bar >= breaker_bar;
        }
    }
}
