// EU07 for Derail Valley - stock DE6 and DH4 on the bench, for comparison with the WE-4U.
// Port of the game's LocoSim components (DV.Simulation.dll; AnimationCurves are native, can't run here);
// settings from the game's resources.assets.
//   DE6: ThrottleGammaPowerConversion -> ConfigurableFunction(MAX) -> DieselEngineDirect
//        -> TractionGenerator (PID on rpm) -> TractionMotorSet (6 motors, 4 configs) -> TransmissionFixedGear;
//        motor heat: HeatReservoir + PassiveCooler
//   DH4: DieselEngineDirect -> HydraulicTransmission (3 configs: torque converters, coupling) -> wheels;
//        one coolant reservoir, passive + automatic cooler
// left out: compressor load, fuel, oil, wear, breakdowns, fire
// force = torque / wheel radius (DrivingForce); slip: bench rule (adhesion 0.30 dry, 0.25 wet)

using System;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

namespace electric_sim.unit_A;

// Unity AnimationCurve: Hermite from tangents, Bezier if weighted; flat beyond the ends (ClampForever)
internal sealed class stock_curve
{
    private readonly (float t, float v, float in_slope, float out_slope, int mode, float in_w, float out_w)[] _keys;

    public stock_curve(params (float t, float v, float in_slope, float out_slope, int mode, float in_w, float out_w)[] keys) => _keys = keys;

    public float max_value => _keys.Max(k => k.v);
    public float time_of_max => _keys.First(k => k.v == max_value).t;

    public float evaluate(float x)
    {
        if (x <= _keys[0].t)
            return _keys[0].v;
        if (x >= _keys[_keys.Length - 1].t)
            return _keys[_keys.Length - 1].v;
        int i = 0;
        while (x > _keys[i + 1].t)
            ++i;
        var a = _keys[i];
        var b = _keys[i + 1];
        float dt = b.t - a.t;
        bool weighted = (a.mode & 2) != 0 || (b.mode & 1) != 0;
        if (!weighted)
        {
            float s = (x - a.t) / dt, s2 = s * s, s3 = s2 * s;
            float m0 = a.out_slope * dt, m1 = b.in_slope * dt;
            return (2 * s3 - 3 * s2 + 1) * a.v + (s3 - 2 * s2 + s) * m0 + (-2 * s3 + 3 * s2) * b.v + (s3 - s2) * m1;
        }
        float w0 = (a.mode & 2) != 0 ? a.out_w : 1.0f / 3.0f, w1 = (b.mode & 1) != 0 ? b.in_w : 1.0f / 3.0f;
        float x0 = a.t, x1 = a.t + w0 * dt, x2 = b.t - w1 * dt, x3 = b.t;
        float y0 = a.v, y1 = a.v + w0 * dt * a.out_slope, y2 = b.v - w1 * dt * b.in_slope, y3 = b.v;
        float bez(float p0, float p1, float p2, float p3, float u) => (1 - u) * (1 - u) * (1 - u) * p0 + 3 * (1 - u) * (1 - u) * u * p1 + 3 * (1 - u) * u * u * p2 + u * u * u * p3;
        float lo = 0.0f, hi = 1.0f;
        for (int k = 0; k < 40; ++k)
        {
            float mid = 0.5f * (lo + hi);
            if (bez(x0, x1, x2, x3, mid) < x)
                lo = mid;
            else
                hi = mid;
        }
        return bez(y0, y1, y2, y3, 0.5f * (lo + hi));
    }
}

// DieselEngineDirect.SimulateTorque; no fuel, oil, retarder, damage; engine on
internal sealed class stock_engine
{
    public readonly float inertia, damping, rpm_max, rpm_idle, rpm_min, torque_idle;
    public readonly stock_curve power;
    public float rpm, torque_generated, heat_out;
    private float _torque_velocity;

    public stock_engine(float inertia, float damping, float rpm_max, float rpm_idle, stock_curve power)
    {
        this.inertia = inertia; this.damping = damping; this.rpm_max = rpm_max; this.rpm_idle = rpm_idle; this.power = power;
        rpm_min = 0.4f * rpm_idle;
        torque_idle = (float) Math.PI / 30f * rpm_idle * damping;
        rpm = rpm_idle;
    }

