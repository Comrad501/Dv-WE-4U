// EU07 / 303E - the EE541 traction motor, for Knsgf's electric_sim.
//
// Written by hand, after his devices/traction_motor.cs (CC0), and used the same way: it
// reads its three branches' currents, sets the armature branch's EMF, and reports torque
// at the wheel. What differs is the flux.
//
// His motor's flux is its field current, level above 300 A. That is a WE6 motor; ours runs
// at 530 A continuous and would sit on the level part the whole time, where weakening the
// field does nothing at all. Here the flux bends:
//
//     K(If) = K_infinity * If / (If + I_half)        volts per rad/s, and newton metres per amp
//
// FITTED to the three points the 303E's DTR gives for an EE541 on 1500 V. The motor's
// resistance is CTK Bydgoszcz's measurement, 0.099 ohm at 20 C with 30% of it in the main
// poles, taken warm at 0.14:
//
//     hourly      370 A   956 rpm  100% field    K wanted 14.47   curve 14.46
//     continuous  355 A   970 rpm  100% field    K wanted 14.28   curve 14.28
//     continuous  355 A  2098 rpm   22% field    K wanted  6.66   curve  6.68
//
// (2098 rpm is the DTR's 109 km/h at 22%, on the same wheel that makes 970 rpm 50.4 km/h.)
// The first two fix the curve; the third was not used and lands within 0.4%. Then scaled
// to this locomotive, which is on 1500 V: half the volts a rad/s, one and a half times
// the amps. docs/NOTES.md has the arithmetic.

using System;
using System.Collections.Generic;

using LocoSim.Implementations;

using electric_sim.circuit_sim;

namespace electric_sim.unit_A;

internal class eu07_traction_motor
{
    public const float gear_ratio = 79.0f / 18.0f;

    // ---- the real motor, 1500 V across it ------------------------------------------------
    private const float real_K_infinity = 21.02f;     // V s / rad
    private const float real_I_half     = 167.6f;     // A of field current for half of that
    private const float real_resistance = 0.14f;      // ohm, the whole motor, warm

    // ---- this locomotive's ---------------------------------------------------------------
    public const float K_infinity          = real_K_infinity * eu07_sim.line_voltage_ratio;
    public const float I_half              = real_I_half     * eu07_sim.current_ratio;
    public const float internal_resistance = real_resistance * eu07_sim.resistor_scale;
    public const float armature_part       = 0.70f;   // armature, compensating and commutating; the rest is main poles
    public const float field_partitioning  = 0.5f;    // the field is drawn as two halves, MFna and MFnb

    // Electromagnetic torque to torque at the rim. The DTR's 145.3 kN at 355 A is 0.988 of
    // what K * I gives, so there is almost nothing to take off.
    private const float torque_efficiency = 0.99f;

    private readonly Port   _wheel_RPM;
    private readonly string _armature_name, _field1_name, _field2_name;
    private readonly float  _armature_resistance, _field1_resistance, _field2_resistance;
    private readonly float  _torque_multiplier, _EMF_multiplier;

    public float RPM           { get; private set; }
    public float wheel_torque  { get; private set; }
    public float load_current  { get; private set; }
    public float field_current { get; private set; }
    public float EMF           { get; private set; }
    public float heat_emission { get; private set; }

    public eu07_traction_motor(int motor_number, float torque_multiplier, float EMF_multiplier,
        Port wheel_RPM, Dictionary<string, circuit.branch_user> named_branches)
    {
        _wheel_RPM     = wheel_RPM;
        _armature_name = $"MA{motor_number}";
        _field1_name   = $"MF{motor_number}a";
        _field2_name   = $"MF{motor_number}b";

        _armature_resistance = named_branches[_armature_name].closed_resistance;
        _field1_resistance   = named_branches[  _field1_name].closed_resistance;
        _field2_resistance   = named_branches[  _field2_name].closed_resistance;

        _torque_multiplier = torque_multiplier;
        _EMF_multiplier    = EMF_multiplier;
    }

