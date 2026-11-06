// EU07 / WE-4U automatic notching, when the throttle comes from a foreign MU leader.
//
// The game's MU cable carries only a 0-1 throttle; mapped straight onto the wheel it parks on
// resistance notches and never shunts. So (mod/eu07_driver.cs decides when) the throttle is
// read as demand and the wheel and field lever are worked like a driver would:
//   - throttle -> target stage (14: series running + field 0-6, then parallel + field 0-6)
//     and a per-notch current limit (least_amps..most_amps); series shunts all in before parallel;
//   - notch up when the next notch's predicted current fits the limit; rest only on 28 or 43.
//     Prediction: back-EMF from current and resistance now, then next notch's current
//     (upper bound, EMF rises with current); notches differ (1-6, 29-32 jump ~1/3);
//   - running notch: shunts in one at a time, with headroom;
//   - overcurrent (slowing on a hill): shunts out, then notch back;
//   - warm grids (eu07_power_circuit.cs): limit eased to a sustainable current, notch back to
//     hold it; stay in series while warm; shut off near red until black;
//   - wheelslip: no notching up.
// Not on the real locomotive.

using System;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class auto_notching
    {
        public const int   stages             = 2 * field_positions;     // 14
        public const float least_amps         = 350.0f;                  // notch limit, throttle just open
        public const float most_amps          = 800.0f;                  // throttle wide (relays at 900)
        public const float back_amps          = 850.0f;                  // above: step back, any throttle
        public const float back_over          = 1.12f;                   // on resistors: step back above this x limit
        public const float shunt_room         = 0.75f;                   // field step in below this x limit (a step adds up to 27 %)
        public const float ease_from_celsius  = 450.0f;                  // grids: easing starts
        public const float ease_full_celsius  = 570.0f;                  // eased_amps here (sustainable)
        public const float eased_amps         = 450.0f;
        public const float off_celsius        = 590.0f;                  // near red: off
        public const float on_celsius         = 400.0f;                  // back on
        public const float notch_seconds      = 0.5f;                    // between notches up
        public const float shunt_seconds      = 1.0f;                    // between field steps
        public const float after_back_seconds = 5.0f;                    // hold-off after a notch back

        // per-pair standstill current per position at table_volts -> position resistance
        // (bench/eu07_bench.cs prints and checks this table)
        public const float table_volts = 1500.0f;
        public static readonly float[] standstill_12 =
        {
                0.0f,   216.9f,   287.4f,   376.5f,   496.1f,   652.2f,   859.8f,   903.0f,   948.4f,   997.6f,  1048.0f,
             1094.3f,  1149.7f,  1206.9f,  1271.5f,  1338.5f,  1411.6f,  1487.5f,  1571.7f,  1662.8f,  1760.0f,  1856.1f,
             1970.4f,  2091.0f,  2231.8f,  2712.9f,  3443.4f,  4734.6f,  7538.8f,
              746.7f,   980.5f,  1287.7f,  1688.8f,  1852.6f,  2036.7f,  2239.2f,  2472.6f,  2732.4f,  3031.7f,  3377.6f,
             3775.4f,  4243.4f,  6370.5f, 12894.4f,
        };
        public static readonly float[] standstill_34 =
        {
                0.0f,   216.2f,   284.7f,   376.1f,   495.4f,   654.6f,   860.8f,   901.2f,   944.2f,   988.5f,  1037.3f,
             1088.3f,  1145.2f,  1205.1f,  1268.4f,  1333.8f,  1407.0f,  1481.8f,  1566.0f,  1652.4f,  1750.7f,  1855.3f,
             1970.6f,  2091.1f,  2229.8f,  2702.1f,  3432.4f,  4722.0f,  7539.7f,
              746.1f,   980.7f,  1287.8f,  1688.6f,  1850.6f,  2032.2f,  2236.5f,  2467.4f,  2729.8f,  3029.7f,  3374.8f,
             3775.3f,  4257.5f,  6402.1f, 12894.4f,
        };

        // greater pair's current on the next position, same speed and EMF (upper bound)
        // pairs done separately: in parallel their resistances differ; the lower-current pair jumps more
        public static float next_amps(int position, float amps_12, float amps_34, float volts)
        {
            if (position < 0 || position >= controller_positions - 1)
                return Math.Max(amps_12, amps_34);
            int in_row(int at) => at <= series_running ? 4 : 2;                   // motors in series per path
            float pair(float[] standstill, float amps)
            {
                float each = position == 0 ? 0.0f : Math.Max(0.0f, (volts - amps * table_volts / standstill[position]) / in_row(position));
                return Math.Max(0.0f, (volts - in_row(position + 1) * each) * standstill[position + 1] / table_volts);
            }
            return Math.Max(pair(standstill_12, amps_12), pair(standstill_34, amps_34));
        }

        public int   wheel     { get; private set; } = 0;                // asked of the controller
        public int   field     { get; private set; } = 0;                // asked of the field lever
        public int   stage     { get; private set; } = 0;                // from the throttle: 0, or 1-14
        public float may_bring { get; private set; } = 0.0f;             // notch limit, eased
        public bool  shut_off  { get; private set; } = false;            // grids too hot

        public bool  series_only { get; private set; } = false;          // grids warm: no parallel until cool

        private float _wait = 0.0f, _no_up = 0.0f;

        public void reset()
        {
            wheel = field = stage = 0;
            _wait = _no_up = 0.0f;
        }

        // demand: throttle 0-1; amps_12/34: per pair, unfiltered (as relays see it); volts: traction feed
        // position, field_now: actual contactors; able: line contactors may close
        public void step(float delta_seconds, float demand, float amps_12, float amps_34, float volts, int position, int field_now, bool able, bool slipping, float grid_celsius)
        {
            float amps = Math.Max(amps_12, amps_34);
            if (grid_celsius >= ease_full_celsius)
                series_only = true;
            else if (grid_celsius < on_celsius)
                series_only = false;
            _wait  = Math.Max(0.0f, _wait - delta_seconds);
            _no_up = Math.Max(0.0f, _no_up - delta_seconds);
            if (grid_celsius >= off_celsius)
                shut_off = true;
            else if (grid_celsius < on_celsius)
                shut_off = false;
            stage = demand <= 0.005f ? 0 : Math.Max(1, Math.Min(stages, (int) Math.Ceiling(demand * stages - 0.001f)));
            if (stage == 0 || !able || shut_off)
            {
                wheel = field = 0;                       // restart from 0 (contactors need it)
                _wait = notch_seconds;
                return;
            }
            bool  further = stage > field_positions;                               // parallel wanted
            int   run     = further && !series_only ? parallel_running : series_running;
            int   shunts  = further && run == series_running ? field_positions - 1 : (stage - 1) % field_positions;
            float most    = least_amps + (most_amps - least_amps) * Math.Min(1.0f, demand);
            float warmth  = Math.Max(0.0f, Math.Min(1.0f, (grid_celsius - ease_from_celsius) / (ease_full_celsius - ease_from_celsius)));
            float eased   = Math.Min(most, most + (eased_amps - most) * warmth);
            bool  running = wheel == series_running || wheel == parallel_running;
            may_bring = running ? most : eased;

            if (position != wheel || (running && field_now != field) || _wait > 0.0f)
                return;                                  // contactors still moving, or just moved
            if (amps > (running ? back_amps : Math.Min(back_amps, eased * back_over)))
            {
                if (running && field > 0)
                {
                    --field;
                    _wait = shunt_seconds;
                }
                else if (wheel > 1)
                {
                    --wheel;
                    field  = 0;
                    _wait  = notch_seconds;
                    _no_up = after_back_seconds;
                }
                return;
            }
            if (wheel > run)
            {
                --wheel;                                 // less wanted: down at once
                field = 0;
                return;
            }
            if (wheel < run)
            {
                if (wheel == series_running && (field < field_positions - 1 || warmth > 0.0f))
                {
                    // to parallel: all series shunts first; not while grids are warm
                    if (field < field_positions - 1 && !slipping && amps < most * shunt_room)
                    {
                        ++field;
                        _wait = shunt_seconds;
                    }
                    return;
                }
                // onto a running notch: grids out, limit not eased
                bool onto_running = wheel + 1 == series_running || wheel + 1 == parallel_running;
                if (!slipping && _no_up <= 0.0f && next_amps(wheel, amps_12, amps_34, volts) < (onto_running ? most : eased))
                {
                    ++wheel;
                    field = 0;
                    _wait = notch_seconds;
                }
                return;
            }
            if (field > shunts)
            {
                --field;
                _wait = shunt_seconds;
            }
            else if (field < shunts && !slipping && amps < most * shunt_room)
            {
                ++field;
                _wait = shunt_seconds;
            }
        }
    }
}