    public void tick(float delta, float throttle, float load_torque, float? driven_rpm)
    {
        float value = rpm;
        float viscous = (float) Math.PI / 30f * value * damping;
        float target = Mathf.Lerp(torque_idle, power.evaluate(value) / ((float) Math.PI / 30f * Math.Max(value, 1e-3f)) + viscous, throttle);
        torque_generated = Mathf.SmoothDamp(torque_generated, target, ref _torque_velocity, 0.2f, float.PositiveInfinity, delta);
        float net = torque_generated - viscous;
        heat_out = Mathf.Max(0f, net * ((float) Math.PI / 30f) * value);
        float total = net + load_torque;
        float next = rpm + total / inertia * delta;
        if (driven_rpm.HasValue && total > 0f && net < 0f)
            next = Mathf.Min(next, driven_rpm.Value);
        rpm = next < 0.01f ? 0f : next;
    }
}

internal interface stock_loco
{
    string name { get; }
    float mass_kg { get; }
    float wheel_radius { get; }
    float red_line_c { get; }                 // driver eases off here
    float temperature_c { get; }              // watched gauge
    float amps_ratio { get; }                 // ammeter / full scale (none: 0)
    string? failed { get; }                   // fuse out, transmission blown: run over
    float throttle { get; set; }
    float tick(float delta, float wheel_rpm); // wheel torque, N m
}

internal sealed class stock_de6 : stock_loco
{
    public string name => "DE6";
    public float mass_kg => 119150f;
    public float wheel_radius => 0.5335f;
    public float red_line_c => 120f;
    public float temperature_c => _tm_temp;
    public float amps_ratio => _amps_per_tm / 1500f;                // maxAmpsPerTm: ammeter full scale
    public string? failed { get; private set; }
    public float throttle { get; set; }

    private readonly stock_engine _de = new(50f, 100f, 950f, 315f, new stock_curve(
        (0f, 0f, 1440.5848f, 1440.5848f, 0, 1f / 3f, 0.10329206f), (275f, 300000f, 1494.04f, 1494.04f, 0, 1f / 3f, 0.04434629f),
        (900f, 1700000f, 0f, 0f, 0, 1f / 3f, 1f / 3f), (950f, 0f, -68105.203f, -68105.203f, 0, 0.035001222f, 1f / 3f)));
    // throttlePower
    private const int notches = 11;
    private const float gamma = 1.2f;
    // tractionGenerator
    private const float g_max_volts = 600f, g_torque_factor = 8f, g_max_amps = 5000f, g_kp = 5f, g_kd = 10f, g_max_speed = 1f, g_gain_speed = 0.2f, g_drop_speed = 1f;
    private float _g_throttle, _g_excitation, _g_volts, _g_load_torque, _pid_prev;
    // tm
    private const float max_motor_rpm = 2200f, motor_r = 0.015f, motor_k = 0.003f, external_r = 0.015f, amps_smooth = 0.5f, amps_smooth_max = 5000f, transition_max_amps = 100f;
    private const float overheat_threshold = 120f, overheat_max_time = 5f;
    private const int motors = 6;
    private static readonly (float excitation, int groups, int series, float fwd, bool fwd_above, float back, bool back_above)[] configs =
    {
        (1.00f, 2, 3, 500f, true, 0f, true),
        (1.00f, 3, 2, 900f, true, 450f, false),
        (0.78f, 3, 2, 1100f, true, 850f, false),
        (0.60f, 3, 2, 0f, true, 1000f, false),
    };
    private int _active, _pending;
    private bool _drop_requested, _circuit_active, _fuse = true;
    private float _connect_timer = 1f, _amps_per_tm, _amps_velocity, _overheat_timer = overheat_max_time, _effective_r = float.PositiveInfinity, _single_r = float.PositiveInfinity, _total_amps;
    // tmHeat, tmPassiveCooler
    private const float heat_capacity = 15000f, cooling_rate = 250f, max_temp = 300f;
    private float _tm_temp = 25f;
    // transmission
    private const float gear = 4.1333332f;

