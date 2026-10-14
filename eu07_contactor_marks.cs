// EU07 / WE-4U - what each contactor has been through.
//
// "ill also want to add per contactor soot. the ones which were engaged at the time of the damage get
// soot, the ones idling dont" (the author, 2026-10-12, of a cage that had been given one soot over all
// of it by what the drivetrain's health had lost). Then what an arc does was looked up, as far as it can
// be, and reasoned from; put to him, "i concur, no additional comments". Two marks, each 0 to 1, for
// each of the circuit's 48 contactors (eu07_contactors.cs):
//
//   SOOT is an arc's. A contactor that is shut and carrying current does not arc: the one that OPENS on
//   the current does. So:
//     - a very little whenever it opens with current in the motors and the main breaker still in (the
//       wheel turned back under load - what its chute is for), and only up to a third of the way;
//     - a great deal at once when the overload relays throw the line contactors out on a heavy current
//       (the circuit counts those arcs), on whichever of the two was in.
//   When the MAIN BREAKER breaks the current - an earth fault, its own relays - the contactors fall out
//   after it on a dead circuit, and take none.
//
//   HEAT is what the ones that were IN got while damage was being done to the traction circuit - a grid
//   going red and then through, a flashover: every hit point the circuit hands the game is shared out
//   as heat among the contactors standing shut at that moment (or within the last half second: the
//   breaker is out, and they with it, a moment after the grid goes).
//
// The ones standing open take neither, and stay clean. A repair that gives the drivetrain a fifth of
// its health back at once is taken for an overhaul of the cage: all clean.
//
// What they LOOK like is the build's and the mod's (unity/Assets/Editor/eu07_build.cs: sooted_chute;
// mod/eu07_driver.cs: watch_contactors). Here is only who gets what, so that the bench can drive the
// circuit through a burnt grid and an overload and see (bench/eu07_bench.cs: marked_contactors).
//
// KEPT IN A SAVE as three bits a mark, six marks to a number - never more than six figures, whoever
// writes it down - sixteen numbers in all: packed() and unpack().

