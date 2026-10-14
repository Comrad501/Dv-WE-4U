// EU07 / 303E - a bench for the locomotive's power circuit, outside the game.
//
// Drop this file and the four eu07_*.cs files into unit_A/ of a checkout of Knsgf's
// WE6SIM, build it in Debug, and run bench/runner against the DLL. The diagram is compiled
// by HIS compiler, switched by HIS contactor sets and solved by HIS solver; nothing here
// is a port. What it prints:
//
//   1. contactors the diagram has that no set owns
//   2. at a standstill, every controller position: line current and what the line sees
//   3. at speed, the two running positions through every field step. Hold this against
//      the DTR: 0.75 of its tractive effort at the same speeds, on a new 1250 mm wheel
//      where the DTR's figures are for a half worn one (3% faster)
//   4. the protection: undervoltage, overcurrent trip and reset, the PLW button, motors cut out;
//      the converter, compressors and heating; earth fault; the slip relays; the lamps
//   5. a train started from rest by a driver who watches the ammeters
//
// 2 and 3 are run with the overcurrent relays bypassed; nobody stands on position 28.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using LocoSim.Definitions;
using LocoSim.Implementations;

using electric_sim.circuit_sim;

namespace electric_sim.unit_A;

public static class eu07_bench
{
    public static int run(string[] arguments) => eu07_sim.bench(arguments);
}

internal partial class eu07_sim
{
    private const float bench_line_volts = 1500.0f, bench_wheel_diameter = 1.25f, bench_tick = 0.02f;

    private class bench_rig
    {
        public readonly power_circuit power;
        public readonly Port[]        wheel_RPM = new Port[motors];
        public readonly Fuse          control_power, control_air;

        private readonly FieldInfo _busy, _recalculation;

        public bench_rig()
        {
            control_power = new("bench", new FuseDefinition("power", true));
            control_air   = new("bench", new FuseDefinition("air"  , true));
            for (int axle = 0; axle < motors; ++axle)
                wheel_RPM[axle] = new Port("bench", new PortDefinition(default, default, $"WHEEL_RPM_{axle + 1}"));
            power         = new power_circuit(control_power, control_air, wheel_RPM) { line_volts = bench_line_volts };

            const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Instance;
            _busy          = typeof(circuit).GetField("_simulation_in_progress", hidden)!;
            _recalculation = typeof(circuit).GetField("_matrices_recalculation", hidden)!;
        }

        public float km_h
        {
            get => wheel_RPM[3].Value / 60.0f * (float) Math.PI * bench_wheel_diameter * 3.6f;
            set
            {
                for (int axle = 0; axle < motors; ++axle)
                    axle_km_h(axle, value);
            }
        }

        public void axle_km_h(int axle, float km_h) => wheel_RPM[axle].Value = km_h / 3.6f / ((float) Math.PI * bench_wheel_diameter) * 60.0f;

        public float effort_kN => power.wheel_torque / (bench_wheel_diameter * 0.5f) * 0.001f;

        // One tick as the game would run it, except that the solver is waited for.
        public void tick()
        {
            power.simulate(bench_tick);
            while ((bool) _busy.GetValue(power.solver)! || _recalculation.GetValue(power.solver) is Task { IsCompleted: false })
                Thread.Yield();
        }

        public void run(float seconds)
        {
            for (int ticks = (int) Math.Ceiling(seconds / bench_tick); ticks > 0; --ticks)
                tick();
        }

        // Ticks until the ammeters stop moving. False if they never do.
        public bool settle()
        {
            int quiet = 0;
            for (int ticks = 1; ticks <= 20000; ++ticks)
            {
                float before_12 = power.ammeter_12, before_34 = power.ammeter_34;
                tick();
                bool still = Math.Abs(power.ammeter_12 - before_12) < 0.002f && Math.Abs(power.ammeter_34 - before_34) < 0.002f;
                quiet = still ? quiet + 1 : 0;
                if (quiet >= 50 && ticks >= 300)
                    return true;
            }
            return false;
        }

        public bool wheel_to(int position)
        {
            power.set_controller(position);
            for (int ticks = 0; power.contactors.position != position; ++ticks)
            {
                if (ticks > 10000)
                    return false;
                tick();
            }
            return true;
        }

        public bool field_to(int step)
        {
            power.set_field_weakening(step);
            run(1.0f);
            return power.contactors.field == step;
        }
    }

    private static int _failures = 0;

    // Standstill current of each motor group at each position, from part 2. The ratio of
    // one row to the next is how much a step multiplies the current at any speed, since
    // the back EMF does not change in the moment of the step.
    private static readonly float[] _standstill_12 = new float[controller_positions], _standstill_34 = new float[controller_positions];

    private static void expect(bool passed, string what)
    {
        Console.WriteLine($"  {(passed ? "ok  " : "FAIL")}  {what}");
        if (!passed)
            ++_failures;
    }