    public float tick(float delta, float wheel_rpm)
    {
        // throttlePower (ThrottleGammaPowerConversion)
        float idle_n = _de.rpm_idle / _de.rpm_max, max_power_n = _de.power.time_of_max / _de.rpm_max;
        int notch = Mathf.RoundToInt(throttle * (notches - 1));
        float per_notch = (max_power_n - idle_n) / (notches - 2);
        float goal_rpm_n = notch <= 1 ? idle_n : idle_n + per_notch * (notch - 1);
        float goal_power = _de.power.max_value * Mathf.Pow((float) notch / (notches - 1), gamma);
        // throttleCalculator: MAX(governor, primer 0); the engine
        _de.tick(delta, Mathf.Max(_g_throttle, 0f), _g_load_torque, null);
        float rpm_n = _de.rpm / _de.rpm_max;
        // tmRpmCalculator
        float motor_rpm = wheel_rpm * gear;
        // tm: damage, circuit, transition, volts, amps, torque, heat
        _overheat_timer = Mathf.Clamp(_overheat_timer + (_tm_temp > overheat_threshold ? -delta : delta), 0f, overheat_max_time);
        if (_overheat_timer == 0f && _fuse)
        {
            _fuse = false;
            failed = "motors overheated: power fuse out";
        }
        bool requested = throttle > 0f;
        if (!_fuse || !requested)
        {
            _circuit_active = false;
            _connect_timer = 1f;
        }
        else if (!_circuit_active)
        {
            _connect_timer -= delta;
            if (_connect_timer <= 0f)
                _circuit_active = true;
        }
        float circuit_r = _circuit_active ? external_r : float.PositiveInfinity;
        var cfg = configs[_active];
        if (_active == _pending)
        {
            bool cond(float threshold, bool above) => above ? Math.Abs(motor_rpm) > threshold : Math.Abs(motor_rpm) < threshold;
            if (_active > 0 && cond(cfg.back, cfg.back_above))
            {
                _pending = _active - 1;
                _drop_requested = configs[_pending].groups != cfg.groups || configs[_pending].series != cfg.series;
            }
            else if (_active + 1 < configs.Length && cond(cfg.fwd, cfg.fwd_above))
            {
                _pending = _active + 1;
                _drop_requested = configs[_pending].groups != cfg.groups || configs[_pending].series != cfg.series;
            }
        }
        else if (!_drop_requested || _total_amps < transition_max_amps)
        {
            _active = _pending;
            _drop_requested = false;
        }
        cfg = configs[_active];
        float current_limit = _drop_requested ? 0f : float.PositiveInfinity;
        float set_current = cfg.groups, set_resistance = (float) cfg.series / cfg.groups;
        float omega = (float) Math.PI / 30f * motor_rpm;
        float flux_factor = 1f * cfg.excitation;                     // reverser forward
        float amps_target;
        float k_flux = motor_k * flux_factor;
        float motor_part = motor_r + k_flux * omega;
        _effective_r = circuit_r + motor_part * set_resistance;
        _single_r = _effective_r * set_current;
        amps_target = _single_r == 0f || float.IsInfinity(_single_r) ? 0f : _g_volts / _single_r;
        _amps_per_tm = Mathf.SmoothDamp(_amps_per_tm, amps_target, ref _amps_velocity, amps_smooth, amps_smooth_max, delta);
        _total_amps = _amps_per_tm * set_current;
        float flux = flux_factor * _amps_per_tm;
        float tm_torque = motor_k * flux * _amps_per_tm * motors;
        float tm_heat = _amps_per_tm * _amps_per_tm * motor_r;
        // slugsPowerCalculator: no slug, pass-through
        // tractionGenerator: overcurrent, PID, volts, load torque
        if (_total_amps > g_max_amps && _fuse)
        {
            _fuse = false;
            failed = "generator overcurrent: power fuse out";
        }
        float error = goal_rpm_n - rpm_n;
        float pid = g_kp * error + g_kd * (error - _pid_prev) / delta;
        _pid_prev = error;
        _g_throttle = Mathf.Clamp01(_g_throttle + Mathf.Clamp(pid, -g_max_speed, g_max_speed) * delta);
        float g_omega = _de.rpm * ((float) Math.PI / 30f);
        float want;
        if (!_fuse || float.IsInfinity(_effective_r))
            want = 0f;
        else
        {
            float v_goal = _effective_r > 0f ? Mathf.Sqrt(goal_power * _effective_r) : 0f;
            float k = Mathf.Lerp(1f, Mathf.Min(1f, rpm_n / goal_rpm_n), (_g_throttle - 0.99f) / 0.01f);   // NumberUtil.MapClamp(throttle, 0.99, 1, 1, min(1, rpm/goal))
            float a = v_goal * k;
            float b = float.IsInfinity(current_limit) ? float.PositiveInfinity : current_limit * _single_r;
            float v = Mathf.Min(g_max_volts, Mathf.Min(a, b));
            want = Mathf.Clamp01(v / Math.Max(g_omega, 1e-3f) / g_torque_factor);
        }
        if (want > _g_excitation)
            _g_excitation = Mathf.Min(want, _g_excitation + g_gain_speed * delta);
        else if (want < _g_excitation)
            _g_excitation = Mathf.Max(want, _g_excitation - g_drop_speed * delta);
        _g_volts = g_torque_factor * _g_excitation * g_omega;
        _g_load_torque = -_g_excitation * g_torque_factor * _total_amps;
        // tmHeat with its passive cooler
        float cooler = (25f - _tm_temp) * cooling_rate;
        _tm_temp = Mathf.Min(_tm_temp + (tm_heat + cooler) * delta / heat_capacity, max_temp);
        // transmission
        return _fuse ? tm_torque * gear : 0f;
    }
}

