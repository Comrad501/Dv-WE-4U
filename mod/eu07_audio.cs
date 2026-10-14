// EU07 for Derail Valley - the locomotive's own sounds, played by the mod.
//
// A ROUGH FIRST RUN (the author, 2026-10-10: "place that into the game, so i can have a rough
// audio run"). The sounds are ours - synthesised by audio/synth_*.py - and lie as WAV files in
// the mod's folder, audio/, with audio/table.txt saying what each follows
// (tools/audio_table.py). Nothing goes through the vehicle's audio prefab: this class makes
// its own AudioSources on the locomotive and sets their pitch and loudness every frame from
// the simulation, and plays the one-shots when it sees a contactor, the breaker or a relay
// change. That is the quickest way to hear them; whether they later move into a Custom Car
// Loader audio prefab is open (docs/research/research_audio.md).
//
// LOUD, and loudest at speed (the author, the same day: "the whole jist of the 07's are that
// they are loud"). Levels are set by MEASURING, not by ear nobody has here: heard_dB is what
// reaches the listener, the debug command "audio 0" silences ours for comparison, and
// debug/audio_trace.csv records ten lines a second (tools/audio_trace.py reads it).
//
// What it does not do yet: no pantograph "reaches the wire", no Hasler tick (a quiet running
// loop stands in), no low-pass with distance (far off only the gear tone should be left).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEngine;
using UnityEngine.Audio;

namespace electric_sim.unit_A;

internal class eu07_audio
{
    private class loop
    {
        public AudioSource source = null!;
        public string value = "", master = "";
        public float gain, detune = 1.0f;
        public float[] pitch_x = [], pitch_y = [], volume_x = [], volume_y = [];
    }

    private static readonly Dictionary<string, AudioClip> _clips = [];
    // Where on the locomotive (x right, y up from the rail, z toward cab A).
    private static readonly Dictionary<string, Vector3> _spots = new()
    {
        ["motors"]      = new Vector3( 0.00f, 0.7f,  0.00f),
        ["bogie_a"]     = new Vector3( 0.00f, 0.7f,  4.25f),
        ["bogie_b"]     = new Vector3( 0.00f, 0.7f, -4.25f),
        ["fans"]        = new Vector3( 0.00f, 4.1f,  0.00f),
        ["roof"]        = new Vector3( 0.00f, 4.0f,  0.00f),
        ["converter"]   = new Vector3(-0.40f, 2.2f, -2.20f),
        ["converter1"]  = new Vector3(-0.40f, 2.2f, -2.20f),
        ["converter2"]  = new Vector3( 0.40f, 2.2f,  2.20f),
        ["compressor1"] = new Vector3( 0.50f, 2.0f,  3.00f),
        ["compressor2"] = new Vector3(-0.50f, 2.0f, -3.60f),
        ["small_compressor"] = new Vector3( 1.19f, 2.25f,  4.48f),
        ["switchgear"]  = new Vector3( 0.00f, 2.6f,  1.00f),
        ["breaker"]     = new Vector3( 0.95f, 2.6f, -1.90f),
        ["cab"]         = new Vector3( 0.30f, 2.9f,  6.90f),
        ["horn"]        = new Vector3( 0.00f, 4.1f,  6.30f),
    };

    private readonly GameObject _root;
    private readonly Dictionary<string, AudioSource> _shot_sources = [];
    private readonly Dictionary<string, (string place, float gain, AudioClip[] clips)> _shots = [];
    private readonly List<loop> _loops = [];
    private readonly System.Random _dice = new();
    private readonly Dictionary<AudioSource, string> _places = [];
    private AudioMixer? _mixer;
    private AudioMixerGroup[] _groups = [];
    private string? _all;
    private float _mixer_search = 0.0f;

