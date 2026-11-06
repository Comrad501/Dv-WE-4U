// EU07 / WE-4U per-contactor wear marks: soot and heat, 0-1 each, for the 48 contactors.
//   soot: from arcs, on the contactor that opens under current
//     - a little when it opens under load with WS in (capped at a third)
//     - a lot when the overload relays throw the line contactors out on a heavy current
//     - none when WS breaks the current (contactors open on a dead circuit)
//   heat: traction-circuit damage shared among contactors closed at the time (or within in_seconds)
// open contactors stay clean; a repair of >= repair_by health = overhaul, all clean.
// Looks: unity/Assets/Editor/eu07_build.cs (sooted_chute), mod/eu07_driver.cs (watch_contactors);
// bench: bench/eu07_bench.cs (marked_contactors).
// Save: 3 bits a mark, 6 marks per number (<= 6 digits), 16 numbers: packed() / unpack().

using System;
using System.Linq;

namespace electric_sim.unit_A;

internal partial class eu07_sim
{
    internal class contactor_marks
    {
        public static readonly string[] names =
            new[] { "SL1", "SL3", "IS", "IM", "IR1", "IR2" }.Concat(Enumerable.Range(1, 30).Select(number => "SR" + number)).Concat(Enumerable.Range(1, 12).Select(number => "SP" + number)).ToArray();

        public const float heat_full_damage  = 260.0f;     // hp (of 1000) to take a closed contactor to full heat
        // (opening at 900 A and over; scaled below. Normal load breaks only dull the chute mouth)
        public const float soot_an_opening   = 0.004f, soot_by_use_at_most = 0.35f;
        public const float soot_from_amps    = 100.0f;
        public const float soot_a_trip       = 0.22f, soot_a_heavy_trip = 0.30f;      // line contactor tripped at arc_from_amps; extra at +600 A
        public const float in_seconds        = 0.6f;       // after opening, still counts as closed
        public const float repair_by         = 0.2f;       // health restored at once = overhaul
        public const int   marks_a_number    = 6, mark_steps = 7;
        public static int  numbers => (names.Length * 2 + marks_a_number - 1) / marks_a_number;

        public readonly float[] soot = new float[names.Length], heat = new float[names.Length];
        public bool changed = false;                       // since last read

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

        // every step, after the circuit; damage: hp just handed over; health: 0-1, < 0 unknown
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

            // (converter knife arc: damage, but not the traction circuit's)
            if (_knife_arcs_seen < 0)
            {
                _knife_arcs_seen = power.knife_arcs;
                _arcs_seen = power.arcs;
            }
            bool tractions = power.knife_arcs == _knife_arcs_seen;
            _knife_arcs_seen = power.knife_arcs;
            // counted arc in the line contactors (not WS)
            float tripped = 0.0f;
            if (power.arcs != _arcs_seen && !power.arc_at_breaker)
                tripped = soot_a_trip + soot_a_heavy_trip * Math.Max(0.0f, Math.Min(1.0f, (power.arc_amps - power_circuit.arc_from_amps) / 600.0f));
            _arcs_seen = power.arcs;
            // motor current a step ago; a contactor breaks it only if WS (or the battery's) is still in
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

        // marks as save numbers: soot, heat per contactor in order, 3 bits each
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