internal sealed class stock_dh4 : stock_loco
{
    public string name => "DH4";
    public float mass_kg => 74230f;
    public float wheel_radius => 0.535f;
    public float red_line_c => 120f;          // transmission blows > 120 (engine hurt > 110)
    public float temperature_c => _coolant;
    public float amps_ratio => 0f;
    public string? failed { get; private set; }
    public float throttle { get; set; }

    private readonly stock_engine _de = new(10f, 40f, 1600f, 600f, new stock_curve(
        (0f, 0f, 842.26666f, 842.26666f, 0, 1f / 3f, 0.031815805f), (1500f, 1000000f, 0.000969854f, 0.000969854f, 3, 1f / 3f, 1f),
        (1600f, 0f, -27790.76f, -27790.76f, 0, 0.06279419f, 1f / 3f)));
    private sealed class config
    {
        public float capacity, stall, coupling, efficiency, gear, up, down, term_a, term_d, fill, pump_rpm, turbine_rpm, pump_torque, turbine_torque, multiplier, heat;
        public config(float capacity, float stall, float coupling, float efficiency, float gear, float up, float down)
        {
            this.capacity = capacity; this.stall = stall; this.coupling = coupling; this.efficiency = efficiency; this.gear = gear; this.up = up; this.down = down;
            float n = stall, c = coupling, e = efficiency;
            term_a = (c * ((2f - n) * e - n * n * (e - c)) - 2f * (n - 1f) * (n - 1f) * Mathf.Sqrt(c * e * e * (e - c) / (n - 1f))) / (c * (n * n * c + (4f - 4f * n) * e));
            term_d = c * (term_a - 1f) / (1f - n);
            if (float.IsNaN(term_a) || float.IsInfinity(term_a) || float.IsNaN(term_d) || float.IsInfinity(term_d)) { term_a = 1f; term_d = 0f; }
        }
        public static float pump(float capacity, float fill, float pump_rpm, float turbine_rpm)
        {
            if (fill == 0f) return 0f;
            float f = turbine_rpm - pump_rpm;
            float a = (float) Math.PI / 30f * Mathf.Abs(f), b = (float) Math.PI / 30f * Mathf.Abs(pump_rpm + turbine_rpm);
            return Mathf.Sign(f) * capacity * fill * Mathf.Sqrt(a * a * a * b);
        }
        public void tick()
        {
            if (fill == 0f) { pump_torque = 0f; multiplier = 0f; turbine_torque = 0f; heat = 0f; return; }
            pump_torque = pump(capacity, fill, pump_rpm, turbine_rpm);
            float ratio = Mathf.Clamp01(pump_rpm == 0f ? 0f : turbine_rpm / pump_rpm);
            float m = (term_a * ratio + term_d * stall) / (ratio + term_d);
            multiplier = float.IsNaN(m) || float.IsInfinity(m) ? stall : Mathf.Clamp(m, 0f, stall);
            turbine_torque = -pump_torque * multiplier;
            heat = Mathf.Abs((float) Math.PI / 30f * pump_rpm * -pump_torque - (float) Math.PI / 30f * turbine_rpm * turbine_torque);
        }
    }
    private readonly config[] _configs =
    {
        new(0.5f, 5.1f, 0.78f, 0.86f, 4.5f, 0.12f, 0f),
        new(1.5f, 2.67f, 0.86f, 0.86f, 4.5f, 0.18f, 0.11f),
        new(1.5f, 2.67f, 0.86f, 0.86f, 3.0f, 0f, 0.17f),
    };
    private int _active;
    private const float fill_time = 5f, overheat_threshold = 120f, overheat_max_time = 5f;
    private float _input_torque, _turbine_rpm, _coolant = 25f, _overheat_timer, _cooler_effect, _cooler_velocity;
    private bool _cooler_on, _broken;
    private readonly System.Random _random = new(7);
    private const float coolant_capacity = 500000f, passive_rate = 3750f, auto_rate = 8000f, auto_on = 85f, auto_off = 75f, auto_ease = 0.8f;