    // How hard this motor's back-EMF answers its own filtered current, against the resistance
    // that current meets: volts of EMF for an ampere of field current, over the ohms this
    // motor's share of the circuit comes to. It is the gain of the loop "filtered current ->
    // field -> EMF -> current", and is greatest where the iron is far from saturation, the
    // speed high and nothing but the windings in the circuit: little current, at speed, on 28
    // or 43. `volts_share` is this motor's share of the wire's voltage (a quarter in series,
    // a half in parallel): what is left of it over the EMF, by the current, is the ohms - the
    // starting resistance with them, when it is in. Never less than the windings' own.
    public float loop_gain(Dictionary<string, float> currents, float volts_share)
    {
        float slope(float amps) => K_infinity * I_half / ((Math.Abs(amps) + I_half) * (Math.Abs(amps) + I_half));
        float volts_per_amp = Math.Abs(_EMF_multiplier * _wheel_RPM.Value * gear_ratio * ((float) Math.PI / 30.0f))
                            * (slope(currents[_field1_name]) * (1.0f - field_partitioning) + slope(currents[_field2_name]) * field_partitioning);
        float own_ohms = _armature_resistance + _field1_resistance + _field2_resistance;
        float met_ohms = (Math.Abs(volts_share) - Math.Abs(EMF)) / Math.Max(Math.Abs(currents[_armature_name]), 5.0f);
        return volts_per_amp / Math.Max(own_ohms, met_ohms);
    }

    public bool owns(string branch) => branch == _armature_name || branch == _field1_name || branch == _field2_name;

    // A series motor cannot push current back into the wire: a current against the one that
    // made its field unmakes the field, and the EMF goes with it. The solver does not know
    // that - handed a field a second old and a connection just changed (43 run back through
    // 29 to 28 at speed: four EMFs in series where two were, twice the wire's voltage) it
    // returns thousands of amperes BACKWARDS, and the relays, which do not ask which way, go
    // ("got a ghost overload" at 128.6 km/h, the author, 2026-10-10: 6244 A).
    // So when the solver's current runs against this motor's filtered one, the filtered
    // currents are put at once where such a motor settles: the current at which its EMF
    // just fills its share of the wire's voltage. True if it did so.
    public bool unmake_field(Dictionary<string, float> currents, Dictionary<string, circuit.branch_user> named_branches, float volts_share)
    {
        float filtered = currents[_armature_name], solved = named_branches[_armature_name].current;
        if (filtered * solved >= 0.0f || Math.Abs(solved) < 50.0f)
            return false;
        float speed = Math.Abs(_EMF_multiplier * _wheel_RPM.Value * gear_ratio * ((float) Math.PI / 30.0f));
        float K = speed > 1.0e-3f ? Math.Abs(volts_share) / speed : float.PositiveInfinity;      // the field its share of the wire asks for
        float settles = K < 0.98f * K_infinity ? I_half * K / (K_infinity - K) : Math.Abs(filtered);
        settles = Math.Min(settles, Math.Abs(filtered)) * Math.Sign(filtered);                 // unmade, never made stronger
        currents[_armature_name] = currents[_field1_name] = currents[_field2_name] = settles;
        return true;
    }

    // Volts per rad/s at this field current; signed like the current.
    public static float flux(float field_current)
    {
        return K_infinity * field_current / (Math.Abs(field_current) + I_half);
    }

    public void simulate(Dictionary<string, float> currents, Dictionary<string, circuit.branch_user> named_branches)
    {
        float motor_RPM       = _wheel_RPM.Value * gear_ratio;
        float armature_current = currents[_armature_name];
        float field1_current   = currents[_field1_name], field2_current = currents[_field2_name];

        float K = flux(field1_current) * (1.0f - field_partitioning) + flux(field2_current) * field_partitioning;

        // The sign is his: an EMF against the current when armature and field currents have
        // the same sign and the wheel turns forward. And so is the 0.75: the solver is a
        // step behind the motor, and the whole of a new EMF at once makes the pair ring.
        float motor_EMF = -_EMF_multiplier * K * motor_RPM * ((float) Math.PI / 30.0f);
        circuit.branch_user armature = named_branches[_armature_name];
        EMF           = armature.EMF = armature.EMF + (motor_EMF - armature.EMF) * 0.75f;
        RPM           = motor_RPM;
        load_current  = armature_current;
        field_current = field2_current;
        wheel_torque  = _torque_multiplier * torque_efficiency * K * armature_current * gear_ratio;
        heat_emission = armature_current * armature_current * _armature_resistance
                      +   field1_current *   field1_current *   _field1_resistance
                      +   field2_current *   field2_current *   _field2_resistance;
    }
}
