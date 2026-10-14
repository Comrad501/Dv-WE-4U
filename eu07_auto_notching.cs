// EU07 / WE-4U - notching by itself, for a locomotive whose throttle comes from a vehicle that is not one of ours.
//
// "Does the game know how to bocznikowanie? we dont want to overheat under way" (the author,
// 2026-10-12). It does not. The game's multiple-unit cable carries a THROTTLE, 0 to 1, and
// nothing of ours: behind a diesel the wheel went wherever that fraction of 43 fell - nearly
// always a resistance notch, held for as long as the diesel's driver held his lever, and
// never a field shunt (the lever for that is shared between two of ours only). And a throttle
// thrown open from a stand walked the contactors up at their own pace into the overload relay.
//
// So, led by a foreign desk (mod/eu07_driver.cs says when), the throttle is taken for what a
// diesel's is - HOW MUCH is wanted - and the wheel and the field lever are worked as a driver
// works them:
//   - the throttle picks a STAGE to end on, of fourteen: series running with the field lever at
//     0 to 6, then parallel running with it at 0 to 6; and the most current a notch may bring,
//     from least_amps to most_amps. On the way to parallel it shunts on the series notch
//     first, all six, as a driver does: the faster she is going when the resistors come in
//     again, the less they are asked to take;
//   - the wheel goes up a notch when the NEXT notch would not bring more than that, and RESTS
//     only on 28 or 43. What the next notch would bring is worked out, not waited for: the
//     notches are not alike (the ramp's at a standstill, 1 to 6, and their mirror on 29 to 32,
//     each throw the current up by a third; a relay that notched up under a fixed current ran
//     into the overload on such a notch). From the current now and
//     the resistance now, the motors' back-EMF; from that and the next notch's resistance, the
//     current then - an upper bound, since the EMF rises with the current;
//   - on the running notch the shunts go in one at a time, with room left for what each adds;
//   - too much current (the speed falling on a hill): shunts out one at a time, then notches back;
//   - THE GRIDS WARM (eu07_power_circuit.cs, "the grids that stay in"): the most a notch may
//     bring is eased, down to a current they carry for good, and it notches BACK to keep to it -
//     a unit that cannot get off its resistors pulls less, for as long as it must, and loses
//     nothing; it does not leave series running for the parallel notches while they are warm,
//     and comes back to it from them when they are as warm as it lets them get; were they to
//     come near red all the same, it shuts off until they are black;
//   - wheels slipping: no further up.
// None of this is the real locomotive, which has no such thing: it is what lets ours work
// behind one of the game's.

using System;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class auto_notching
    {
        public const int   stages             = 2 * field_positions;     // 14: see above
        public const float least_amps         = 350.0f;                  // the most a notch may bring with the throttle just open,
        public const float most_amps          = 800.0f;                  // and with it wide (the overload relays stand at 900)
        public const float back_amps          = 850.0f;                  // over this, a step back - whatever the throttle
        public const float back_over          = 1.12f;                   // and on the resistors, over this much of what a notch may bring
        public const float shunt_room         = 0.75f;                   // a field step in under this much of it: a step adds up to 27 %
        public const float ease_from_celsius  = 450.0f;                  // the grids: from here what a notch may bring is eased
        public const float ease_full_celsius  = 570.0f;                  // to eased_amps here, which they carry for good,
        public const float eased_amps         = 450.0f;
        public const float off_celsius        = 590.0f;                  // near red all the same: off,
        public const float on_celsius         = 400.0f;                  // until black again
        public const float notch_seconds      = 0.5f;                    // between notches up
        public const float shunt_seconds      = 1.0f;                    // between field steps
        public const float after_back_seconds = 5.0f;                    // no notch up so soon after one back

        // What each pair of motors draws at a STAND on each position, at table_volts: the bench's own table
        // (bench/eu07_bench.cs prints it, and fails if this is not it). From it, each position's resistance.
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

        // The greater pair's current on the next position up, from each pair's current on this one: at the same
        // speed, with the motors' EMF as it is now. They give more EMF for more current, so it is never this much.
        // (Each pair by itself: on the parallel notches the two have not the same resistance in, so not the
        // same current, so not the same EMF - and it is the pair with the LESS that jumps the more.)
        public static float next_amps(int position, float amps_12, float amps_34, float volts)
        {
            if (position < 0 || position >= controller_positions - 1)
                return Math.Max(amps_12, amps_34);
            int in_row(int at) => at <= series_running ? 4 : 2;                   // motors one after another in each path
            float pair(float[] standstill, float amps)
            {
                float each = position == 0 ? 0.0f : Math.Max(0.0f, (volts - amps * table_volts / standstill[position]) / in_row(position));
                return Math.Max(0.0f, (volts - in_row(position + 1) * each) * standstill[position + 1] / table_volts);
            }
            return Math.Max(pair(standstill_12, amps_12), pair(standstill_34, amps_34));
        }

        public int   wheel     { get; private set; } = 0;                // what it asks of the controller
        public int   field     { get; private set; } = 0;                // and of the field lever
        public int   stage     { get; private set; } = 0;                // what the throttle asks: 0, or 1-14
        public float may_bring { get; private set; } = 0.0f;             // the most a notch may bring, as eased
        public bool  shut_off  { get; private set; } = false;            // for the grids' sake

        public bool  series_only { get; private set; } = false;          // likewise: no parallel notches until they are black

        private float _wait = 0.0f, _no_up = 0.0f;

        public void reset()
        {
            wheel = field = stage = 0;
            _wait = _no_up = 0.0f;
        }

        // demand: the throttle, 0-1. amps_12, amps_34: each pair's current as the overload relays see it, undamped
        // (by the meters, which are damped, it notched up into the relays). volts: what feeds the traction circuit.
        // position, field_now: where the contactors ARE. able: the line contactors may close.
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
                wheel = field = 0;                       // (and from 0 again when it may: the contactors want the wheel there first)
                _wait = notch_seconds;
                return;
            }
            bool  further = stage > field_positions;                               // parallel is asked for
            int   run     = further && !series_only ? parallel_running : series_running;
            int   shunts  = further && run == series_running ? field_positions - 1 : (stage - 1) % field_positions;
            float most    = least_amps + (most_amps - least_amps) * Math.Min(1.0f, demand);
            float warmth  = Math.Max(0.0f, Math.Min(1.0f, (grid_celsius - ease_from_celsius) / (ease_full_celsius - ease_from_celsius)));
            float eased   = Math.Min(most, most + (eased_amps - most) * warmth);
            bool  running = wheel == series_running || wheel == parallel_running;
            may_bring = running ? most : eased;

            if (position != wheel || (running && field_now != field) || _wait > 0.0f)
                return;                                  // the contactors are still on their way, or it has only just moved
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
                --wheel;                                 // less is asked: down without waiting on anything
                field = 0;
                return;
            }
            if (wheel < run)
            {
                if (wheel == series_running && (field < field_positions - 1 || warmth > 0.0f))
                {
                    // on the way to parallel: the series shunts first, all of them; and not on while the grids are warm
                    if (field < field_positions - 1 && !slipping && amps < most * shunt_room)
                    {
                        ++field;
                        _wait = shunt_seconds;
                    }
                    return;
                }
                // (onto a running notch the grids are out: what it may bring is not eased for their sake)
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