    public float tick(float delta, float wheel_rpm)
    {
        _de.tick(delta, throttle, _input_torque, _turbine_rpm);
        float input_rpm = _de.rpm;
        // SimulateGearSelect
        float ratio = input_rpm == 0f ? 0f : Mathf.Abs(wheel_rpm / input_rpm);
        if (_active > 0 && ratio < _configs[_active].down) --_active;
        else if (_active + 1 < _configs.Length && ratio > _configs[_active].up) ++_active;
        float max_fill = _broken ? 0f : Mathf.Clamp01(Mathf.InverseLerp(500f, 600f, input_rpm));     // pumpRpmFillCurve: 0 at 500, 1 at 600
        max_fill = max_fill * max_fill * (3f - 2f * max_fill);
        bool fill = throttle > 0.01f;
        for (int i = 0; i < _configs.Length; ++i)
        {
            float goal = fill && i == _active ? max_fill : 0f;
            var c = _configs[i];
            c.fill = c.fill < goal ? Mathf.Min(goal, c.fill + delta / fill_time) : Mathf.Max(goal, c.fill - delta / fill_time);
        }
        // SimulateCouplings (reverser forward, no freewheel)
        foreach (var c in _configs)
        {
            c.pump_rpm = input_rpm;
            c.turbine_rpm = wheel_rpm * c.gear;
            c.tick();
        }
        // SumOutputs
        float pump_sum = 0f, out_sum = 0f, heat = 0f;
        foreach (var c in _configs)
            if (c.fill > 0f) { pump_sum += c.pump_torque; out_sum += c.turbine_torque * c.gear; heat += c.heat; }
        _input_torque = pump_sum;
        _turbine_rpm = _configs[_active].turbine_rpm;
        // SimulateDamage: > 120, 1-5 s, then blown
        if (!_broken)
        {
            if (_coolant > overheat_threshold && _overheat_timer <= 0f)
                _overheat_timer = 1f + (float) _random.NextDouble() * (overheat_max_time - 1f);
            if (_overheat_timer > 0f)
            {
                if (_coolant <= overheat_threshold) _overheat_timer = 0f;
                else
                {
                    _overheat_timer -= delta;
                    if (_overheat_timer <= 0f || _coolant > overheat_threshold * 1.75f)
                    {
                        _broken = true;
                        failed = "transmission overheated and blew";
                    }
                }
            }
        }
        // coolant: engine + transmission heat, passive + automatic cooler
        float passive = (25f - _coolant) * passive_rate;
        if (!_cooler_on && _coolant > auto_on) _cooler_on = true;
        else if (_cooler_on && _coolant < auto_off) _cooler_on = false;
        float want = _cooler_on ? 1f : 0f;
        if (_cooler_effect != want)
            _cooler_effect = Mathf.SmoothDamp(_cooler_effect, want, ref _cooler_velocity, auto_ease, float.PositiveInfinity, delta);
        float automatic = (25f - _coolant) * _cooler_effect * auto_rate;
        _coolant = Mathf.Min(_coolant + (_de.heat_out + heat + passive + automatic) * delta / coolant_capacity, 300f);
        return _broken ? 0f : out_sum;
    }
}

internal static class stock_bench
{
    private const float tick_s = 0.02f;