    public static int bench(string[] arguments)
    {
        bench_rig     rig   = new();
        power_circuit power = rig.power;
        Console.WriteLine($"compiled: {power.named.Count} named branches, {power.contactor_locations.Count} contactors");

        HashSet<string> owned = ["OS1", "OS2", "OS3", "OS4"];
        foreach (string table in new[] { _series_toggles, _parallel_toggles, _field_toggles, _reverser_toggles })
        {
            foreach (string name in table.Trim('\r', '\n').Split('\n')[0].Split([' ', '\r'], StringSplitOptions.RemoveEmptyEntries))
                owned.Add(name);
        }
        List<string> loose = [];
        foreach (string name in power.contactor_locations.Keys)
        {
            if (!owned.Contains(name))
                loose.Add(name);
        }
        loose.Sort();
        Console.WriteLine("contactors no set owns: " + string.Join(" ", loose) + "   (WS is the power circuit's own)");

        power.protection_bypassed = true;
        if (!power.close_fast_breaker() || !power.set_reverser(1))
        {
            Console.WriteLine("the fast breaker would not close, or the reverser would not throw");
            return 1;
        }

        // ---- 2. standstill ---------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("standstill, forward, full field");
        Console.WriteLine("pos   line A   M1,2 A  M3,4 A   line sees ohm   effort kN   fans V   resistors kW");
        int unsettled = 0;
        for (int position = 0; position < controller_positions; ++position)
        {
            if (!rig.wheel_to(position))
            {
                Console.WriteLine($"the contactors never reached position {position}");
                return 1;
            }
            if (!rig.settle())
                ++unsettled;
            float line = power.line_current;
            _standstill_12[position] = power.ammeter_12;
            _standstill_34[position] = power.ammeter_34;
            Console.WriteLine($"{position,3} {line,8:F1} {power.ammeter_12,8:F1} {power.ammeter_34,7:F1}   " +
                $"{(line > 0.5f ? (bench_line_volts / line).ToString("F3") : "open"),10}   {rig.effort_kN,9:F1} {power.fan_volts,8:F0} {power.resistor_heat * 0.001f,11:F0}");
        }

        // (notching by itself works from this very table: eu07_auto_notching.cs)
        float table_off = 0.0f;
        for (int position = 1; position < controller_positions; ++position)
            table_off = Math.Max(table_off, Math.Max(Math.Abs(auto_notching.standstill_12[position] / _standstill_12[position] - 1.0f), Math.Abs(auto_notching.standstill_34[position] / _standstill_34[position] - 1.0f)));
        expect(table_off < 0.01f && bench_line_volts == auto_notching.table_volts, $"the table that notching by itself works from is this one: at most {table_off * 100.0f:F2} % off");

        // ---- 3. the running positions, at speed ---------------------------------------------
        foreach (int position in new[] { parallel_running, series_running })
        {
            if (!rig.wheel_to(position))
                return 1;
            Console.WriteLine();
            Console.WriteLine($"position {position}, {(position == series_running ? "series" : "parallel")} running: motor A / tractive effort kN");
            Console.Write("km/h ");
            for (int field = 0; field < field_positions; ++field)
                Console.Write($"      field {field}  ");
            Console.WriteLine();
            foreach (int km_h in new[] { 20, 30, 40, 52, 60, 80, 100, 112, 125 })
            {
                Console.Write($"{km_h,4} ");
                rig.km_h = km_h;
                for (int field = 0; field < field_positions; ++field)
                {
                    if (!rig.field_to(field))
                    {
                        Console.WriteLine($"the field set never reached step {field}");
                        return 1;
                    }
                    if (!rig.settle())
                        ++unsettled;
                    Console.Write($" {power.ammeter_12,7:F0} /{rig.effort_kN,6:F1}");
                }
                Console.WriteLine();
            }
        }
        rig.km_h = 0.0f;
        rig.field_to(0);
        rig.wheel_to(0);
        rig.settle();
        Console.WriteLine();
        Console.WriteLine($"back at 0: line {power.line_current:F2} A. Points that never settled: {unsettled}");
        _failures += unsettled;

        // ---- 4. protection -----------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("protection");
        power.protection_bypassed = false;

        power.line_volts = undervoltage_test_low;
        rig.run(power_circuit.undervoltage_hold_seconds - 0.5f);
        expect(power.fast_breaker_closed, $"WS holds for {power_circuit.undervoltage_hold_seconds - 0.5f:F1} s at {undervoltage_test_low:F0} V on the wire");
        rig.run(0.7f);
        expect(!power.fast_breaker_closed, $"and falls out when that has lasted {power_circuit.undervoltage_hold_seconds:F0} s");
        power.line_volts = (power_circuit.undervoltage_dropout + power_circuit.undervoltage_pickup) * 0.5f;
        expect(!power.close_fast_breaker(), $"and will not close at {power.line_volts:F0} V");
        power.line_volts = bench_line_volts;
        expect(power.close_fast_breaker(), $"and closes at {bench_line_volts:F0} V");

        rig.wheel_to(3);
        rig.settle();
        expect(power.line_contactors_closed && !power.overcurrent_tripped,
            $"position 3 at a standstill holds: {power.ammeter_12:F0} A against {power_circuit.overcurrent_normal:F0}");
        power.set_controller(20);
        rig.run(3.0f);
        expect(power.overcurrent_tripped && !power.line_contactors_closed && power.contactors.position == 0,
            $"the wheel thrown to 20 at a standstill trips the relays; contactors at {power.contactors.position}, line {power.line_current:F0} A");
        expect(!power.reset_relays(), "OPN does nothing with the wheel at 20");
        power.set_controller(0);
        rig.run(0.2f);
        expect(power.reset_relays(), "OPN resets with the wheel at 0");
        rig.wheel_to(2);
        rig.settle();
        expect(power.line_contactors_closed && power.ammeter_12 > 100.0f, $"and the locomotive takes power again: {power.ammeter_12:F0} A on 2");

        power.line_contactor_cutout = true;
        rig.run(0.2f);
        power.line_contactor_cutout = false;
        rig.run(1.0f);
        expect(power.contactors.position == 0 && power.contactors.dropped_out, "PLW opens the line contactors, and letting it go does not close them");
        power.set_controller(0);
        rig.run(0.2f);
        rig.wheel_to(1);
        expect(power.line_contactors_closed, "they close again once the wheel has been to 0");
        rig.wheel_to(0);

        // jazda ciagniona: hauled, the desk takes no power; back at train running, it does
        power.hauled = true;
        rig.wheel_to(2);
        rig.settle();
        expect(!power.line_contactors_closed && power.ammeter_12 < 1.0f && !power.train_protection_live(true),
            $"hauled (jazda ciagniona): the wheel on 2 closes nothing, {power.ammeter_12:F0} A, and the train protection is out");
        rig.wheel_to(0);
        power.hauled = false;
        rig.wheel_to(2);
        rig.settle();
        expect(power.line_contactors_closed && power.ammeter_12 > 100.0f && power.train_protection_live(true),
            $"back at train running it takes power: {power.ammeter_12:F0} A on 2");
        rig.wheel_to(0);

        // The 600 A relays are blocked on high start, in series only; the 750 A one never.
        int trip_normal = first_position_that_trips(rig, high: false), trip_high = first_position_that_trips(rig, high: true);
        expect(trip_normal > 0 && trip_high > trip_normal,
            $"at a standstill the relays trip on position {trip_normal}, on high start not until {trip_high}");

        expect(!power.cut_out_motors(true, false), "the motor isolators will not move with WS closed");
        power.open_fast_breaker();
        expect(power.cut_out_motors(true, false) && power.close_fast_breaker(), "motors 1 and 2 cut out on a dead locomotive");
        rig.wheel_to(1);
        rig.settle();
        Console.WriteLine($"        on 1 with 1-2 out: M1,2 {power.ammeter_12:F0} A, M3,4 {power.ammeter_34:F0} A, line {power.line_current:F0} A, effort {rig.effort_kN:F1} kN");
        expect(power.ammeter_12 < 1.0f && power.ammeter_34 > 100.0f, "and the other pair works alone");
        rig.wheel_to(0);
        power.open_fast_breaker();
        power.cut_out_motors(false, false);
        power.close_fast_breaker();

        // ---- the auxiliaries and the relays that open WS -----------------------------------
        Console.WriteLine();
        Console.WriteLine("auxiliaries");
        power.converter_switch = true;
        power.compressor_switch[0] = power.compressor_switch[1] = true;
        power.main_reservoir_bar = 5.0f;
        rig.run(1.0f);
        expect(!power.converter_charging && !power.compressor_running[0], $"the converter is still running up after 1 s: {power.converter_speed:F2}");
        rig.run(6.0f);
        expect(power.converter_charging && power.compressor_running[0] && power.compressor_running[1],
            $"then charges and both compressors run: {power.auxiliary_current:F1} A from the wire against a relay at {power_circuit.converter_overcurrent:F0}");
        power.main_reservoir_bar = 8.6f;
        rig.run(0.1f);
        expect(!power.compressor_running[0] && !power.compressor_running[1], $"the pressure switch stops them above 8.0 bar: {power.auxiliary_current:F1} A left");
        power.main_reservoir_bar = 7.5f;
        rig.run(0.1f);
        expect(!power.compressor_running[0], "and does not start them again at 7.5");
        power.main_reservoir_bar = 6.9f;
        rig.run(0.1f);
        expect(power.compressor_running[0], "but does at 6.9");
        power.trip(power_circuit.relay.compressor_1);
        rig.run(0.1f);
        expect(!power.compressor_running[0] && power.compressor_running[1] && power.compressor_tripped && power.fast_breaker_closed,
            "a compressor's relay stops that compressor only, and lights LSC");
        power.reset_auxiliary_relays();

        power.line_volts = 1020.0f;
        rig.run(0.1f);
        expect(power.converter_tripped && !power.fast_breaker_closed, "both compressors on a wire sagged to 1020 V: the converter's relay opens WS");
        power.line_volts = bench_line_volts;
        expect(!power.close_fast_breaker(), "WS will not close on a tripped relay");
        rig.run(2.0f);
        power.reset_auxiliary_relays();
        expect(power.close_fast_breaker(), "and closes after WJ1");
        rig.run(0.5f);
        expect(!power.converter_charging && !power.compressor_running[1], "two seconds dead: the converter has run down and must run up again");
        rig.run(4.0f);
        expect(power.converter_charging && power.compressor_running[1], "which it has, four seconds on");

        power.heating_switch     = true;
        power.heating_load_watts = 300.0e+3f;
        rig.run(0.1f);
        expect(power.heating_on && power.auxiliary_current > 190.0f, $"train heating, 300 kW: {power.auxiliary_current:F0} A with the converter");
        power.heating_load_watts = 700.0e+3f;
        rig.run(0.1f);
        expect(power.heating_tripped && !power.fast_breaker_closed, $"700 kW is past its relay ({power_circuit.heating_overcurrent:F0} A) and opens WS");
        power.heating_switch = false;
        power.reset_auxiliary_relays();
        power.close_fast_breaker();

        rig.wheel_to(2);
        power.trip(power_circuit.relay.earth_fault);
        rig.run(0.1f);
        expect(!power.fast_breaker_closed && power.contactors.position == 0, "an earth fault opens WS and everything behind it");
        expect(!power.close_fast_breaker() && !power.reset_relays(), "OPN will not reset it with the wheel at 2");
        power.set_controller(0);
        rig.run(0.1f);
        expect(power.reset_relays() && power.close_fast_breaker(), "and does at 0");

        power.pantograph_air_ok = false;
        rig.run(0.1f);
        expect(!power.fast_breaker_closed, "no air under the pantograph: WS out");
        power.pantograph_air_ok = true;
        power.close_fast_breaker();

        // Slip: one axle of the first bogie runs away at 30 km/h on position 20.
        rig.km_h = 30.0f;
        power.protection_bypassed = true;
        rig.wheel_to(20);
        rig.settle();
        expect(!power.slip_detected, $"30 km/h on 20, all axles alike: no slip lamp ({power.ammeter_12:F0} A)");
        float before = power.ammeter_12;
        rig.axle_km_h(0, 33.0f);
        rig.settle();
        expect(!power.slip_detected, "axle 1 three km/h ahead: not yet");
        rig.axle_km_h(0, 38.0f);
        rig.settle();
        expect(power.slip_detected && power.ammeter_12 < before, $"eight ahead: LS9, and the current has fallen from {before:F0} to {power.ammeter_12:F0} A");
        rig.km_h = 0.0f;
        rig.wheel_to(0);
        power.protection_bypassed = false;
        rig.run(8.0f);
        expect(!power.slip_detected, "gone when the axles agree again");

        // Plugging: rolling forward, thrown into reverse, power on.
        Console.WriteLine();
        Console.WriteLine("plugging");
        power.take_damage();
        rig.km_h = 30.0f;
        rig.wheel_to(3);
        rig.run(3.0f);
        float forward_kN = rig.effort_kN, forward_amps = power.ammeter_12;
        expect(power.plugging_stress == 0.0f && power.take_damage() == 0.0f, $"30 km/h forward on 3: {forward_amps:F0} A, {forward_kN:F0} kN, no plugging, no damage");
        rig.wheel_to(0);
        rig.run(0.5f);
        expect(power.set_reverser(-1), "the reverser goes to reverse at 30 km/h: only the wheel locks it");
        rig.wheel_to(3);
        rig.run(3.0f);
        float plug_damage = power.take_damage();
        Console.WriteLine($"  30 km/h forward, reverse on 3: {power.ammeter_12:F0} A, {rig.effort_kN:F0} kN, {power.plugging_stress:F2} of rated power taken in, {plug_damage:F1} hit points in 3 s");
        expect(rig.effort_kN < 0.0f && power.ammeter_12 > forward_amps * 1.5f, "it brakes, on far more current than the same notch forward");
        expect(power.plugging_stress > 0.05f && power.plugging_stress < power_circuit.plugging_free && plug_damage == 0.0f && !power.earth_fault_tripped && !power.overcurrent_tripped,
            "the relays hold, and while they hold it costs the drivetrain nothing");
        rig.wheel_to(0);
        rig.run(0.5f);
        rig.km_h = 80.0f;
        power.protection_bypassed = true;                 // so that the overcurrent relays do not end it first
        rig.wheel_to(8);
        float waited = 0.0f;
        while (power.flashovers == 0 && waited < 30.0f)
        {
            rig.run(0.1f);
            waited += 0.1f;
        }
        float plug_stress = power.plugging_stress;
        rig.run(0.2f);                                    // the relay has tripped; WS answers on the next tick
        Console.WriteLine($"  80 km/h forward, reverse on 8: {plug_stress:F2} of rated power; a flashover after {waited:F1} s " +
            $"(flashovers {power.flashovers}, earth fault {power.earth_fault_tripped}, WS {power.fast_breaker_closed})");
        expect(power.flashovers == 1 && power.earth_fault_tripped && !power.fast_breaker_closed, "from speed a commutator flashes over: earth fault, WS out");
        expect(power.take_damage() >= power_circuit.flashover_damage, "which costs a lump");
        power.protection_bypassed = false;
        rig.km_h = 0.0f;
        power.set_controller(0);
        rig.run(8.0f);
        expect(power.reset_relays() && power.set_reverser(1) && power.close_fast_breaker(), "at a stand: reset, forward, WS in");
        rig.wheel_to(3);
        rig.run(3.0f);
        float healthy_kN = rig.effort_kN;
        power.health = 0.25f;
        rig.run(0.5f);
        expect(Math.Abs(rig.effort_kN - healthy_kN * 0.5f) < Math.Abs(healthy_kN) * 0.05f, $"a drivetrain at a quarter of its health pulls half: {rig.effort_kN:F0} of {healthy_kN:F0} kN");
        power.health = 0.0f;
        rig.run(0.5f);
        power.set_controller(0);
        rig.run(8.0f);
        expect(power.earth_fault_tripped && !power.reset_relays(), "at nothing the earth-fault relay is out and will not reset");
        power.health = 1.0f;
        expect(power.reset_relays() && power.close_fast_breaker(), "repaired, it resets");
        power.take_damage();

        Console.WriteLine();
        Console.WriteLine("lamps");
        rig.wheel_to(5);
        rig.run(3.0f);
        Console.WriteLine($"  on 5: LSS {power.fast_breaker_closed}, LS5 on resistors {power.on_resistors}, LS6 fans failed {power.fans_failed}, " +
            $"LS10 line contactors open {!power.line_contactors_closed}, fans at {power.fan_speed:F2}");
        expect(power.on_resistors && !power.fans_failed && power.fan_speed > 0.5f, "on resistors, fans turning");
        rig.wheel_to(0);
        rig.run(8.0f);
        Console.WriteLine($"  on 0: LS5 {power.on_resistors}, LS6 {power.fans_failed}, LS10 {!power.line_contactors_closed}, fans at {power.fan_speed:F2}");
        expect(!power.on_resistors && !power.fans_failed && !power.line_contactors_closed, "at 0: neither lamp, line contactors open");

        // A wire that flickers, on position 43 at speed: a bouncing pantograph. It tripped the
        // author's motor relays at 106 km/h with 135 A in the motors (2026-10-10): every step of
        // the wire, down or up, was a tick of a thousand amperes and more in a circuit with no
        // inductance, and three ticks running are the relays' 0.06 s.
        Console.WriteLine();
        Console.WriteLine("a flickering wire at speed");
        foreach (bool follows in new[] { false, true })
        {
            power.supply_follows_field = follows;
            power.field_loop_gain_limit = follows ? 0.2f : float.PositiveInfinity;      // "as it was" is every protection off
            power.field_unmade_by_reversal = follows;
            power.heating_switch = false;               // as the author ran: converter only, 17 A from the wire
            power.compressor_switch[0] = power.compressor_switch[1] = false;
            power.reset_auxiliary_relays();
            power.reset_relays();
            if (!power.fast_breaker_closed)
                power.close_fast_breaker();
            rig.run(6.0f);                              // the converter up again, if an earlier test left it down
            rig.km_h = 106.0f;
            rig.wheel_to(43);
            rig.settle();
            float steady = power.ammeter_12, worst = 0.0f;
            for (int tick = 0; tick < 250; ++tick)
            {
                power.line_volts = tick < 20 && tick % 2 == 0 ? 1350.0f : bench_line_volts;      // 0.4 s between 1350 and 1500 V, then steady
                rig.tick();
                worst = Math.Max(worst, power.relay_amps);
            }
            Console.WriteLine($"  106 km/h on 43, {steady:F0} A; the wire between 1350 and 1500 V for 0.4 s, the traction circuit {(follows ? "following the field" : "given the wire as it is")}: " +
                $"the relays saw {worst:F0} A of {power_circuit.overcurrent_normal:F0}, tripped {power.overcurrent_tripped}, WS {power.fast_breaker_closed}");
            if (follows)
                expect(!power.overcurrent_tripped && power.line_contactors_closed && power.fast_breaker_closed, "the flicker passes: no relay, the contactors stay in");
            else
                expect(power.overcurrent_tripped, "given the wire as it is, the flicker trips the motors' relays (the fault, kept to show it)");
            rig.km_h = 0.0f;
            power.set_controller(0);
            rig.run(8.0f);
            power.reset_auxiliary_relays();
            power.reset_relays();
            if (!power.fast_breaker_closed)
                power.close_fast_breaker();
            rig.run(6.0f);
        }
        power.supply_follows_field = true;
        power.field_loop_gain_limit = 0.2f;

        // The wheel run back from 43 to 0 at speed: through 29 to 28 the motors go from two in
        // series to four, with a field a second old - the author's ghost overload at 128.6 km/h.
        Console.WriteLine();
        Console.WriteLine("the wheel run back at speed");
        foreach (bool unmade in new[] { false, true })
        {
            power.field_unmade_by_reversal = unmade;
            power.heating_switch = false;
            power.compressor_switch[0] = power.compressor_switch[1] = false;
            power.reset_auxiliary_relays();
            power.reset_relays();
            if (!power.fast_breaker_closed)
                power.close_fast_breaker();
            rig.run(6.0f);
            rig.km_h = 128.0f;
            rig.wheel_to(43);
            rig.settle();
            float steady = power.ammeter_12, worst = 0.0f, pull_after = float.NaN;
            int out_for = -1;
            power.set_controller(0);
            for (int tick = 0; tick < 300; ++tick)
            {
                rig.tick();
                worst = Math.Max(worst, power.relay_amps);
                if (!power.line_contactors_closed && ++out_for == 5)          // a tenth of a second after the contactors opened
                    pull_after = rig.effort_kN;
            }
            if (unmade)
                expect(Math.Abs(pull_after) < 0.1f && power.ammeter_12 < 1.0f, $"and with the contactors out the motors pull nothing: {pull_after:F2} kN a tenth of a second after");
            Console.WriteLine($"  128 km/h on 43, {steady:F0} A; the wheel to 0, a reversed current {(unmade ? "unmaking the field" : "left to the filter")}: " +
                $"the relays saw {worst:F0} A of {power_circuit.overcurrent_normal:F0}, tripped {power.overcurrent_tripped}");
            if (unmade)
                expect(!power.overcurrent_tripped && power.contactors.position == 0, "running the wheel back trips nothing");
            else
                expect(power.overcurrent_tripped, "left to the filter, the motors' relays go (the fault, kept to show it)");
            rig.km_h = 0.0f;
            rig.run(8.0f);
            power.reset_relays();
            rig.run(1.0f);
        }
        power.field_unmade_by_reversal = true;

        // ---- 5. a start --------------------------------------------------------------------
        foreach (float train_tonnes in new[] { 0.0f, 600.0f, 2000.0f })
            start_a_train(rig, train_tonnes);
        start_a_train(rig, 2000.0f, careful: false);

        battery();
        resistor_heat();
        grid_heat();
        converter_knife();
        notching_by_itself();
        marked_contactors();
        motor_heat();
        wire_joint();
        arcs();
        battery_reset();
        low_voltage_bus();
        pantograph_air_test();
        pantograph_air_band();
        frame_cocks();
        if (Environment.GetEnvironmentVariable("EU07_BENCH_PLUGGING") != null)
            plugging_table();
        if (Environment.GetEnvironmentVariable("EU07_BENCH_BATTERY") != null)
        {
            foreach (float share in new[] { 0.6f, 0.9f })
            {
                Console.WriteLine();
                Console.WriteLine($"on the battery, from a stand, notched up to the end of series by a driver who takes the next notch under {share * power_circuit.battery_max_amps:F0} A:");
                Console.WriteLine("  train t  grade   km/h    A   battery V   kW out   km on a full battery (at that speed, after the start)");
                foreach (float grade in new[] { 0.0f, 0.005f, 0.010f, 0.020f })
                    foreach (float tonnes in new[] { 0.0f, 300.0f, 600.0f, 1000.0f, 1500.0f })
                        battery_run(tonnes, grade, share);
            }
        }

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // The starting resistors' heat, and the fans it drives.
    private static void resistor_heat()
    {
        Console.WriteLine();
        Console.WriteLine("the resistors' heat");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = bench_line_volts;
        power.set_reverser(1);
        rig.run(0.5f);
        power.close_fast_breaker();
        // what the real circuit gives the fans at working currents, for the record: plenty, on every notch tried
        foreach ((int position, float speed) in new[] { (36, 45.0f), (32, 35.0f), (24, 25.0f), (18, 15.0f) })
        {
            rig.km_h = speed;
            power.set_controller(position);
            rig.run(6.0f);
            Console.WriteLine($"        on {position} at {speed:F0} km/h: {power.ammeter_12:F0} A, {power.resistor_heat * 0.001f:F0} kW into the resistors, fans by the current {power.fan_by_current:F2}" + (power.overcurrent_tripped ? " - the relays went, the wheel wound straight up at speed" : ""));
            power.set_controller(0);
            rig.run(3.0f);
            power.reset_relays();
        }
        if (Environment.GetEnvironmentVariable("EU07_BENCH_MOTOR_HEAT") != null)
        {
            foreach ((int position, float speed) in new[] { (43, 120.0f), (43, 100.0f), (43, 90.0f), (43, 80.0f), (43, 70.0f), (43, 65.0f), (43, 60.0f), (43, 55.0f), (28, 45.0f), (28, 40.0f), (28, 35.0f), (28, 30.0f), (28, 27.0f) })
            {
                rig.km_h = speed;
                power.set_controller(position);
                rig.run(12.0f);
                Console.WriteLine($"        on {position} at {speed:F0} km/h: {power.ammeter_12:F0} A, {rig.effort_kN:F0} kN, {power.motor_heat * 0.001f:F1} kW lost in the motors{(power.overcurrent_tripped ? " (relay out)" : "")}");
                power.set_controller(0);
                rig.run(3.0f);
                power.reset_relays();
            }
            Console.WriteLine($"        at a stand, wheel at 0: {power.motor_heat * 0.001f:F1} kW");
        }
        rig.run(1800.0f);                                   // cold again
        const int notch = 32;
        rig.km_h = 35.0f;
        power.set_controller(notch);
        rig.run(8.0f);
        float by_current = power.fan_by_current;
        expect(power.on_resistors && power.resistor_heat > 1.0e+4f && power.fan_by_heat < 0.05f && Math.Abs(power.fan_speed - by_current) < 0.001f,
            $"cold, on {notch} at 35 km/h: {power.ammeter_12:F0} A, {power.resistor_heat * 0.001f:F0} kW into the resistors, {power.resistor_celsius:F0} C; the fans by the current alone, {power.fan_speed:F2}");
        rig.run(120.0f);
        float hot = power.resistor_celsius;
        expect(hot > power_circuit.fans_from_celsius + 20.0f && power.fan_by_heat > 0.3f,
            $"two minutes of it: {hot:F0} C; the heat alone would have the fans at {power.fan_by_heat:F2}, the current has them at {power.fan_by_current:F2}");
        rig.run(180.0f);                                    // (not ten, as it was: at this current a grid would be gone - grid_heat, below)
        Console.WriteLine($"        three minutes more: {power.resistor_celsius:F0} C, the fans at {power.fan_speed:F2} - where what is lost and what is blown away meet; the grid that stays in at {power.grid_celsius:F0} C");
        hot = power.resistor_celsius;
        power.set_controller(0);
        rig.run(10.0f);
        expect(power.resistor_heat < 1.0e+3f && power.fan_by_current < 0.05f && power.fan_speed > 0.2f,
            $"wheel at 0: {power.resistor_heat * 0.001f:F1} kW into them, the real circuit's fans at {power.fan_by_current:F2}, and they run on at {power.fan_speed:F2}");
        float time = 10.0f;
        while (power.fan_speed > 0.05f && time < 3600.0f)
        {
            rig.run(5.0f);
            time += 5.0f;
        }
        expect(time < 1200.0f && power.resistor_celsius < hot, $"they stop after {time:F0} s, the resistors at {power.resistor_celsius:F0} C");
        power.set_controller(notch);
        rig.run(120.0f);
        power.open_fast_breaker();
        rig.run(2.0f);
        expect(power.fan_speed < 0.01f, "with the main breaker out they have nothing to run on, however hot");
    }

    // The grid that stays in: minutes at a heavy current, for good at a light one, and what becomes of it held too long.
    private static bench_rig rig_on(int notch, float km_h)
    {
        bench_rig rig = new bench_rig();
        rig.power.set_reverser(1);
        rig.run(0.5f);
        rig.power.close_fast_breaker();
        rig.km_h = km_h;
        rig.power.set_controller(notch);
        rig.run(6.0f);
        return rig;
    }

    // That notch drawing that current, the grids cold: reached as a driver reaches a heavy current - notched up
    // at speed, and the speed then let fall (at a fixed low speed the notches on the way trip the relays).
    private static bench_rig rig_drawing(int notch, float amps)
    {
        bench_rig rig = rig_on(notch, 130.0f);
        while (rig.power.ammeter_12 < amps && rig.km_h > 1.0f && !rig.power.overcurrent_tripped)
        {
            rig.km_h -= 0.1f;
            rig.run(0.1f);
        }
        rig.run(3.0f);
        rig.power.grid_celsius = power_circuit.ambient_celsius;
        return rig;
    }

    private static float seconds_until(bench_rig rig, Func<bool> reached, float at_most)
    {
        float time = 0.0f;
        while (!reached() && time < at_most)
        {
            rig.run(1.0f);
            time += 1.0f;
        }
        return time;
    }

    private static void grid_heat()
    {
        Console.WriteLine();
        Console.WriteLine("the grid that stays in");
        const int notch = 32;
        bench_rig rig = rig_drawing(notch, 750.0f);
        power_circuit power = rig.power;
        float amps = power.ammeter_12, hard = rig.km_h;
        float to_warm = seconds_until(rig, () => power.grid_celsius >= power_circuit.grids_warm_celsius, 3600.0f);
        float to_hot = to_warm + seconds_until(rig, () => power.grid_celsius >= power_circuit.grids_hot_celsius, 3600.0f);
        expect(to_hot >= 240.0f && to_hot <= 390.0f && power.take_damage() < 1.0f && power.grids_burnt == 0,
            $"on {notch} at {hard:F0} km/h, {amps:F0} A, fans at {power.fan_speed:F2}: warm ({power_circuit.grids_warm_celsius:F0} C) after {to_warm:F0} s, hot ({power_circuit.grids_hot_celsius:F0} C) after {to_hot:F0} s, and nothing lost yet");
        float to_fail = to_hot + seconds_until(rig, () => power.grids_burnt > 0, 3600.0f);
        rig.run(0.1f);
        float lost = power.take_damage();
        expect(power.grids_burnt == 1 && power.earth_fault_tripped && !power.fast_breaker_closed && power.ammeter_12 < 1.0f
               && lost > power_circuit.grid_burn_damage + 20.0f && lost < power_circuit.grid_burn_damage + 200.0f,
            $"held there, a grid goes after {to_fail:F0} s: the earth-fault relay out, the main breaker with it, {lost:F0} hit points of 1000 lost ({lost - power_circuit.grid_burn_damage:F0} of them worn away red hot)");
        power.set_controller(0);
        rig.run(60.0f);
        bool reset = power.reset_relays();
        power.take_damage();
        power.close_fast_breaker();
        rig.km_h = 130.0f;
        power.set_controller(notch);
        rig.run(6.0f);
        while (rig.km_h > hard + 15.0f)
        {
            rig.km_h -= 0.1f;
            rig.run(0.1f);
        }
        rig.run(10.0f);
        expect(reset && power.grids_burnt == 1 && power.ammeter_12 > 100.0f && power.take_damage() > 1.0f,
            $"the relay resets and she takes power again, the grids at {power.grid_celsius:F0} C still: no second grid, but the wear goes on");
        power.set_controller(0);
        float cooling = seconds_until(rig, () => power.grid_celsius < power_circuit.grids_warm_celsius, 7200.0f);
        expect(cooling < 900.0f, $"wheel at 0, the fans running on while the bank is warm: black again, below {power_circuit.grids_warm_celsius:F0} C, after {cooling:F0} s");

        rig = rig_drawing(notch, 600.0f);
        power = rig.power;
        amps = power.ammeter_12;
        float less = rig.km_h;
        float slower = seconds_until(rig, () => power.grid_celsius >= power_circuit.grids_hot_celsius, 3600.0f);
        rig.run(1800.0f);
        float wearing = power.take_damage();
        expect(slower > to_hot * 1.4f && slower < 900.0f && wearing > 50.0f,
            $"at {amps:F0} A ({less:F0} km/h): hot after {slower:F0} s; half an hour more: {power.grid_celsius:F0} C, a grid gone {power.grids_burnt} times, {wearing:F0} hit points lost");

        foreach (float light in new[] { 525.0f, 450.0f, 300.0f })
        {
            rig = rig_drawing(notch, light);
            power = rig.power;
            amps = power.ammeter_12;
            float fans = power.fan_speed;
            float never = seconds_until(rig, () => power.grid_celsius >= power_circuit.grids_hot_celsius, 3600.0f);
            if (light > 450.0f)
                Console.WriteLine($"        at {amps:F0} A, fans at {fans:F2} then {power.fan_speed:F2}: {(never < 3600.0f ? $"hot after {never:F0} s" : $"{power.grid_celsius:F0} C after an hour")}");
            else
                expect(never >= 3600.0f && power.grid_celsius < power_circuit.grids_hot_celsius && power.take_damage() <= 0.0f,
                    $"at {amps:F0} A, fans at {fans:F2} then {power.fan_speed:F2}: {power.grid_celsius:F0} C after an hour, and nothing lost");
        }

        // a start as a heavy train's is - two and a half minutes of 675 A - and another five minutes later
        rig = rig_drawing(notch, 675.0f);
        float start_km_h = rig.km_h;
        power = rig.power;
        rig.run(144.0f);
        float once = power.grid_celsius;
        power.set_controller(0);
        rig.run(300.0f);
        float between = power.grid_celsius;
        rig.km_h = 130.0f;                                   // (back onto the notch as before: at speed, and the speed let fall)
        power.set_controller(notch);
        rig.run(6.0f);
        while (rig.km_h > start_km_h)
        {
            rig.km_h -= 0.1f;
            rig.run(0.1f);
        }
        rig.run(144.0f);
        expect(once < power_circuit.grids_warm_celsius && power.grid_celsius < power_circuit.grids_hot_celsius && power.take_damage() <= 0.0f,
            $"two and a half minutes at 675 A, as a heavy start is: {once:F0} C; five minutes off, {between:F0} C, and the same again: {power.grid_celsius:F0} C, and nothing lost");

        rig = rig_drawing(notch, 750.0f);
        power = rig.power;
        power.fans_fed = false;
        float unblown = seconds_until(rig, () => power.grid_celsius >= power_circuit.grids_hot_celsius, 3600.0f);
        expect(unblown < to_hot - 20.0f, $"at {power.ammeter_12:F0} A with the fans dead: hot after {unblown:F0} s");

        // a running notch has no grid in: the same current for as long, and they stay cold
        // (reached as a driver reaches it: notched up at speed, and the speed then let fall until she pulls)
        rig = rig_on(series_running, 90.0f);
        power = rig.power;
        while (power.ammeter_12 < 700.0f && rig.km_h > 5.0f)
        {
            rig.km_h -= 0.25f;
            rig.run(0.5f);
        }
        amps = power.ammeter_12;
        rig.run(600.0f);
        expect(!power.on_resistors && amps > 600.0f && power.grid_celsius < power_circuit.ambient_celsius + 1.0f,
            $"on {series_running}, the series running notch, {amps:F0} A for ten minutes: {power.grid_celsius:F0} C");
    }

    // The converter's knife switch is an isolator: opened on a converter that has been switched off, nothing; on one that runs, an arc.
    private static void converter_knife()
    {
        Console.WriteLine();
        Console.WriteLine("the converter's knife switch");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.set_reverser(1);
        rig.run(0.5f);
        power.close_fast_breaker();
        power.converter_switch = true;
        rig.run(30.0f);
        bool ran = power.converter_charging;
        power.take_damage();
        power.converter_switch = false;                     // as it should be done: off at the desk, then the knife
        rig.run(1.0f);
        power.converter_knife_open = true;
        rig.run(1.0f);
        expect(ran && power.knife_arcs == 0 && power.fast_breaker_closed && !power.earth_fault_tripped && power.take_damage() <= 0.0f,
            "switched off at the desk first, the knife opens on nothing: no arc, the main breaker in");
        power.converter_switch = true;
        rig.run(10.0f);
        expect(!power.converter_charging && power.converter_speed < 0.6f, $"open, the converter has no feed whatever its switch says: at {power.converter_speed:F2} of its speed and falling");
        power.converter_knife_open = false;
        rig.run(30.0f);
        bool again = power.converter_charging;
        int arcs_before = power.arcs;
        power.converter_knife_open = true;                  // pulled on a running converter
        rig.run(0.5f);
        float lost = power.take_damage();
        expect(again && power.knife_arcs == 1 && power.arcs == arcs_before + 1 && power.earth_fault_tripped && !power.fast_breaker_closed && Math.Abs(lost - power_circuit.knife_arc_damage) < 0.5f,
            $"pulled on a running converter: an arc, the earth-fault relay out and the main breaker with it, {lost:F0} hit points of 1000 lost");
    }

    // Behind a foreign desk: a throttle comes, and the wheel and the field lever are worked for it (eu07_auto_notching.cs).
    // A train of so many tonnes behind the locomotive's 80, on so much of a gradient, for so long; the throttle as given.
    private sealed class hauled_train
    {
        public readonly bench_rig     rig = new bench_rig();
        public readonly auto_notching auto = new auto_notching();
        public float km_h = 0.0f, most_amps = 0.0f, most_celsius = 0.0f, on_grids_seconds = 0.0f, lost = 0.0f, time = 0.0f;
        public int   most_wheel = 0, moves = 0;
        public bool  by_itself = true;
        private readonly float _tonnes, _per_mille;
        private int _wheel_was = 0;
        private bool _tripped_was = false;
        private int _field_was = 0;
        private float _amps_was = 0.0f, _peak = 0.0f;
        private static readonly bool _trace = Environment.GetEnvironmentVariable("EU07_BENCH_NOTCHING") != null;

        public hauled_train(float tonnes, float per_mille)
        {
            _tonnes = tonnes + 80.0f;
            _per_mille = per_mille;
            rig.power.set_reverser(1);
            rig.run(0.5f);
            rig.power.close_fast_breaker();
        }

        public void run(float seconds, float throttle)
        {
            power_circuit power = rig.power;
            for (int ticks = (int) Math.Ceiling(seconds / bench_tick); ticks > 0; --ticks)
            {
                if (by_itself)
                {
                    auto.step(bench_tick, throttle, power.pair_amps_12, power.pair_amps_34, power.feed_volts, power.contactors.position, power.contactors.field,
                              power.may_take_power, power.slip_detected, power.grid_celsius);
                    power.set_controller(auto.wheel);
                    power.set_field_weakening(auto.field);
                }
                else
                    power.set_controller((int) Math.Round(throttle * (controller_positions - 1)));      // as it was: the throttle is a notch
                rig.km_h = km_h;
                rig.tick();
                // 2 N a kN rolling, the air by the square of the speed, the gradient; standing, it does not roll back
                float newtons = rig.effort_kN * 1000.0f - _tonnes * 9.81f * (2.0f + _per_mille) - 4.0f * km_h * km_h;
                km_h = Math.Max(0.0f, km_h + newtons / (_tonnes * 1000.0f) * bench_tick * 3.6f);
                time += bench_tick;
                most_amps    = Math.Max(most_amps, power.relay_amps);
                most_celsius = Math.Max(most_celsius, power.grid_celsius);
                most_wheel   = Math.Max(most_wheel, power.contactors.wanted);
                if (power.on_resistors)
                    on_grids_seconds += bench_tick;
                if (power.contactors.wanted != _wheel_was)
                    ++moves;
                if (_trace && (power.contactors.wanted != _wheel_was || power.overcurrent_tripped != _tripped_was))
                    Console.WriteLine($"          {time,7:F2} s {km_h,5:F1} km/h wheel {_wheel_was,2} -> {power.contactors.wanted,2} at {power.contactors.position,2} field {power.contactors.field}: relays {power.relay_amps,5:F0} A, meters {power.ammeter_12,5:F0} {power.ammeter_34,5:F0}, a notch may bring {auto.may_bring:F0}{(power.overcurrent_tripped ? "  TRIPPED" : "")}");
                _tripped_was = power.overcurrent_tripped;
                if (_trace && power.contactors.field != _field_was)
                {
                    Console.WriteLine($"          {time,7:F2} s {km_h,5:F1} km/h on {power.contactors.position} field {_field_was} -> {power.contactors.field}: before it {_amps_was:F0} A, most since the last step {_peak:F0} A");
                    _peak = 0.0f;
                }
                _peak = Math.Max(_peak, power.relay_amps);
                if (power.contactors.field == _field_was)
                    _amps_was = power.relay_amps;
                _field_was = power.contactors.field;
                _wheel_was = power.contactors.wanted;
                lost += power.take_damage();
            }
        }

        public string where => $"{km_h:F0} km/h on {rig.power.contactors.position} field {rig.power.contactors.field}, {rig.power.ammeter_12:F0} A, the grids at {rig.power.grid_celsius:F0} C";
    }

    // What each contactor has been through (eu07_contactor_marks.cs): heat for the ones that stood shut while a grid
    // burnt, soot for the one that broke an overload, nothing for the ones that stood open.
    private static void marked_contactors()
    {
        Console.WriteLine();
        Console.WriteLine("what each contactor has been through");
        string[] names = contactor_marks.names;
        expect(names.Length == 48 && names.All(name => new bench_rig().power.contactor_locations.ContainsKey(name)) && contactor_marks.numbers == 16,
            $"{names.Length} contactors, every one of them the circuit's; kept as {contactor_marks.numbers} numbers");

        // a grid burnt through, held on a resistance notch at 750 A (notch 8 at a stand, where the author held his, is
        // some 700 A of this bench's: the grids go red on it and stay)
        const int notch = 12;
        bench_rig rig = rig_drawing(notch, 750.0f);
        power_circuit power = rig.power;
        power.take_damage();
        contactor_marks marks = new contactor_marks();
        bool[] were_in = new bool[names.Length];
        float seconds = 0.0f;
        while (power.grids_burnt == 0 && seconds < 3600.0f)
        {
            for (int index = 0; index < names.Length; ++index)
                were_in[index] = power.contactor_locations[names[index]].is_contactor_on(names[index]);
            rig.tick();
            marks.step(power, bench_tick, power.take_damage());
            seconds += bench_tick;
        }
        for (float after = 0.0f; after < 3.0f; after += bench_tick)
        {
            rig.tick();
            marks.step(power, bench_tick, power.take_damage());
        }
        string[] stood_in = names.Where((name, index) => were_in[index]).ToArray(), heated = names.Where((name, index) => marks.heat[index] > 0.0f).ToArray();
        float least = names.Where((name, index) => were_in[index]).Min(name => marks.heat[contactor_marks.index_of(name)]);
        expect(power.grids_burnt == 1 && stood_in.Length >= 3 && stood_in.Length < 20 && stood_in.SequenceEqual(heated) && least >= 0.75f,
            $"a grid burnt on notch {notch} after {seconds:F0} s: heat on the {stood_in.Length} that stood shut ({string.Join(" ", stood_in)}), {least:F2} at the least, and on no other ({heated.Length} heated)");
        expect(marks.soot.All(mark => mark == 0.0f) && !power.fast_breaker_closed && names.All(name => !power.contactor_locations[name].is_contactor_on(name)),
            "the main breaker broke that current: the contactors fell out after it on a dead circuit, and none has soot");

        // the overload relays throwing the line contactors out
        // (notch 12 at a stand is 860 A, under the relays: 18 it is)
        rig = rig_on(18, 130.0f);
        power = rig.power;
        power.take_damage();
        marks = new contactor_marks();
        float carried = 0.0f;
        while (!power.overcurrent_tripped && rig.km_h > 1.0f)
        {
            rig.km_h -= 0.1f;
            for (int tick = 0; tick < 5 && !power.overcurrent_tripped; ++tick)
            {
                carried = power.relay_amps;
                rig.tick();
                marks.step(power, bench_tick, power.take_damage());
            }
        }
        for (float after = 0.0f; after < 3.0f; after += bench_tick)
        {
            rig.tick();
            marks.step(power, bench_tick, power.take_damage());
        }
        float line_1 = marks.soot[contactor_marks.index_of("SL1")], line_3 = marks.soot[contactor_marks.index_of("SL3")];
        string[] sooted = names.Where((name, index) => marks.soot[index] > 0.0f).ToArray();
        expect(power.overcurrent_tripped && power.fast_breaker_closed && line_1 >= contactor_marks.soot_a_trip && line_3 == 0.0f && marks.heat.All(mark => mark == 0.0f),
            $"the overload relays out on {carried:F0} A in series: SL1, which broke it, sooted {line_1:F2}; SL3, which stood open, {line_3:F2}; no heat anywhere (sooted: {string.Join(" ", sooted)})");

        // the wheel turned back under load: a little, on the ones that opened
        rig = rig_drawing(notch, 600.0f);
        power = rig.power;
        power.take_damage();
        marks = new contactor_marks();
        marks.step(power, bench_tick, 0.0f);
        power.set_controller(0);
        for (float after = 0.0f; after < 3.0f; after += bench_tick)
        {
            rig.tick();
            marks.step(power, bench_tick, power.take_damage());
        }
        sooted = names.Where((name, index) => marks.soot[index] > 0.0f).ToArray();
        expect(sooted.Length >= 1 && marks.soot.Max() <= contactor_marks.soot_an_opening + 0.0001f && marks.heat.All(mark => mark == 0.0f),
            $"shut off by the wheel from 600 A: a very little soot ({marks.soot.Max():F4} at the most) on what opened on it ({string.Join(" ", sooted)}), no heat");

        // kept and read back; cleaned by a repair
        marks.put(contactor_marks.index_of("SR7"), 1.0f, 0.5f);
        marks.put(contactor_marks.index_of("SP12"), 0.3f, 1.0f);
        contactor_marks read_back = new contactor_marks();
        for (int number = 0; number < contactor_marks.numbers; ++number)
            read_back.unpack(number, marks.packed(number));
        float worst = Enumerable.Range(0, names.Length).Max(index => Math.Max(Math.Abs(read_back.soot[index] - marks.soot[index]), Math.Abs(read_back.heat[index] - marks.heat[index])));
        int greatest = Enumerable.Range(0, contactor_marks.numbers).Max(number => marks.packed(number));
        expect(worst <= 0.5f / contactor_marks.mark_steps + 0.0001f && read_back.soot[contactor_marks.index_of("SR7")] == 1.0f && read_back.heat[contactor_marks.index_of("SP12")] == 1.0f && greatest < 262144,
            $"kept and read back: no mark off by more than {worst:F3}; the greatest number kept is {greatest}");
        read_back.step(power, bench_tick, 0.0f, 0.5f);
        read_back.step(power, bench_tick, 0.0f, 0.9f);
        expect(read_back.soot.All(mark => mark == 0.0f) && read_back.heat.All(mark => mark == 0.0f), "the drivetrain repaired, 50 to 90 %: every contactor clean");
    }

    private static void notching_by_itself()
    {
        Console.WriteLine();
        Console.WriteLine("notching by itself, for a foreign throttle");

        // a light train on the level, the throttle thrown wide open from a stand
        hauled_train light = new hauled_train(300.0f, 0.0f);
        light.run(240.0f, 1.0f);
        power_circuit power = light.rig.power;
        expect(power.contactors.position == parallel_running && power.contactors.field == field_positions - 1 && !power.overcurrent_tripped
               && light.most_amps < power_circuit.overcurrent_normal - 50.0f && light.most_celsius < power_circuit.grids_warm_celsius && light.lost <= 0.0f,
            $"300 t on the level, throttle wide open from a stand: after four minutes {light.where}; never over {light.most_amps:F0} A or {light.most_celsius:F0} C, {light.on_grids_seconds:F0} s on the grids");
        light.run(60.0f, 0.3f);
        expect(power.contactors.position == series_running && power.contactors.field <= 4 && light.auto.stage == 5,
            $"the throttle back to 0.3 (stage {light.auto.stage} of {auto_notching.stages}): {light.where}");
        light.run(5.0f, 0.0f);
        expect(power.contactors.position == 0 && power.ammeter_12 < 1.0f, $"the throttle shut: on {power.contactors.position}, {power.ammeter_12:F0} A");

        // a heavy one on a hill it cannot get up to a running notch on: the case that was the question
        foreach (bool by_itself in new[] { false, true })
        {
            hauled_train heavy = new hauled_train(1500.0f, 14.0f) { by_itself = by_itself };
            power = heavy.rig.power;
            heavy.run(1800.0f, by_itself ? 1.0f : 12.0f / (controller_positions - 1));
            if (!by_itself)
                Console.WriteLine($"        AS IT WAS, 1500 t on 14 per mille, a throttle that falls on notch 12 and is held: after half an hour {heavy.where}; a grid gone {power.grids_burnt} times, {heavy.lost:F0} hit points lost");
            else
                expect(power.grids_burnt == 0 && heavy.lost <= 0.0f && !power.overcurrent_tripped && heavy.most_celsius < power_circuit.grids_hot_celsius
                       && power.ammeter_12 > 150.0f && heavy.rig.effort_kN > 20.0f,
                    $"by itself, the throttle wide open: after half an hour {heavy.where}, still pulling {heavy.rig.effort_kN:F0} kN; never over {heavy.most_amps:F0} A or {heavy.most_celsius:F0} C, nothing lost; the wheel moved {heavy.moves} times");
        }

        // the same train on a hill it CAN climb, slowly: through the resistance notches and off them
        hauled_train slow = new hauled_train(1500.0f, 4.0f);
        power = slow.rig.power;
        slow.run(900.0f, 1.0f);
        expect(!power.on_resistors && power.contactors.position >= series_running && slow.lost <= 0.0f && !power.overcurrent_tripped && slow.most_celsius < power_circuit.grids_hot_celsius,
            $"1500 t on 4 per mille, wide open: after a quarter of an hour {slow.where}; {slow.on_grids_seconds:F0} s on the grids, never over {slow.most_amps:F0} A or {slow.most_celsius:F0} C");

        // with nothing to feed it, it waits at 0; and starts from 0 when it may
        hauled_train dead = new hauled_train(300.0f, 0.0f);
        power = dead.rig.power;
        power.open_fast_breaker();
        dead.run(10.0f, 1.0f);
        bool waited = dead.auto.wheel == 0 && power.contactors.position == 0;
        power.close_fast_breaker();
        dead.run(20.0f, 1.0f);
        expect(waited && power.contactors.position > 5 && dead.most_amps < power_circuit.overcurrent_normal - 100.0f,
            $"the main breaker out: the wheel waits at 0; closed again, it starts from 0 - {dead.where}, never over {dead.most_amps:F0} A");
    }

    // When the protection breaks a heavy current, that is an arc; when the driver shuts off, it is not.
    private static void arcs()
    {
        Console.WriteLine();
        Console.WriteLine("arcs");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = bench_line_volts;
        power.set_reverser(1);
        rig.run(0.5f);
        power.close_fast_breaker();
        void onto(int wanted, float km_h)
        {
            power.set_controller(0);
            rig.run(2.0f);
            power.reset_relays();
            rig.km_h = 45.0f;
            for (int position = 1; position <= wanted; ++position)
            {
                power.set_controller(position);
                rig.run(0.4f);
            }
            while (Math.Abs(rig.km_h - km_h) > 0.05f)
            {
                rig.km_h += Math.Max(-0.1f, Math.Min(0.1f, km_h - rig.km_h));
                rig.run(0.1f);
            }
            rig.run(4.0f);
        }
        onto(series_running, 30.0f);
        int before = power.arcs;
        float carried = power.ammeter_12;
        power.set_controller(0);
        rig.run(5.0f);
        expect(power.arcs == before && carried > power_circuit.arc_from_amps, $"shut off by the wheel from {carried:F0} A: no arc");
        onto(series_running, 30.0f);
        before = power.arcs;
        carried = power.ammeter_12;
        power.open_fast_breaker();
        rig.run(1.0f);
        expect(power.arcs == before + 1 && power.arc_amps > power_circuit.arc_from_amps, $"the main breaker opened on {carried:F0} A: an arc, {power.arc_amps:F0} A broken");
        power.close_fast_breaker();
        onto(series_running, 45.0f);
        before = power.arcs;
        carried = power.ammeter_12;
        power.open_fast_breaker();
        rig.run(1.0f);
        expect(power.arcs == before && carried < power_circuit.arc_from_amps, $"opened on {carried:F0} A: none");
        power.close_fast_breaker();
        onto(series_running, 45.0f);
        before = power.arcs;
        while (!power.overcurrent_tripped && rig.km_h > 5.0f)                 // slowed on 28 until the relays go
        {
            rig.km_h -= 0.1f;
            rig.run(0.1f);
        }
        rig.run(1.0f);
        expect(power.overcurrent_tripped && power.arcs == before + 1 && power.arc_amps >= power_circuit.overcurrent_normal * 0.95f,
            $"the overload relays going out at {rig.km_h:F0} km/h: an arc, {power.arc_amps:F0} A broken");
    }

    // "i cant reset motor ground fault when on battery" (the author, 2026-10-11): standing on the
    // brakes with no wire, wound up on the battery until the overload relays go, then reset.
    private static void battery_reset()
    {
        Console.WriteLine();
        Console.WriteLine("the overload relays, reset on the battery");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = 0.0f;
        power.set_reverser(1);
        power.battery_wanted = true;
        rig.run(0.5f);
        // The wheel wound STRAIGHT up, as a careless hand does: notch by notch the battery's own breaker
        // (700 A for 0.3 s) goes first, as it should - the starting resistors are made so that no
        // notch throws the current up by much (2026-10-14; the simulator-derived set before them had
        // a notch that added 43 %, which beat the battery's breaker to it).
        int position = series_running;
        power.set_controller(position);
        for (float waited = 0.0f; waited < 3.0f && !power.overcurrent_tripped && !power.battery_tripped; waited += 0.05f)
            rig.run(0.05f);
        expect(power.overcurrent_tripped, $"standing, the wheel wound straight up to {position}: the overload relays go ({power.arc_amps:F0} A broken; battery breaker {(power.battery_tripped ? "OPEN" : "in")})");
        rig.run(1.0f);
        expect(!power.reset_relays(), "the button with the wheel still up: nothing");
        power.set_controller(0);
        rig.run(1.0f);
        power.set_reverser(0);
        rig.run(0.2f);
        expect(!power.reset_relays(), "the button with the reverser in neutral: nothing");
        power.set_reverser(1);
        rig.run(0.2f);
        expect(power.reset_relays() && !power.overcurrent_tripped, "reverser forward, wheel at 0, the button: reset");
        power.set_controller(1);
        rig.run(1.0f);
        expect(power.contactors.position == 1 && power.line_contactors_closed, "and the first notch takes again");
    }

    // The pantographs' air: the small compressor from nothing, a raising, the main reservoir taking over.
    private static void pantograph_air_test()
    {
        Console.WriteLine();
        Console.WriteLine("the pantographs' air");
        pantograph_air air = new pantograph_air();
        float run(float seconds, Func<bool>? until = null)
        {
            float time = 0.0f;
            for (; time < seconds && (until == null || !until()); time += 0.1f)
                air.simulate(0.1f);
            return time;
        }
        run(5.0f);
        expect(air.bar == 0.0f && !air.can_raise && !air.breaker_air, "stood empty, the main reservoir empty: no air for a pantograph or the breaker");
        air.pump_wanted = true;
        run(0.1f);
        float stub = 0.1f + run(30.0f, () => !air.pump_running);
        run(30.0f);
        expect(stub < 5.0f && !air.pump_running && air.bar == 0.0f, $"the small compressor with the cock on the main reservoir: it fills its own pipe in {stub:F1} s and stops; the pantographs get nothing");
        air.cock_to_small = true;
        run(0.2f);
        expect(air.pump_running, "the cock turned to it: it starts again");
        float took = run(600.0f, () => air.can_raise);
        expect(took > 20.0f && took < 40.0f && air.breaker_air, $"enough for a pantograph and the breaker ({pantograph_air.raise_bar:F1} bar) after {took:F0} s");
        took += run(600.0f, () => !air.pump_running);
        run(60.0f);
        expect(!air.pump_running && air.bar <= pantograph_air.pump_out_bar + 0.02f && air.bar > pantograph_air.pump_out_bar - 0.1f,
            $"its pressure switch stops it at {air.bar:F2} bar, {took:F0} s from nothing");
        air.pantographs_up = 1;
        run(0.2f);
        float after_one = air.bar;
        expect(after_one < 4.7f && air.can_raise && air.breaker_air, $"one pantograph up: {after_one:F2} bar, the breaker's air still there, enough for the second");
        air.pantographs_up = 2;
        run(0.2f);
        float restart = 0.0f;
        while (!air.pump_running && restart < 3.0f)
        {
            run(1.0f);
            restart += 1.0f / 3600.0f;
        }
        expect(air.pump_running && air.bar <= pantograph_air.pump_in_bar + 0.2f, $"both up, the switch left on: it starts again by itself at {air.bar:F2} bar, {restart * 60.0f:F0} min on");
        run(60.0f);
        air.pump_wanted = false;
        float hours = 0.0f;
        while (air.can_hold && hours < 10.0f)
        {
            run(60.0f);
            hours += 1.0f / 60.0f;
        }
        expect(hours > 0.8f && hours < 2.0f && !air.breaker_air, $"switched off and left: the breaker's air goes first, the pantographs come down after {hours:F1} h");
        air.main_reservoir_bar = 8.0f;
        float before = air.bar;
        run(30.0f);
        expect(air.bar <= before, $"the main reservoir at 8 bar, the cock still on the small compressor: nothing from it ({air.bar:F2} bar)");
        air.cock_to_small = false;
        took = run(60.0f, () => air.bar >= pantograph_air.feed_bar - 0.01f);
        float fed_to = air.bar;
        air.main_reservoir_bar = 1.0f;
        run(60.0f);
        expect(took < 10.0f && fed_to > pantograph_air.feed_bar - 0.02f && air.bar > 4.9f, $"the cock back on the reservoir: {fed_to:F1} bar in {took:F0} s; the reservoir let down to 1, a minute on: {air.bar:F2} (it does not run back)");
    }

    // As it stood in the game: the air come down from full to 4.34 bar, the main reservoir at 2.8 and
    // its compressors off, the cock on the small compressor and that switched on.
    private static void frame_cocks()
    {
        Console.WriteLine();
        Console.WriteLine("the pneumatic frame's cocks");
        pantograph_air air = new pantograph_air { bar = 0.0f, main_reservoir_bar = 8.0f };
        void run(float seconds)
        {
            for (float time = 0.0f; time < seconds; time += 0.1f)
                air.simulate(0.1f);
        }
        air.feed_open = false;
        run(30.0f);
        expect(air.bar < 0.1f && !air.can_raise, $"CONTROL AIR shut, the reservoir at 8 bar: the circuit stays at {air.bar:F2} bar");
        air.feed_open = true;
        run(30.0f);
        expect(air.bar > 4.9f && air.can_raise, $"opened: {air.bar:F2} bar, a pantograph may go up");
        air.pantographs_up = 1;
        run(1.0f);
        air.pantographs_open = false;
        expect(!air.can_hold && !air.can_raise, "PANTOGRAPH AIR shut: the one that is up comes down, and none goes up");
        air.pantographs_open = true;
        expect(air.can_hold, "opened again: it may stay up");
        air.drain_open = true;
        run(20.0f);
        expect(air.bar < 0.1f && !air.breaker_air, $"DRAIN open, the reservoir still feeding: {air.bar:F2} bar, the breaker's air gone");
        air.drain_open = false;
        run(30.0f);
        expect(air.bar > 4.9f, $"shut: it fills again, {air.bar:F2} bar");
    }

    private static void pantograph_air_band()
    {
        Console.WriteLine();
        Console.WriteLine("the pantographs' air, between the two pressure switches");
        pantograph_air air = new pantograph_air { bar = 5.0f, main_reservoir_bar = 8.0f };
        void run(float seconds)
        {
            for (float time = 0.0f; time < seconds; time += 0.1f)
                air.simulate(0.1f);
        }
        run(5.0f);
        air.cock_to_small = true;                   // turned to it with the circuit full: its pressure switch is open
        air.pump_wanted = true;
        run(5.0f);
        air.main_reservoir_bar = 2.8f;
        air.bar = 4.34f;                            // four raisings later
        run(5.0f);
        expect(!air.pump_running && air.can_raise && air.breaker_air, $"at {air.bar:F2} bar: the small compressor stays out (its switch cuts in at {pantograph_air.pump_in_bar:F1}), and a pantograph MAY be raised");
        air.pantographs_up = 1;
        run(1.0f);
        expect(air.can_hold && air.breaker_air, $"raised: {air.bar:F2} bar, it stays up and the breaker has its air");
        float minutes = 0.0f;
        while (!air.pump_running && minutes < 120.0f)
        {
            run(1.0f);
            minutes += 1.0f / 60.0f;
        }
        expect(air.pump_running && air.breaker_air, $"left so: the small compressor cuts in by itself at {air.bar:F2} bar, {minutes:F0} min on, before the breaker's air is lost");
        pantograph_air saved = new pantograph_air { bar = 4.0f };
        saved.simulate(0.1f);
        expect(saved.breaker_air && saved.can_raise, "a save's 4.0 bar: the pressure switches are found made");
        pantograph_air low = new pantograph_air { bar = 3.2f };
        low.simulate(0.1f);
        expect(!low.breaker_air && !low.can_raise, "a save's 3.2 bar: found open");
    }

    // The 110 V: its battery, the switch, the breakers.
    private static void low_voltage_bus()
    {
        Console.WriteLine();
        Console.WriteLine("the 110 V");
        low_voltage low = new low_voltage();
        void run(float seconds)
        {
            for (float time = 0.0f; time < seconds; time += 0.5f)
                low.simulate(0.5f);
        }
        low.load_watts = low_voltage.desk_watts;
        run(1.0f);
        expect(low.live && low.volts > 115.0f && low.volts < low_voltage.full_volts, $"full, the switch on, the desk alone: {low.volts:F1} V, {low.battery_amps:F2} A, live");
        low.battery_switch = false;
        run(1.0f);
        expect(!low.live && low.volts == 0.0f && !low.fed(low_breaker.headlights), "the switch off: dead");
        low.battery_switch = true;
        low.breaker[(int) low_breaker.headlights] = false;
        run(1.0f);
        expect(low.live && !low.fed(low_breaker.headlights) && low.fed(low_breaker.lighting), "on again, the headlights' breaker out: they are not fed, the lighting is");
        low.breaker[(int) low_breaker.headlights] = true;
        // standing dark with everything on: desk, both valves, three headlights bright, the cab's light
        low.load_watts = low_voltage.desk_watts + 2.0f * low_voltage.valve_watts + 3.0f * low_voltage.headlight_watts + low_voltage.cab_light_watts;
        float hours = 0.0f;
        while (low.live && hours < 100.0f)
        {
            run(60.0f);
            hours += 1.0f / 60.0f;
        }
        expect(hours > 5.0f && hours < 12.0f && low.charge == 0.0f, $"{low.load_watts:F0} W off the battery alone: flat and dead after {hours:F1} h");
        low.load_watts = 0.0f;
        run(10.0f);
        expect(!low.live, "a flat battery does not come back by being left alone");
        low.supplied = true;
        run(1.0f);
        expect(low.live && low.volts == low_voltage.supplied_volts && low.charging, $"supplied: {low.volts:F0} V at once, {-low.battery_amps:F0} A into the battery");
        hours = 0.0f;
        while (low.charge < 1.0f && hours < 100.0f)
        {
            run(60.0f);
            hours += 1.0f / 60.0f;
        }
        expect(hours > 2.0f && hours < 6.0f && !low.charging, $"full again after {hours:F1} h of that");
    }

    // A joint in the wire, taken at speed with power on: 1.3 s of nothing. The breaker holds, nothing
    // trips as the volts come back, and she pulls again; a dead stretch opens it.
    private static void wire_joint()
    {
        Console.WriteLine();
        Console.WriteLine("a joint in the wire");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = bench_line_volts;
        power.set_reverser(1);
        power.converter_switch = true;
        rig.run(0.5f);
        power.close_fast_breaker();
        rig.km_h = 60.0f;
        power.set_controller(0);
        rig.run(10.0f);
        for (int position = 1; position <= parallel_running; ++position)       // notched up as a driver would, not thrown
        {
            power.set_controller(position);
            rig.run(0.5f);
        }
        rig.run(10.0f);
        float amps = power.ammeter_12, kN = rig.effort_kN;
        power.line_volts = 0.0f;
        rig.run(1.3f);
        bool held = power.fast_breaker_closed;
        power.line_volts = bench_line_volts;
        float peak = 0.0f;
        for (int tick = 0; tick < 300; ++tick)
        {
            rig.tick();
            peak = Math.Max(peak, power.relay_amps);
        }
        rig.run(8.0f);
        expect(held && power.fast_breaker_closed && !power.overcurrent_tripped && power.contactors.position == parallel_running && rig.effort_kN > 0.8f * kN,
            $"on 43 at 60 km/h ({amps:F0} A, {kN:F0} kN): 1.3 s without the wire, and she holds - {peak:F0} A at most as it comes back, then {power.ammeter_12:F0} A, {rig.effort_kN:F0} kN again");
        expect(power.converter_speed > 0.95f, $"the converter has run down and up again by itself ({power.converter_speed:F2})");
        power.line_volts = 0.0f;
        rig.run(power_circuit.undervoltage_hold_seconds + 0.3f);
        expect(!power.fast_breaker_closed, "a dead stretch opens the breaker");
    }

    // The motors' heat: what the blowers are worth, and (asked for) how long hard work takes to cook them.
    private static void motor_heat()
    {
        Console.WriteLine();
        Console.WriteLine("the motors' heat");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = bench_line_volts;
        power.set_reverser(1);
        power.converter_switch = true;
        rig.run(0.5f);
        power.close_fast_breaker();
        rig.run(10.0f);
        void onto_28()
        {
            power.set_controller(0);
            rig.run(2.0f);
            power.reset_relays();
            rig.km_h = 45.0f;                      // fast enough that walking up the notches trips nothing
            power.set_controller(series_running);
            rig.run(12.0f);
        }
        // The speed is changed as a train's would be, a km/h a second: jumped, the current overshoots
        // while the motors' fields catch up, and the relays - which see the wire, not the meters - go out.
        void ease(float to)
        {
            while (Math.Abs(rig.km_h - to) > 0.05f)
            {
                rig.km_h += Math.Max(-0.1f, Math.Min(0.1f, to - rig.km_h));
                rig.run(0.1f);
            }
        }
        // On 28, slowed until the motors take so many amperes (the less speed, the more). Left there.
        void hold(float amps)
        {
            onto_28();
            float slow = 14.0f, fast = 45.0f;
            for (int step = 0; step < 14; ++step)
            {
                float tried = (slow + fast) * 0.5f;
                ease(tried);
                rig.run(6.0f);
                if (power.overcurrent_tripped)
                {
                    onto_28();
                    slow = tried;
                }
                else if (power.ammeter_12 > amps)
                    slow = tried;
                else
                    fast = tried;
            }
            ease(fast);
            rig.run(8.0f);
        }
        const float cold = power_circuit.ambient_celsius, continuous = 510.0f, whole = power_circuit.motor_still_air + power_circuit.motor_blown_air;
        hold(continuous);
        float amps = power.ammeter_12, kW = power.motor_heat * 0.001f, settles = cold + power.motor_heat / whole;
        power.motor_celsius = cold;
        rig.run(1200.0f);
        float blown_20 = power.motor_celsius;
        expect(Math.Abs(amps - continuous) < 15.0f && blown_20 > cold + 20.0f && blown_20 < settles && settles > 70.0f && settles < 90.0f,
            $"on the continuous current ({amps:F0} A, {kW:F1} kW lost): {blown_20:F0} C after twenty minutes from cold, settling at {settles:F0}");
        power.converter_switch = false;
        rig.run(10.0f);
        power.motor_celsius = cold;
        rig.run(1200.0f);
        float still_20 = power.motor_celsius;
        expect(still_20 > blown_20 + 10.0f, $"the same with the converter stopped, no blowers: {still_20:F0} C after twenty minutes");
        float still_minutes = 20.0f;
        while (power.motor_celsius < 120.0f && still_minutes < 240.0f && Environment.GetEnvironmentVariable("EU07_BENCH_MOTOR_HEAT") != null)
        {
            rig.run(60.0f);
            still_minutes += 1.0f;
        }
        power.converter_switch = true;
        power.set_controller(0);
        rig.run(10.0f);
        power.motor_celsius = 100.0f;
        rig.run(600.0f);
        expect(power.motor_celsius < 80.0f, $"hot, wheel at 0, blowers running: from 100 to {power.motor_celsius:F0} C in ten minutes");
        if (Environment.GetEnvironmentVariable("EU07_BENCH_MOTOR_HEAT") == null)
            return;
        Console.WriteLine($"  on the continuous current with the converter stopped: 120 C after {still_minutes:F0} min from cold");
        Console.WriteLine("  held current    lost    settles at    from 80 C to 120 C (the game's limit)");
        foreach (float current in new[] { 510.0f, 548.0f, 600.0f, 670.0f, 750.0f, 880.0f })
        {
            hold(current);
            float took = power.ammeter_12, lost = power.motor_heat;
            power.motor_celsius = 80.0f;
            float minutes = 0.0f;
            while (power.motor_celsius < 120.0f && minutes < 120.0f && !power.overcurrent_tripped)
            {
                rig.run(30.0f);
                minutes += 0.5f;
            }
            Console.WriteLine($"  {took,9:F0} A  {lost * 0.001f,5:F0} kW  {cold + lost / whole,9:F0} C    {(power.overcurrent_tripped ? "relay out" : minutes >= 120.0f ? "never" : $"{minutes:F1} min")}");
        }
        power.set_controller(0);
    }

    // The last-mile battery: what its contactor will and will not do.
    private static void battery()
    {
        Console.WriteLine();
        Console.WriteLine("the last-mile battery");
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = 0.0f;
        power.set_reverser(1);
        expect(!power.close_fast_breaker(), "no wire: the line breaker will not close");
        power.battery_wanted = true;
        rig.run(0.2f);
        expect(power.battery_contactor && !power.fast_breaker_closed, "the battery's contactor closes, the wheel at 0");
        power.set_controller(5);
        rig.run(4.0f);
        float amps = power.ammeter_12, kN = rig.effort_kN, volts = power.battery_volts;
        expect(power.contactors.position == 5 && kN > 5.0f && power.battery_amps > 50.0f && volts < power_circuit.battery_full_volts,
            $"on 5 at a stand: {amps:F0} A a group, {kN:F0} kN, {power.battery_amps:F0} A from the battery at {volts:F0} V");
        expect(power.line_current < 1.0f, $"none of it from the wire ({power.line_current:F1} A)");
        float before = power.battery_joules;
        rig.run(10.0f);
        expect(power.battery_joules < before, $"and it empties: {(before - power.battery_joules) / 3.6e+6f:F2} kWh in ten seconds");
        rig.km_h = 25.0f;
        power.set_controller(parallel_running);
        power.set_field_weakening(6);
        rig.run(40.0f);
        expect(power.contactors.position == series_running && power.contactors.field == 0 && !power.battery_tripped && !power.overcurrent_tripped,
            $"at 25 km/h, asked for 43 and the deepest field step, it goes to {power.contactors.position} and field {power.contactors.field}: the end of series, the field whole");
        rig.km_h = 0.0f;
        power.set_controller(0);
        power.battery_wanted = false;
        rig.run(3.0f);
        power.battery_wanted = true;
        rig.run(1.0f);
        power.line_volts = bench_line_volts;
        expect(power.battery_contactor && !power.close_fast_breaker(), "while it is in, the line breaker will not close");
        power.battery_wanted = false;
        rig.run(0.2f);
        expect(!power.battery_contactor && power.close_fast_breaker(), "out, and the line breaker closes on the wire");
        power.battery_wanted = true;
        rig.run(0.2f);
        expect(!power.battery_contactor, "and with the line breaker in, the battery's contactor stays out");
        power.converter_switch = true;
        rig.run(8.0f);
        before = power.battery_joules;
        rig.run(10.0f);
        expect(power.battery_charging && power.battery_joules > before, $"under the wire with the converter up it charges: {(power.battery_joules - before) / 3.6e+6f:F2} kWh in ten seconds");
        power.battery_joules = 3.6e+4f;                    // a hundredth of a kWh left
        power.open_fast_breaker();
        power.set_controller(0);
        rig.run(1.0f);
        power.line_volts = 0.0f;
        rig.run(0.5f);
        power.set_controller(2);
        rig.run(20.0f);
        expect(!power.battery_contactor && power.battery_joules <= 0.0f && Math.Abs(rig.effort_kN) < 0.5f, "flat, its contactor opens and the pull is gone");
    }

    // One run on the battery, for the table: how fast it gets, on what, and how far a full one goes.
    private static void battery_run(float train_tonnes, float grade, float share)
    {
        const float loco_tonnes = 83.4f, adhesion = 0.30f;
        bench_rig rig = new bench_rig();
        power_circuit power = rig.power;
        power.line_volts = 0.0f;
        power.set_reverser(1);
        power.battery_wanted = true;
        rig.run(0.5f);
        float mass = (loco_tonnes + train_tonnes) * 1000.0f, speed = 0.0f, metres = 0.0f, since_step = 0.0f;
        int position = 0;
        for (float time = 0.0f; time <= 600.0f; time += bench_tick)
        {
            since_step += bench_tick;
            float take_next_below = step_when_below(position);
            // a driver who watches the ammeters: the next notch only under a share of what the battery's breaker allows
            if (since_step >= 0.6f && power.ammeter_12 < Math.Min(take_next_below, share * power_circuit.battery_max_amps) && position < series_running
                && !power.overcurrent_tripped && !power.battery_tripped)
            {
                power.set_controller(++position);
                since_step = 0.0f;
            }
            rig.tick();
            float force = Math.Min(power.wheel_torque / (bench_wheel_diameter * 0.5f), loco_tonnes * 1000.0f * 9.81f * adhesion);
            float v_km_h = speed * 3.6f;
            float resistance = mass * 9.81f * (0.001f * (1.5f + 0.00025f * v_km_h * v_km_h) + grade);
            speed = Math.Max(0.0f, speed + (force - (speed > 0.0f || force > resistance ? resistance : force)) / mass * bench_tick);
            metres += speed * bench_tick;
            rig.km_h = speed * 3.6f;
        }
        float watts = power.battery_volts * power.battery_amps, full = power_circuit.battery_kWh * 3.6e+6f;
        float open = power_circuit.battery_empty_volts + (power_circuit.battery_full_volts - power_circuit.battery_empty_volts) * power.battery_charge;
        string range = speed < 0.3f ? "does not move" : $"{speed * full / Math.Max(open * power.battery_amps, 1.0f) * 0.001f,6:F1}";
        Console.WriteLine($"  {train_tonnes,7:F0}  {grade * 100.0f,4:F1} %  {speed * 3.6f,5:F1}  {power.battery_amps,4:F0}   {power.battery_volts,7:F0}   {watts * 0.001f,6:F0}   {range}" +
            $"   (pos {power.contactors.position}, {(full - power.battery_joules) / 3.6e+6f:F1} kWh and {metres * 0.001f:F1} km in the ten minutes" +
            $"{(power.battery_tripped ? "; ITS BREAKER OPENED" : "")}{(power.overcurrent_tripped ? "; OVERCURRENT" : "")})");
    }

    // What braking with the reverser against the run gives, and what it asks of the motors, at
    // every speed and on the first notches. Printed on request (EU07_BENCH_PLUGGING): a table to
    // read, not a test.
    private static void plugging_table()
    {
        float[] speeds = { 10.0f, 20.0f, 30.0f, 40.0f, 50.0f, 60.0f, 80.0f, 100.0f, 120.0f };
        Console.WriteLine();
        Console.WriteLine("the reverser against the run, ten seconds on a notch: the relays' peak A / kN / rated powers taken in, at the end");
        Console.WriteLine("  (* the overcurrent relays tripped, f a flashover)");
        string heads = "  notch ";
        foreach (float km_h in speeds)
            heads += $"{km_h,14:F0} km/h   ";
        Console.WriteLine(heads);
        foreach (int notch in new[] { 1, 2, 3, 4, 5, 6, 8, 10, 14 })
        {
            string line = $"  {notch,5} ";
            foreach (float km_h in speeds)
            {
                bench_rig rig = new bench_rig();
                power_circuit power = rig.power;
                power.close_fast_breaker();
                power.set_reverser(-1);
                rig.km_h = km_h;
                rig.run(0.5f);
                power.set_controller(notch);
                float amps = 0.0f, stress = 0.0f, kN = 0.0f;
                bool tripped = false;
                for (int tick = 0; tick < 500 && !tripped; ++tick)         // ten seconds: the field is slow to come up
                {
                    rig.run(bench_tick);
                    amps    = Math.Max(amps, power.relay_amps);
                    stress  = power.plugging_stress;
                    kN      = rig.effort_kN;
                    tripped = power.overcurrent_tripped;
                }
                line += $"{amps,6:F0}{(tripped ? '*' : power.flashovers > 0 ? 'f' : ' ')}{kN,5:F0} {stress,5:F2}  |";
            }
            Console.WriteLine(line);
        }
    }

    private const float undervoltage_test_low = power_circuit.undervoltage_dropout - 50.0f;

    private static int first_position_that_trips(bench_rig rig, bool high)
    {
        power_circuit power = rig.power;
        power.high_start = high;
        int tripped_at = -1;
        for (int position = 1; position <= series_running && tripped_at < 0; ++position)
        {
            rig.wheel_to(position);
            rig.run(1.0f);
            if (power.overcurrent_tripped)
                tripped_at = position;
        }
        power.set_controller(0);
        rig.run(0.2f);
        power.reset_relays();
        power.high_start = false;
        return tripped_at;
    }

    // A driver who knows the locomotive: he takes the next position when the ammeters have
    // fallen far enough that the step will land at 0.9 of the relay setting, and no higher
    // than 450 A real. The steps are not alike - the standstill ramp's (1 to 6) and their mirror
    // in parallel (29 to 32) each add a third, the rest about a sixteenth (design_resistors.py).
    // Field steps he takes under 310 A real. Level track, a plain rolling resistance, and
    // adhesion taken at 0.30: where the motors ask for more the wheels would be slipping.
    private static float step_when_below(int position)
    {
        const float land_at = 0.9f * power_circuit.overcurrent_normal, never_above = 450.0f * current_ratio;
        if (position == 0)
            return never_above;
        if (position >= parallel_running)
            return 310.0f * current_ratio;
        // 28 to 29 regroups the motors: each group goes from the whole line to half of it.
        float grows = Math.Max(_standstill_12[position + 1] / _standstill_12[position], _standstill_34[position + 1] / _standstill_34[position]);
        return Math.Min(never_above, land_at / grows);
    }

    // CAREFUL: he also waits for the needles to stop rising - they are damped, and read low for
    // a moment after every notch. A careless driver notches by the reading alone, and as soon as
    // the wheel will turn again (0.3 s, not 0.6), so he notches on a current still on its way up;
    // with a heavy train at a stand that walks him into the relays, and it should ("it should trip if the driver is not careful enough", the
    // author, 2026-10-14, of the starting resistors designed that day - whose standstill notches
    // go up to the full starting current, where the old simulator-derived set stopped near
    // 400 A real and so let a careless hand off).
    private static void start_a_train(bench_rig rig, float train_tonnes, bool careful = true)
    {
        const float loco_tonnes = 83.4f, adhesion = 0.30f;
        power_circuit power = rig.power;
        float mass = (loco_tonnes + train_tonnes) * 1000.0f, speed = 0.0f, metres = 0.0f, since_step = 0.0f;
        float peak_amps = 0.0f, peak_kN = 0.0f, resistor_MJ = 0.0f, line_MJ = 0.0f, slipping = 0.0f, next_print = 0.0f;
        float on_28 = -1.0f, on_43 = -1.0f, at_60 = -1.0f, at_100 = -1.0f, at_120 = -1.0f;
        float hottest = 0.0f, fastest_fans = 0.0f, fans_after = 0.0f;
        int   position = 0, field = 0;
        float last_12 = 0.0f, last_34 = 0.0f;

        Console.WriteLine();
        Console.WriteLine($"a start: locomotive {loco_tonnes} t and {train_tonnes:F0} t behind it, level" + (careful ? "" : ", a CARELESS driver"));
        Console.WriteLine("    s   km/h  pos fld  M1,2 A  M3,4 A  effort kN  fans V  resistors kW");
        rig.km_h = 0.0f;
        rig.run(8.0f);            // the meters are filtered: let the last test die away
        for (float time = 0.0f; time <= 900.0f && at_120 < 0.0f; time += bench_tick)
        {
            since_step += bench_tick;
            float take_next_below = step_when_below(position);
            bool settled = !careful || (power.ammeter_12 <= last_12 && power.ammeter_34 <= last_34);
            last_12 = power.ammeter_12;
            last_34 = power.ammeter_34;
            if (since_step >= (careful ? 0.6f : 0.3f) && settled && power.ammeter_12 < take_next_below && power.ammeter_34 < take_next_below && !power.overcurrent_tripped)
            {
                if (position < parallel_running)
                    power.set_controller(++position);
                else if (field < field_positions - 1)
                    power.set_field_weakening(++field);
                since_step = 0.0f;
            }
            rig.tick();

            float force = power.wheel_torque / (bench_wheel_diameter * 0.5f), grip = loco_tonnes * 1000.0f * 9.81f * adhesion;
            if (force > grip)
            {
                force     = grip;
                slipping += bench_tick;
            }
            float v_km_h     = speed * 3.6f;
            float resistance = mass * 9.81f * 0.001f * (1.5f + 0.00025f * v_km_h * v_km_h);
            speed    = Math.Max(0.0f, speed + (force - (speed > 0.0f || force > resistance ? resistance : force)) / mass * bench_tick);
            metres  += speed * bench_tick;
            rig.km_h = speed * 3.6f;

            peak_amps    = Math.Max(peak_amps, Math.Max(power.ammeter_12, power.ammeter_34));
            peak_kN      = Math.Max(peak_kN, force * 0.001f);
            hottest      = Math.Max(hottest, power.resistor_celsius);
            fastest_fans = Math.Max(fastest_fans, power.fan_speed);
            if (power.on_resistors)
                fans_after = 0.0f;
            else if (power.fan_speed > 0.05f)
                fans_after += bench_tick;
            resistor_MJ += power.resistor_heat * bench_tick * 1e-6f;
            line_MJ     += power.line_volts * power.line_current * bench_tick * 1e-6f;
            if (on_28  < 0.0f && power.contactors.position == series_running  ) on_28  = time;
            if (on_43  < 0.0f && power.contactors.position == parallel_running) on_43  = time;
            if (at_60  < 0.0f && rig.km_h >=  60.0f) at_60  = time;
            if (at_100 < 0.0f && rig.km_h >= 100.0f) at_100 = time;
            if (at_120 < 0.0f && rig.km_h >= 120.0f) at_120 = time;
            if (time >= next_print)
            {
                Console.WriteLine($"{time,5:F0} {rig.km_h,6:F1}  {power.contactors.position,3} {power.contactors.field,3} {power.ammeter_12,7:F0} {power.ammeter_34,7:F0} " +
                    $"{force * 0.001f,10:F1} {power.fan_volts,7:F0} {power.resistor_heat * 0.001f,13:F0}");
                next_print += time < 60.0f ? 10.0f : 60.0f;
            }
        }
        string when(float seconds) => seconds < 0.0f ? "never" : $"{seconds:F0} s";
        Console.WriteLine($"  the resistors reached {hottest:F0} C, the fans {fastest_fans:F2} of rated speed, and ran on {fans_after:F0} s after the last resistor was out (so far)");
        Console.WriteLine($"  on 28 after {when(on_28)}, on 43 after {when(on_43)}; 60 km/h {when(at_60)}, 100 km/h {when(at_100)}, 120 km/h {when(at_120)}; {metres * 0.001f:F1} km run");
        Console.WriteLine($"  peak {peak_amps:F0} A a motor, {peak_kN:F0} kN; {line_MJ:F0} MJ from the wire, {resistor_MJ:F0} MJ of it into the resistors; " +
            $"asked for more than the rails give for {slipping:F1} s");
        if (careful)
            expect(!power.overcurrent_tripped, "no relay tripped");
        else
            expect(power.overcurrent_tripped, "notching on a rising needle, he trips the relays");

        power.set_field_weakening(0);
        power.set_controller(0);
        rig.km_h = 0.0f;
        rig.run(2.0f);
        power.reset_relays();
    }
}