    // what was, to see what changed
    private int   _position = 0, _field = 0;
    private bool  _breaker = false, _relays = false, _line = false, _battery = false;
    // the driver's side of it: the two horns (0-1 each) and the sand's flow (0-1), set before update()
    public float horn_low = 0.0f, horn_high = 0.0f, sand = 0.0f;
    public bool  vigilance_buzzer = false;       // the vigilance device has asked for longer than its lamp goes alone (eu07_driver, watch_vigilance)
    public bool  small_compressor = false;       // the pantographs' small compressor is running
    private float _small = 0.0f;
    public readonly bool[] panto_wanted = new bool[2];            // each pantograph's switch
    private readonly bool[] _panto_wanted = new bool[2];
    private float _horn_low = 0.0f, _horn_high = 0.0f;
    // each pantograph: how far up it was a frame ago, and which way it is going
    private readonly float[] _raised = new float[2];
    private readonly int[]   _going = new int[2];
    private readonly bool[] _compressing = new bool[2];
    private float _converter = 0.0f, _fan = 0.0f;
    // each pantograph: whether its pan was on the wire a frame ago, and how long since it was down
    private readonly bool[]  _on_wire    = new bool[2];
    private readonly float[] _since_down = { 1000.0f, 1000.0f };
    private readonly float[] _compressor = new float[2];
    private bool  _first = true;
    private int   _starts = 0;
    private float _relay_peak = 0.0f;         // the highest the relays saw since the trace's last line

    public int loops => _loops.Count;
    public int events => _shots.Count;
    public int playing => _loops.Count(item => item.source.isPlaying);
    public string group => _mixer == null ? "not_found" : _all ?? "own";

    // For measuring instead of guessing: what reaches the player's ears, everything mixed, in
    // dB under full scale (averaged over a second or so); and a switch that silences all of
    // OURS, so that the same run can be read with and without.
    private readonly float[] _heard = new float[1024];
    private float _mean_square = 0.0f, _peak = 0.0f;
    // the highest single value lately: at 0 dB the output is cutting tops off
    public float peak_dB => 20.0f * Mathf.Log10(Mathf.Max(_peak, 1.0e-5f));
    public float heard_dB => 10.0f * Mathf.Log10(Mathf.Max(_mean_square, 1.0e-10f));
    public bool muted
    {
        get => _muted;
        set
        {
            _muted = value;
            foreach (AudioSource source in _root.GetComponentsInChildren<AudioSource>(true))
                source.mute = value;
        }
    }
    private bool _muted = false;
    // WHAT IS HEARD IN A CAB IS HEARD IN THAT CAB ("i can hear the hassler in another loco's cab", the author,
    // 2026-10-13, of two of these coupled). Every source is made to carry the length of the locomotive with no
    // falling off, so the recorder's tick, the buzzers and the desk's clicks of the NEXT locomotive, 16 m away, were
    // as loud as one's own. The sounds placed in the cab are small sounds behind doors: they are for whoever is
    // aboard THIS locomotive, and silent for everybody else.
    private readonly List<AudioSource> _cab_sources = [];
    private bool _aboard = false;
    private const string in_the_cab = "cab";

    // "The audio still just randomly cuts out for 1 or 2 seconds. but only ours" (the author,
    // 2026-10-10). So a record, ten lines a second while the debug folder exists: what was
    // asked of the loops, how many of them the sound card was NOT playing (culled: the game
    // has 32 voices), and what reached the ears. tools/audio_trace.py reads it.
    private readonly string? _trace;
    private float _trace_wait = 0.0f, _clock = 0.0f;
    private readonly System.Text.StringBuilder _lines = new();
    public int culled => _loops.Count(item => item.source.isPlaying && item.source.isVirtual);