    // load rating, as eu07_bench.load_run: to 30 km/h in 15 min; 0.30 dry, 0.25 wet; 5 s unbroken slip fails
    // throttle +0.1 per 2 s while gripping, under red line, ammeter < 0.9; -0.1 on slip, red line or full scale
    private static bool run(Func<stock_loco> make, float train_tonnes, float grade, float adhesion, out string how)
    {
        stock_loco loco = make();
        float mass = loco.mass_kg + train_tonnes * 1000f, speed = 0f, hottest = 0f, peak_kN = 0f, since = 0f;
        loco.throttle = 0f;
        for (float t = 0f; t < 20f; t += tick_s)          // settle at idle
            loco.tick(tick_s, 0f);
        loco.throttle = 0.1f;                                // first notch
        float slip_run = 0f, grip_run = 2f;
        for (float time = 0f; time <= 900f; time += tick_s)
        {
            since += tick_s;
            // -0.1 per second of slip or at red line; +0.1 once gripping and 5 degrees under
            bool hot = loco.temperature_c >= loco.red_line_c || loco.amps_ratio >= 1f, cool = loco.temperature_c < loco.red_line_c - 5f && loco.amps_ratio < 0.9f;
            if ((slip_run > 0f || hot) && since >= 1f && loco.throttle > 0.1f) { loco.throttle = Mathf.Max(0.1f, loco.throttle - 0.1f); since = 0f; }
            else if (slip_run == 0f && grip_run >= 2f && cool && since >= 2f && loco.throttle < 1f) { loco.throttle = Mathf.Min(1f, loco.throttle + 0.1f); since = 0f; }
            float wheel_rpm = speed / (2f * (float) Math.PI * loco.wheel_radius) * 60f;
            float force = loco.tick(tick_s, wheel_rpm) / loco.wheel_radius;
            float grip = loco.mass_kg * 9.81f * adhesion;
            if (force > grip) { force = grip; slip_run += tick_s; grip_run = 0f; } else { slip_run = 0f; grip_run += tick_s; }
            float v_km_h = speed * 3.6f;
            float resistance = mass * 9.81f * (0.001f * (1.5f + 0.00025f * v_km_h * v_km_h) + grade);
            speed = Math.Max(0f, speed + (force - (speed > 0f || force > resistance ? resistance : force)) / mass * tick_s);
            hottest = Math.Max(hottest, loco.temperature_c);
            peak_kN = Math.Max(peak_kN, force * 0.001f);
            if (loco.failed != null) { how = $"{loco.failed} at {speed * 3.6f:F0} km/h"; return false; }
            if (slip_run > 5f) { how = $"slipping at {speed * 3.6f:F0} km/h"; return false; }
            if (speed * 3.6f >= 30f) { how = $"30 km/h in {time:F0} s, peak {peak_kN:F0} kN, hottest {hottest:F0} C"; return true; }
        }
        how = $"stalled at {speed * 3.6f:F0} km/h, {hottest:F0} C";
        return false;
    }

    public static void ratings(Func<stock_loco> make)
    {
        string name = make().name;
        Console.WriteLine($"  {name}");
        foreach ((string label, float grade, float adhesion) in new[] { ("0 %", 0.0f, 0.30f), ("2 %", 0.020f, 0.30f), ("2 % wet", 0.020f, 0.25f) })
        {
            float ok = 0f, bad = 12000f;
            string ok_how = "", bad_why = "";
            while (bad - ok > 25f)
            {
                float tonnes = 0.5f * (ok + bad);
                if (run(make, tonnes, grade, adhesion, out string how)) { ok = tonnes; ok_how = how; }
                else { bad = tonnes; bad_why = how; }
            }
            Console.WriteLine($"    {label,-8} {ok,6:F0} t   ({ok_how}); {bad:F0} t: {bad_why}");
        }
    }

    // tractive effort vs speed, flat out, speed forced
    public static void effort_table(Func<stock_loco> make)
    {
        Console.Write($"  {make().name,-5}");
        foreach (float km_h in new[] { 0f, 10f, 20f, 30f, 40f, 60f, 80f, 100f, 120f })
        {
            stock_loco loco = make();                          // fresh loco per speed: idle warm-up, 8 s flat out
            float wheel_rpm = km_h / 3.6f / (2f * (float) Math.PI * loco.wheel_radius) * 60f, force = 0f, best = 0f;
            loco.throttle = 0f;
            for (float t = 0f; t < 20f; t += tick_s) loco.tick(tick_s, wheel_rpm);
            loco.throttle = 1f;
            for (float t = 0f; t < 8f; t += tick_s)
            {
                force = loco.tick(tick_s, wheel_rpm) / loco.wheel_radius;
                best = Math.Max(best, force);
            }
            Console.Write($" {best * 0.001f,6:F0}");
        }
        Console.WriteLine();
    }

    public static void run_all()
    {
        Console.WriteLine();
        Console.WriteLine("the game's own locomotives, ported (stock_locos.cs), the same test as the WE-4U's load ratings:");
        ratings(() => new stock_de6());
        ratings(() => new stock_dh4());
        Console.WriteLine("  tractive effort, kN, flat out, held at (km/h):  0     10     20     30     40     60     80    100    120");
        effort_table(() => new stock_de6());
        effort_table(() => new stock_dh4());
    }
}
