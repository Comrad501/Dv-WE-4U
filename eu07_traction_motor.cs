// EU07 / 303E - the EE541 traction motor, for Knsgf's electric_sim.
//
// After Knsgf's devices/traction_motor.cs (CC0), used the same way: reads its three
// branches' currents, sets the armature EMF, reports wheel torque. Differs in the flux:
// Knsgf's (WE6) saturates flat above 300 A, where ours runs and weakening would do nothing.
// Here the flux bends:
//
//     K(If) = K_infinity * If / (If + I_half)        volts per rad/s, and newton metres per amp
//
// fitted to the 303E DTR's EE541 points on 1500 V; resistance CTK Bydgoszcz 0.099 ohm at
// 20 C (30% main poles), taken warm at 0.14:
//
//     hourly      370 A   956 rpm  100% field    K wanted 14.47   curve 14.46
//     continuous  355 A   970 rpm  100% field    K wanted 14.28   curve 14.28
//     continuous  355 A  2098 rpm   22% field    K wanted  6.66   curve  6.68
//
// (2098 rpm = 109 km/h at 22%; 970 rpm = 50.4 km/h.) First two fix the curve, third checks
// within 0.4%. Scaled to this model: half the volts per rad/s, 1.5x amps (docs/NOTES.md).

using System;
using System.Collections.Generic;

using LocoSim.Implementations;

using electric_sim.circuit_sim;

namespace electric_sim.unit_A;

internal class eu07_traction_motor
{
    public const float gear_ratio = 79.0f / 18.0f;

    // ---- real motor, 1500 V ---------------------------------------------------------------
    private const float real_K_infinity = 21.02f;     // V s / rad
    private const float real_I_half     = 167.6f;     // A field current for half K
    private const float real_resistance = 0.14f;      // ohm, whole motor, warm

    // ---- this model -----------------------------------------------------------------------
    public const float K_infinity          = real_K_infinity * eu07_sim.line_voltage_ratio;
    public const float I_half              = real_I_half     * eu07_sim.current_ratio;
    public const float internal_resistance = real_resistance * eu07_sim.resistor_scale;
    public const float armature_part       = 0.70f;   // armature + compensating + commutating; rest main poles
    public const float field_partitioning  = 0.5f;    // field as two halves, MFna and MFnb

    // electromagnetic -> rim torque: DTR 145.3 kN at 355 A = 0.988 of K * I
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

    // loop gain filtered current -> field -> EMF -> current: dEMF/dIf over this motor's circuit ohms
    // highest unsaturated, at speed, on 28/43; volts_share: line share (1/4 series, 1/2 parallel);
    // ohms = (share - EMF) / I, never below the windings'
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

    // series motor can't feed back: reverse current unmakes the field. The solver doesn't know
    // (stale field + new connection, e.g. parallel -> series at speed, gives huge reverse amps).
    // reverse solver current: filtered currents jump to where EMF just fills the line share; true if so
    public bool unmake_field(Dictionary<string, float> currents, Dictionary<string, circuit.branch_user> named_branches, float volts_share)
    {
        float filtered = currents[_armature_name], solved = named_branches[_armature_name].current;
        if (filtered * solved >= 0.0f || Math.Abs(solved) < 50.0f)
            return false;
        float speed = Math.Abs(_EMF_multiplier * _wheel_RPM.Value * gear_ratio * ((float) Math.PI / 30.0f));
        float K = speed > 1.0e-3f ? Math.Abs(volts_share) / speed : float.PositiveInfinity;      // field the line share asks for
        float settles = K < 0.98f * K_infinity ? I_half * K / (K_infinity - K) : Math.Abs(filtered);
        settles = Math.Min(settles, Math.Abs(filtered)) * Math.Sign(filtered);                 // only weakened
        currents[_armature_name] = currents[_field1_name] = currents[_field2_name] = settles;
        return true;
    }

    // V per rad/s at this field current, signed like the current
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

        // sign and 0.75 from Knsgf: EMF opposes current when armature/field same sign, wheel forward;
        // 0.75: solver a step behind, full new EMF at once rings
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
