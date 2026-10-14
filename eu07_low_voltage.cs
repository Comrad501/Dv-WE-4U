// EU07 / 303E - the 110 V side: the battery, its switch, and the breakers under the flap in
// the assistant's desk. No Unity in it, as the power circuit.
//
// ("Well then, breakers, batteries and controls it is. this side panel over here is normally
// under a cover. check reference", the author, 2026-10-11.)
//
// WHAT IS READ, and where:
//   - the reference cab has, let into the assistant's desk top under a hinged lid, a box with
//     a "Bateria" switch, two red tumblers, and two rows of miniature breakers, labelled in
//     pairs: fans' start, converter, compressor, cab heating, pantograph; sanding, control,
//     window heating, general lighting, headlights (and one more, unlabelled);
//   - the 303E schematics (NOTES, 2026-10-11 01:00) give what each such breaker feeds, the
//     battery's two halves with a 63 A fuse each, and that a running converter charges it
//     through a 100 A fuse and a diode.
// WHAT IS OURS:
//   - a pair is taken as ONE two-pole breaker (the two poles of an unearthed 110 V circuit).
//     Whether the pairs are that, or one breaker a cab, nothing read says;
//   - every figure below: the battery's size and volts, what each thing takes. Round guesses;
//   - the "Bateria" switch is the MASTER: off, the 110 V is dead whatever the converter does.
//     On the real machine a running converter would keep the bus up without the battery;
//     here the switch is the game's own main-electrics fuse, which every locomotive of the
//     game's has and which means exactly that;
//   - the last-mile battery's own converter keeps the 110 V up while ITS switch is on: there
//     must be a way back from a flat battery, and that one is already on the locomotive.
using System;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    // In the order they stand under the flap, far row then near row, from the cab's side wall.
    internal enum low_breaker { fans, converter, compressors, cab_heating, pantographs, sanding, control, windscreens, lighting, headlights }

    internal class low_voltage
    {
        public const int   breakers       = 10;
        public const float nominal_volts  = 110.0f;
        public const float full_volts     = 118.0f, flat_volts = 100.0f;    // open circuit, full and all but empty
        public const float amp_hours      = 80.0f;                          // ASSUMED
        public const float ohms           = 0.15f;                          // inside it
        public const float supplied_volts = 121.0f;                         // the bus with the converter charging
        public const float charger_amps   = 30.0f;                          // the most that goes into the battery
        public const float charge_gain    = 0.85f;                          // of that, what stays
        public const float pick_up_volts  = 77.0f, drop_out_volts = 66.0f;  // 0.7 and 0.6 of 110: where relays and valves take and let go

        // What hangs on it, watts. ASSUMED, all of it.
        public const float desk_watts       = 120.0f;       // relays, the meters' supply, a lamp or two: whenever it is live
        public const float valve_watts      = 25.0f;        // a pantograph's valve, held
        public const float breaker_watts    = 80.0f;        // the line breaker's holding coil
        public const float contactors_watts = 250.0f;       // the line contactors and what the wheel has in
        public const float headlight_watts  = 250.0f, headlight_dim_watts = 60.0f, tail_lamp_watts = 25.0f;
        public const float cab_light_watts  = 40.0f;
        public const float sanding_watts    = 30.0f;

        // ---- in ---------------------------------------------------------------------------
        public bool  battery_switch { get; set; } = true;
        public bool  supplied       { get; set; } = false;       // a running converter (or the last-mile battery's own) holds the bus and charges
        public float load_watts     { get; set; } = 0.0f;        // what is on now
        public float charge         { get; set; } = 1.0f;        // 0-1; whoever keeps it over a save sets it
        public readonly bool[] breaker = { true, true, true, true, true, true, true, true, true, true };

        // ---- out --------------------------------------------------------------------------
        public float volts        { get; private set; } = 0.0f;  // on the bus
        public float battery_amps { get; private set; } = 0.0f;  // out of the battery; negative: into it
        public bool  live         { get; private set; } = false; // enough of it for relays and valves
        public bool  charging     => battery_amps < 0.0f;

        // Is this breaker's circuit fed: the bus live, and the breaker in.
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
                // the battery alone: V = open - I R with I = P / V
                float root = open * open - 4.0f * ohms * Math.Max(0.0f, load_watts);
                volts = open <= 0.0f ? 0.0f : root > 0.0f ? 0.5f * (open + (float) Math.Sqrt(root)) : 0.5f * open;
                battery_amps = volts > 1.0f ? Math.Max(0.0f, load_watts) / volts : 0.0f;
                charge = Math.Max(0.0f, charge - battery_amps * delta_seconds / 3600.0f / amp_hours);
            }
            live = live ? volts >= drop_out_volts : volts >= pick_up_volts;
        }
    }

    // THE PANTOGRAPHS' AIR ("back to the compressors and the machinery room", the author,
    // 2026-10-11). A pantograph is raised and held up by air, and the line breaker will not
    // close without it (the pressure switch WCP). The air comes from the main reservoir - which
    // on a locomotive that has stood is empty, and can only be filled by compressors that want
    // the wire. So there is a SMALL compressor on the 110 V battery, by the outside door behind
    // cab A's handbrake, with a vessel and a gauge of its own: run until there is air enough to
    // put one pantograph up and the breaker in, after which the main compressors take over.
    //   READ ("have a read then", the author, 2026-10-11 - the 303E DTR's text and
    //   transportszynowy.pl's page on the pneumatic equipment, which follows the WKL book):
    //   - the pantographs are fed from the CONTROL RESERVOIR (100 l), itself fed from the main
    //     reservoirs through a filter, a reducing valve set to 5 bar and a check valve;
    //   - 4.5 bar at least to raise one; the pantographs' pressure switches (WCP1,2: 0.34 and
    //     0.44 MPa) take the line breaker's control away under 3.5 and give it back at 4.5;
    //   - the small compressor (1JS60 / KP1, 95-100 l/min, 0.67 kW; its lever switch in the
    //     low-voltage cabinet over the converter) has a pressure switch of its own (WCT): in
    //     at 3.8, out at 4.8 bar. It fills the pantographs' cylinders, not the reservoir: a
    //     three-way cock on the machine room's frame B puts them on it, turned by hand;
    //   - its motor has a breaker of its own and runs only with the line breaker OPEN.
    //   OURS: how fast it pumps (a few litres of cylinder and pipe at that delivery: half a
    //   minute), what a raising costs, the leak, and where a raised pantograph falls.
    internal class pantograph_air
    {
        public const float feed_bar          = 5.0f;        // what the main reservoir gives it, through a reducing valve and a check valve
        public const float feed_per_second   = 0.5f;
        public const float pump_out_bar      = 4.8f, pump_in_bar = 3.8f;    // WCT: the small compressor's own pressure switch
        public const float pump_per_second   = 0.16f;       // 0 to 4.8 bar in half a minute
        public const float pump_watts        = 670.0f;
        public const float raise_bar         = 4.5f, hold_bar = 2.5f;       // a pantograph goes up from here, and comes down under this
        public const float breaker_bar       = 4.5f, breaker_out_bar = 3.5f; // WCP
        public const float raising_costs_bar = 0.15f;       // one pantograph's cylinder, off the small compressor alone
        public const float leak_per_second   = 0.0005f;     // some two bar an hour

        // ---- in ---------------------------------------------------------------------------
        public float main_reservoir_bar { get; set; } = 0.0f;       // gauge
        public bool  pump_wanted        { get; set; } = false;      // its switch, with 110 V and the line breaker open
        public int   pantographs_up     { get; set; } = 0;          // how many valves are held open
        // THE THREE-WAY COCK on the pneumatic frame ("yeah do the three way cock", the author,
        // 2026-10-11): the pantographs' circuit on the main reservoir, or on the small compressor.
        // Never both: on the reservoir the small compressor pumps into nothing, and on the
        // compressor the reservoir, however full, gives the pantographs none - a cock left there
        // after starting lets them come down when the small vessel has leaked away.
        public bool  cock_to_small      { get; set; } = false;
        // THE FRAME'S OTHER COCKS ("Since the handles are titled, we can make em functional", the
        // author, 2026-10-12, of the labelled cocks in the frame's pipe runs):
        //   CONTROL AIR     shut, the main reservoir gives this circuit nothing (the small compressor still can);
        //   PANTOGRAPH AIR  shut, the pantographs have no air - none goes up, and what is up comes down;
        //   DRAIN           open, the circuit empties, faster than the reservoir can fill it.
        public bool  feed_open          { get; set; } = true;
        public bool  pantographs_open   { get; set; } = true;
        public bool  drain_open         { get; set; } = false;
        public const float drain_per_second = 0.8f;
        public float bar                { get; set; } = 0.0f;       // gauge; whoever keeps it over a save sets it

        // ---- out --------------------------------------------------------------------------
        public bool pump_running { get; private set; } = false;
        public bool breaker_air  { get; private set; } = false;     // WCP: enough for the line breaker
        // A pantograph may be RAISED while the pressure switches WCP are made: they make at 4.5
        // and let go at 3.5, and between the two they are as they were. It was a plain "4.5 or
        // more" at first - and with the small compressor's own switch not cutting in until 3.8,
        // there was a band (seen in the game at 4.34 bar, 2026-10-12, the reservoir let down
        // to 2.8 and the main compressors off) in which a pantograph would not go up and the
        // small compressor would not start: "we need the small compressor not be governed by
        // the in cab compressor switch", of a small compressor that seemed to want the big ones.
        public bool can_raise    => breaker_air && pantographs_open;
        public bool can_hold     => bar >= hold_bar && pantographs_open;

        private int   _were_up = 0;
        private bool  _started = false;
        private bool  _pump_asked = true;       // WCT: closed until its upper figure, open until its lower
        private float _delivery_bar = 0.0f;     // in the small compressor's own delivery pipe

        public void simulate(float delta_seconds)
        {
            if (!_started)
            {
                breaker_air = bar >= breaker_out_bar;       // a save's air: the switches as they will have been
                _started = true;
            }
            // What its pressure switch feels is its own delivery: the pantographs' circuit with the
            // cock turned to it; a stub of pipe otherwise, which it fills in a second or two and stops.
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