using System;
using System.Linq;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class contactor_marks
    {
        public static readonly string[] names =
            new[] { "SL1", "SL3", "IS", "IM", "IR1", "IR2" }.Concat(Enumerable.Range(1, 30).Select(number => "SR" + number)).Concat(Enumerable.Range(1, 12).Select(number => "SP" + number)).ToArray();

        public const float heat_full_damage  = 260.0f;     // hit points (of the game's 1000) that take a contactor standing shut through them to the end of its heat
        // (an opening at 900 A and over; by the amperes under that. It was 0.03, with no end to it: six times shut off
        // under load and the whole cage was marked, fifty and it was black - by nothing but driving her. A contactor
        // that only ever does what its chute is for goes dull and grey at the mouth, and stops there.)
        public const float soot_an_opening   = 0.004f, soot_by_use_at_most = 0.35f;
        public const float soot_from_amps    = 100.0f;
        public const float soot_a_trip       = 0.22f, soot_a_heavy_trip = 0.30f;      // a line contactor thrown out on arc_from_amps; and what 600 A more adds
        public const float in_seconds        = 0.6f;       // how long after it opened a contactor still counts as having been in
        public const float repair_by         = 0.2f;       // of the drivetrain's health, given back at once: an overhaul
        public const int   marks_a_number    = 6, mark_steps = 7;
        public static int  numbers => (names.Length * 2 + marks_a_number - 1) / marks_a_number;

        public readonly float[] soot = new float[names.Length], heat = new float[names.Length];
        public bool changed = false;                       // since whoever keeps them last looked

        private readonly bool[]  _was_in  = new bool[names.Length];
        private readonly float[] _in_left = new float[names.Length];
        private float _amps_before = 0.0f, _health_was = -1.0f;
        private int   _arcs_seen = -1, _knife_arcs_seen = -1;

        public static int index_of(string name) => Array.IndexOf(names, name);

        public bool was_in(int index) => _in_left[index] > 0.0f;

        public void put(int index, float soot_given, float heat_given)
        {
            soot[index] = Math.Max(0.0f, Math.Min(1.0f, soot_given));
            heat[index] = Math.Max(0.0f, Math.Min(1.0f, heat_given));
            changed = true;
        }

        private void add(float[] mark, int index, float much)
        {
            if (much <= 0.0f)
                return;
            mark[index] = Math.Min(1.0f, mark[index] + much);
            changed = true;
        }

        // Every step of the simulation, after the circuit's own. damage: the hit points it has just handed
        // over. health: the drivetrain's, 0 to 1 - under 0 if nobody knows.
        public void step(power_circuit power, float delta_seconds, float damage, float health = -1.0f)
        {
            if (_health_was >= 0.0f && health > _health_was + repair_by)
            {
                Array.Clear(soot, 0, soot.Length);
                Array.Clear(heat, 0, heat.Length);
                changed = true;
            }
            if (health >= 0.0f)
                _health_was = health;

            // (the converter's knife pulled under load is damage too, and none of the traction circuit's)
            if (_knife_arcs_seen < 0)
            {
                _knife_arcs_seen = power.knife_arcs;
                _arcs_seen = power.arcs;
            }
            bool tractions = power.knife_arcs == _knife_arcs_seen;
            _knife_arcs_seen = power.knife_arcs;
            // an arc the circuit has counted, in the line contactors (not the main breaker's)
            float tripped = 0.0f;
            if (power.arcs != _arcs_seen && !power.arc_at_breaker)
                tripped = soot_a_trip + soot_a_heavy_trip * Math.Max(0.0f, Math.Min(1.0f, (power.arc_amps - power_circuit.arc_from_amps) / 600.0f));
            _arcs_seen = power.arcs;
            // what was in the motors a step ago; broken by a contactor only if the breaker (or the battery's) still stands
            bool fed = power.fast_breaker_closed || power.battery_contactor;
            float broken = fed ? _amps_before : 0.0f;
            _amps_before = power.relay_amps;

            for (int index = 0; index < names.Length; ++index)
            {
                bool on = power.contactor_locations.TryGetValue(names[index], out var branch) && branch.is_contactor_on(names[index]);
                if (_was_in[index] && !on && broken > soot_from_amps && soot[index] < soot_by_use_at_most)
                    add(soot, index, Math.Min(soot_by_use_at_most - soot[index], soot_an_opening * Math.Min(1.0f, broken / 900.0f)));
                _was_in[index] = on;
                _in_left[index] = on ? in_seconds : Math.Max(0.0f, _in_left[index] - delta_seconds);
                if (damage > 0.0f && tractions && _in_left[index] > 0.0f)
                    add(heat, index, damage / heat_full_damage);
            }
            if (tripped > 0.0f)
            {
                foreach (string line in new[] { "SL1", "SL3" })
                {
                    if (was_in(index_of(line)))
                        add(soot, index_of(line), tripped);
                }
            }
        }

        // The marks as numbers to keep: soot and heat of the first contactor, of the second, ... three bits each.
        public int packed(int number)
        {
            int all = 0;
            for (int place = 0; place < marks_a_number; ++place)
            {
                int value = number * marks_a_number + place;
                if (value >= names.Length * 2)
                    break;
                float mark = (value % 2 == 0 ? soot : heat)[value / 2];
                all |= Math.Max(0, Math.Min(mark_steps, (int) Math.Round(mark * mark_steps))) << (place * 3);
            }
            return all;
        }

        public void unpack(int number, int all)
        {
            for (int place = 0; place < marks_a_number; ++place)
            {
                int value = number * marks_a_number + place;
                if (value >= names.Length * 2)
                    break;
                (value % 2 == 0 ? soot : heat)[value / 2] = ((all >> (place * 3)) & mark_steps) / (float) mark_steps;
            }
        }
    }
}