    public eu07_audio(TrainCar car, string folder)
    {
        string debug = Path.Combine(Path.GetDirectoryName(folder.TrimEnd('/', '\\'))!, "debug");
        if (Directory.Exists(debug))
        {
            _trace = Path.Combine(debug, "audio_trace.csv");
            File.WriteAllText(_trace, "seconds,kmh,load,fan,converter,asked_motors,asked_fans,asked_all,playing,culled,culled_names,starts,heard_dB,muted,peak_dB,wire_V,traction_V,relay_A_peak,position,field,meter_A\n");
        }
        _root = new GameObject("eu07_audio");
        _root.transform.SetParent(car.transform, false);
        string table = Path.Combine(folder, "table.txt");
        if (!File.Exists(table))
        {
            Main.release_log("EU07 audio: no " + table + " - the locomotive stays silent");
            return;
        }
        foreach (string line in File.ReadAllLines(table))
        {
            string[] words = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 8 && words[0] == "loop" && clip(folder, words[1]) is AudioClip sound)
            {
                AudioSource source = make_source(words[1], words[2]);
                source.clip = sound;
                source.loop = true;
                source.volume = 0.0f;
                if (words[2] == in_the_cab)
                    _cab_sources.Add(source);
                // no two machines turn quite alike: a few parts in a thousand apart, they beat like the real four
                loop made = new() { source = source, value = words[3], master = words[4], gain = number(words[5]),
                                    detune = 1.0f + ((float) _dice.NextDouble() - 0.5f) * 0.012f };
                if (words[2].StartsWith("motor") || words[2].StartsWith("bogie") || words[2].StartsWith("fan") || words[2].StartsWith("converter"))
                    source.minDistance = 30.0f;          // these carry: a tenth of full strength is still heard 300 m off
                if (words[2] == "horn")
                    source.minDistance = 90.0f;          // and this is made to: a tenth of it a kilometre off
                points(words[6], out made.pitch_x, out made.pitch_y);
                points(words[7], out made.volume_x, out made.volume_y);
                _loops.Add(made);
            }
            else if (words.Length == 5 && words[0] == "shot")
            {
                AudioClip[] sounds = words[4].Split(',').Select(name => clip(folder, name)).OfType<AudioClip>().ToArray();
                if (sounds.Length > 0)
                    _shots[words[1]] = (words[2], number(words[3]), sounds);
            }
        }
        AudioConfiguration card = AudioSettings.GetConfiguration();
        Main.release_log($"EU07 audio: {_loops.Count} loops, {_shots.Count} events, {_clips.Count} clips; the game plays {card.numRealVoices} voices at once ({card.numVirtualVoices} virtual)");
    }

    public void destroy()
    {
        if (_root != null)
            UnityEngine.Object.Destroy(_root);
    }

    private static float number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    private static void points(string text, out float[] x, out float[] y)
    {
        if (text == "-")
        {
            x = [0.0f];
            y = [1.0f];
            return;
        }
        string[][] pairs = text.Split(',').Select(pair => pair.Split(':')).ToArray();
        x = pairs.Select(pair => number(pair[0])).ToArray();
        y = pairs.Select(pair => number(pair[1])).ToArray();
    }

    private static float read(float[] x, float[] y, float at)
    {
        if (at <= x[0])
            return y[0];
        for (int index = 1; index < x.Length; ++index)
        {
            if (at <= x[index])
                return y[index - 1] + (y[index] - y[index - 1]) * (at - x[index - 1]) / (x[index] - x[index - 1]);
        }
        return y[y.Length - 1];
    }

    private AudioSource make_source(string name, string place)
    {
        GameObject holder = new(name);
        holder.transform.SetParent(_root.transform, false);
        holder.transform.localPosition = _spots.TryGetValue(place, out Vector3 at) ? at : Vector3.up * 2.0f;
        AudioSource source = holder.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1.0f;
        source.dopplerLevel = 0.0f;
        source.minDistance = 14.0f;       // the whole locomotive is "near": no falling off between its ends
        source.maxDistance = 3000.0f;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        // The sound card plays only so many voices at once and the game fills most of them; a
        // source of ordinary rank (128) is then dropped and picked up again as it pleases -
        // "our audio keeps on cutting out" (the author, 2026-10-10). The locomotive's own
        // machines go before nearly everything.
        source.priority = 24;
        _places[source] = place;
        source.outputAudioMixerGroup = channel(place);
        return source;
    }

    // 16-bit mono PCM, as audio/synth_*.py write it.
    private static AudioClip? clip(string folder, string name)
    {
        if (_clips.TryGetValue(name, out AudioClip known))
            return known;
        string path = Path.Combine(folder, name + ".wav");
        if (!File.Exists(path))
            return null;
        byte[] bytes = File.ReadAllBytes(path);
        int at = 12, rate = 44100, channels = 1, data_at = -1, data_bytes = 0;
        while (at + 8 <= bytes.Length)
        {
            string tag = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BitConverter.ToInt32(bytes, at + 4);
            if (tag == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, at + 10);
                rate     = BitConverter.ToInt32(bytes, at + 12);
            }
            else if (tag == "data")
            {
                data_at    = at + 8;
                data_bytes = Math.Min(size, bytes.Length - data_at);
                break;
            }
            at += 8 + size + (size & 1);
        }
        if (data_at < 0)
            return null;
        float[] samples = new float[data_bytes / 2];
        for (int index = 0; index < samples.Length; ++index)
            samples[index] = BitConverter.ToInt16(bytes, data_at + index * 2) / 32768.0f;
        // The motor, fan and converter loops were made with a great deal of headroom (level 13 to
        // 17 dB under their peaks) and were hardly heard in the game. Pressed hard against a
        // soft ceiling they were loud - and "everything seems like its getting bit crushed"
        // (the author, 2026-10-10): the ceiling itself, and above all the sum of them riding at
        // full scale, where the output simply cuts the tops off. So: each brought up CLEAN
        // until its own peak is at full scale, the motors and fans then leaned on only gently
        // (their rare tops rounded, about 2 dB gained), the converter not at all.
        if (name.StartsWith("motor_") || name.StartsWith("fan_") || name == "converter_run")
        {
            float top = 1.0e-6f;
            foreach (float sample in samples)
                top = Math.Max(top, Math.Abs(sample));
            float drive = name == "converter_run" ? 0.0f : 1.5f;
            float ceiling = drive > 0.0f ? (float) Math.Tanh(drive) : 1.0f;
            for (int index = 0; index < samples.Length; ++index)
            {
                float sample = samples[index] / top;
                samples[index] = 0.9f * (drive > 0.0f ? (float) Math.Tanh(drive * sample) / ceiling : sample);
            }
        }
        AudioClip made = AudioClip.Create(name, samples.Length / channels, channels, rate, false);
        made.SetData(samples, 0);
        _clips[name] = made;
        return made;
    }

    private void play(string what, float loudness = 1.0f, string? where = null, float pitch = 1.0f)
    {
        if (!_shots.TryGetValue(what, out (string place, float gain, AudioClip[] clips) shot))
            return;
        string place = where ?? shot.place;
        // (a source has one pitch for everything it is playing: what is to sound higher or lower has a source of its own)
        string from = pitch == 1.0f ? place : place + "@" + pitch.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        if (!_shot_sources.TryGetValue(from, out AudioSource source))
        {
            _shot_sources[from] = source = make_source("shots_" + from, place);
            source.pitch = pitch;
            source.mute = _muted;
            if (place == in_the_cab)
            {
                _cab_sources.Add(source);
                source.mute = _muted || !_aboard;
            }
        }
        source.PlayOneShot(shot.clips[_dice.Next(shot.clips.Length)], Mathf.Clamp01(shot.gain * loudness));
    }

    // One of the table's sounds, asked for by the driver's side (eu07_driver): at its own place, or at another -
    // the vigilance device's relay is the switchgear's relay's click, heard in the cab where that device is.
    public void sound(string what, float loudness = 1.0f, string? where = null, float pitch = 1.0f) => play(what, loudness, where, pitch);

    // The game's mixer, so that its volume slider, tunnels and the cab act on these sounds as
    // on the game's own - and WHICH of its channels matters. Measured in cab A with the meter
    // below (2026-10-10, one loop at full strength): through "Engine" -18 dB, the same as
    // standing outside; through "Compressor" -20; through "Wheels" or "Outdoors" -25 to -26,
    // which is where everything of ours went at first and why the machines were faint from
    // the driver's seat. So: what makes an EU07 an EU07 - gears, motors, resistor fans, the
    // contactors' clatter - goes through Engine and fills the cab; the converter stays behind
    // the wall ("the converter can be abit on the less side", the author).
    private static readonly Dictionary<string, string> _channels = new()
    {
        ["motors"] = "Engine", ["bogie_a"] = "Engine", ["bogie_b"] = "Engine", ["fans"] = "Engine", ["roof"] = "Engine", ["switchgear"] = "Engine", ["breaker"] = "Engine",
        ["converter"] = "Wheels", ["converter1"] = "Wheels", ["compressor1"] = "Compressor", ["compressor2"] = "Compressor", ["small_compressor"] = "Compressor",
        ["cab"] = "Cab", ["horn"] = "Horn",
    };

    // Finds the mixer through the car's own sounds, silences the borrowed railcar's machines.
    private void borrow_mixer(TrainCar car)
    {
        IEnumerable<AudioSource> theirs = car.GetComponentsInChildren<AudioSource>(true);
        if (car.interior != null)
            theirs = theirs.Concat(car.interior.GetComponentsInChildren<AudioSource>(true));
        foreach (AudioSource source in theirs)
        {
            if (source.transform.IsChildOf(_root.transform))
                continue;
            if (source.name is "MotorSoundFront" or "MotorSoundBack" or "ChopperSound" or "CompressourSound")
                source.mute = true;
            else if (source.outputAudioMixerGroup != null)
                _mixer ??= source.outputAudioMixerGroup.audioMixer;
        }
        if (_mixer == null)
            return;
        _groups = _mixer.FindMatchingGroups(string.Empty);
        use_group(null);
        Main.release_log("EU07 audio: plays through the game's mixer " + _mixer.name + " (" + string.Join(", ", _groups.Select(one => one.name)) + ")");
    }

    // Every source to the channel of its place - or all of them to one, for trying.
    private void use_group(string? all)
    {
        _all = all;
        foreach (KeyValuePair<AudioSource, string> pair in _places)
            pair.Key.outputAudioMixerGroup = channel(pair.Value);
    }

    private AudioMixerGroup? channel(string place)
    {
        string wanted = _all ?? (_channels.TryGetValue(place, out string name) ? name : "Engine");
        return _groups.FirstOrDefault(one => one.name == wanted) ?? (_all == null ? _groups.FirstOrDefault(one => one.name == "Master") : null);
    }

    // For trying the game's channels by name ("none": past the mixer; "own": each to its own again).
    public string try_group(string name)
    {
        if (name != "none" && name != "own" && !_groups.Any(one => one.name == name))
            return "no group " + name;
        use_group(name == "own" ? null : name);
        return "group " + group;
    }

    // on_wire: for each pantograph, whether its pan touches the wire; raised: how far up it is, 0 to 1.
    public void update(eu07_sim.power_circuit power, TrainCar car, float wire_volts, float delta, bool[] on_wire, float[] raised)
    {
        if (_root == null || _loops.Count == 0)
            return;
        _aboard = PlayerManager.Car == car;
        foreach (AudioSource in_cab in _cab_sources)
            if (in_cab != null && in_cab.mute != (_muted || !_aboard))
                in_cab.mute = _muted || !_aboard;
        if (_mixer == null && (_mixer_search -= delta) <= 0.0f)
        {
            _mixer_search = 2.0f;
            borrow_mixer(car);
        }

        AudioListener.GetOutputData(_heard, 0);
        float squares = 0.0f;
        _peak *= Mathf.Exp(-delta / 1.0f);
        foreach (float sample in _heard)
        {
            squares += sample * sample;
            _peak = Mathf.Max(_peak, Mathf.Abs(sample));
        }
        _mean_square += (squares / _heard.Length - _mean_square) * Mathf.Min(1.0f, delta / 1.0f);

        // ---- what the loops follow ---------------------------------------------------------
        float kmh  = Mathf.Abs(car.GetForwardSpeed()) * 3.6f;
        float load = Mathf.Clamp01(Mathf.Max(power.ammeter_12, power.ammeter_34) / eu07_sim.power_circuit.overcurrent_normal);
        // the fans have inertia the simulation does not give them: up in a second or so, down slowly
        _fan += (power.fan_speed - _fan) * Mathf.Min(1.0f, delta / (power.fan_speed > _fan ? 1.2f : 4.0f));
        for (int index = 0; index < 2; ++index)
            _compressor[index] = Mathf.MoveTowards(_compressor[index], power.compressor_running[index] ? 1.0f : 0.0f,
                delta / (power.compressor_running[index] ? 0.5f : 1.0f));
        _small = Mathf.MoveTowards(_small, small_compressor ? 1.0f : 0.0f, delta / (small_compressor ? 0.4f : 0.8f));
        // a horn speaks in a twentieth of a second and dies away in a fifth
        _horn_low  = Mathf.MoveTowards(_horn_low , horn_low , delta / (horn_low  > _horn_low  ? 0.05f : 0.20f));
        _horn_high = Mathf.MoveTowards(_horn_high, horn_high, delta / (horn_high > _horn_high ? 0.05f : 0.20f));
        float asked_all = 0.0f, asked_motors = 0.0f, asked_fans = 0.0f;
        foreach (loop item in _loops)
        {
            float x = item.value switch
            {
                "kmh"         => kmh,
                "fan"         => _fan,
                "converter"   => power.converter_speed,
                "compressor1" => _compressor[0],
                "compressor2" => _compressor[1],
                "small_compressor" => _small,
                "buzzer"      => power.high_start_buzzer || vigilance_buzzer ? 1.0f : 0.0f,
                "moving"      => kmh > 0.5f ? 1.0f : 0.0f,
                "horn_low"    => _horn_low,
                "horn_high"   => _horn_high,
                "sand"        => sand,
                _             => 0.0f,
            };
            float master = item.master switch { "load" => load, "gear" => 0.35f + 0.65f * Mathf.Clamp01(load * 4.0f), _ => 1.0f };
            // a standing locomotive with no current makes no motor sound, whatever the bank says at 0 km/h
            if (item.value == "kmh" && kmh < 0.3f && load < 0.01f)
                master = 0.0f;
            float volume = Mathf.Clamp01(read(item.volume_x, item.volume_y, x) * master * item.gain);
            item.source.pitch = Mathf.Clamp(read(item.pitch_x, item.pitch_y, x) * item.detune, 0.05f, 3.0f);
            item.source.volume = volume;
            asked_all += volume;
            if (_places[item.source] is "motors" or "bogie_a" or "bogie_b")
                asked_motors += volume;
            else if (_places[item.source] == "fans")
                asked_fans += volume;
            if (volume > 0.002f && !item.source.isPlaying)
            {
                ++_starts;
                item.source.time = (float) _dice.NextDouble() * item.source.clip.length * 0.9f;
                item.source.Play();
            }
            else if (volume <= 0.002f && item.source.isPlaying)
                item.source.Stop();
        }

        _clock += delta;
        _relay_peak = Mathf.Max(_relay_peak, power.relay_amps);
        if (_trace != null && (_trace_wait -= delta) <= 0.0f)
        {
            _trace_wait = 0.1f;
            string names = string.Join(" ", _loops.Where(item => item.source.isPlaying && item.source.isVirtual).Select(item => item.source.name));
            _lines.Append(FormattableString.Invariant(
                $"{_clock:F2},{kmh:F1},{load:F2},{_fan:F2},{power.converter_speed:F2},{asked_motors:F2},{asked_fans:F2},{asked_all:F2},{playing},{culled},{names},{_starts},{heard_dB:F1},{(_muted ? 1 : 0)},{peak_dB:F1},{power.line_volts:F0},{power.traction_volts:F0},{_relay_peak:F0},{power.contactors.position},{power.contactors.field},{Mathf.Max(power.ammeter_12, power.ammeter_34):F0}\n"));
            _relay_peak = 0.0f;
            if (_lines.Length > 4000)
            {
                try { File.AppendAllText(_trace, _lines.ToString()); } catch (IOException) { }
                _lines.Clear();
            }
        }

        // ---- what happened since the last frame ---------------------------------------------
        int position = power.contactors.position, field = power.contactors.field;
        bool breaker = power.fast_breaker_closed, line = power.line_contactors_closed;
        bool relays = power.overcurrent_tripped || power.earth_fault_tripped || power.converter_tripped
                   || power.heating_tripped || power.compressor_tripped;
        if (!_first)
        {
            if (breaker != _breaker)
                play(breaker ? "breaker_close" : "breaker_trip");
            if (power.battery_contactor != _battery)                    // the last-mile battery's contactor: one heavy contactor in, out
                play(power.battery_contactor ? "contactor_close_heavy" : "contactor_open_double");
            if (relays != _relays)
                play("relay_click");
            if (_line && !line && _position >= 4)
                play("line_dropout");                                   // everything falls out at once
            else if (position > _position)
            {
                for (int step = _position + 1; step <= position; ++step)
                    play(step == 1 ? "contactor_close_heavy" : step == 28 ? "position28" : step == 29 ? "transition_sp"
                       : step is >= 4 and <= 6 or >= 30 ? "contactor_close_double" : "contactor_close_light");
            }
            else if (position < _position)
            {
                for (int step = _position; step > position; --step)
                    play(step == 29 ? "transition_sp" : step is >= 30 or (>= 4 and <= 6) or 1 ? "contactor_open_double" : "contactor_open");
            }
            if (field > _field)
                play("shunt_contactor");
            else if (field < _field)
                play("contactor_open");
            if (_converter < 0.01f && power.converter_speed >= 0.01f)
                play("converter_start");
            for (int index = 0; index < 2; ++index)
            {
                bool running = power.compressor_running[index];
                if (running && !_compressing[index])
                    play("compressor_start", index == 0 ? 1.0f : 0.7f);
                else if (!running && _compressing[index])
                {
                    play("compressor_stop", index == 0 ? 1.0f : 0.7f);
                    // stopped with its switch still on and no relay out: the pressure switch did it
                    if (index == 0 && power.compressor_switch[0] && !power.compressor_tripped && power.converter_charging)
                        play("compressor_unload");
                }
            }
            // The pan MEETING the wire, not the wire coming alive: it used to go by the volts,
            // and so sounded at the end of every neutral section ("it also fires on voltage
            // gain", the author, 2026-10-11). Now by the pantograph itself, and only for a pan
            // that has come UP from its stops - not for a wire that comes back over a raised
            // one after a gap.
            for (int pan = 0; pan < _on_wire.Length; ++pan)
            {
                if (on_wire[pan] && !_on_wire[pan] && _since_down[pan] < 30.0f)
                    play("panto_contact");
            }
        }
        // The pantographs' valves and their landing ("audio sure", the author, 2026-10-11, of
        // three sounds made long ago and never played). The valve is the SWITCH's: it picks up
        // as the switch goes on and drops as it goes off. They went by the pan's height at
        // first, and the pan's height follows the wire: "im getting random panto drop or pant
        // valve audio" (the same, an hour later). The landing is the pan coming to its stops
        // after it has been well up - once, not at every flutter near the bottom.
        for (int pan = 0; pan < _raised.Length && pan < raised.Length; ++pan)
        {
            if (!_first)
            {
                // each of these is written to the game's log with what the pantograph was doing, so
                // that a sound heard for no reason can be traced afterwards
                if (panto_wanted[pan] != _panto_wanted[pan])
                {
                    play(panto_wanted[pan] ? "panto_valve_up" : "panto_valve_down");
                    Main.release_log($"EU07 audio: pantograph {pan} valve {(panto_wanted[pan] ? "up" : "down")} at {_clock:F1} s, raised {raised[pan]:F3}");
                }
                // ...and only with its switch OFF. With the switch on, the pan is sent to its
                // stops whenever the wire's height reads lower than the pan can go (the catenary
                // mod's pantograph follows whatever height it is given), and each time was a
                // landing heard for no reason: "i still get random panto drop audio".
                if (_going[pan] > 0 && raised[pan] <= 0.02f)
                {
                    if (!panto_wanted[pan])
                        play("panto_drop");
                    Main.release_log($"EU07 audio: pantograph {pan} on its stops at {_clock:F1} s, switch {(panto_wanted[pan] ? "ON - no sound" : "off - landing")}, was at {_raised[pan]:F3}");
                }
            }
            if (raised[pan] >= 0.30f)
                _going[pan] = 1;                        // well up: the next arrival at the stops is a landing
            else if (raised[pan] <= 0.02f)
                _going[pan] = 0;
            _raised[pan] = raised[pan];
            _panto_wanted[pan] = panto_wanted[pan];
        }
        for (int pan = 0; pan < _on_wire.Length; ++pan)
        {
            _since_down[pan] = raised[pan] < 0.05f ? 0.0f : _since_down[pan] + delta;
            _on_wire[pan]    = on_wire[pan];
        }
        _first = false;
        _position = position;
        _field = field;
        _breaker = breaker;
        _battery = power.battery_contactor;
        _relays = relays;
        _line = line;
        _converter = power.converter_speed;
        _compressing[0] = power.compressor_running[0];
        _compressing[1] = power.compressor_running[1];
    }
}
