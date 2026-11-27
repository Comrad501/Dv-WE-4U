// THE CAB'S WALLS (2026-11-20, the author: "So basically none of our Models have the audio muffler - check stock locos and
// our WE-4U", then "Go then"). DV's own isolation is its mixer: a CameraTrigger in a cab with DV's
// InternalExternalSnapshotSwitcher(DoorsAndWindows) sets AudioManager.Internality (1 with the camera inside and nothing open,
// else 0) and the mixer blends to its "Cab" snapshot. Read from DV's mixer asset (resources.assets, DerailValleyMixer, with
// UnityPy, 2026-11-20): that snapshot only re-equalises a little [M]: the Engine group about -4 dB round 50 Hz, the Outdoors
// group (Engine's parent) -3 to -4 dB round 1.4 and 5.2 kHz, wheels and joints cut harder, Master -3 dB. Our sources sit
// in those groups already, but play at full strength within 14 to 30 m (no fall-off inside the car), so in the cab they
// sounded as outside. So every outside sound of ours also passes a wall: a low-pass and a cut, eased in as DV's Internality
// rises [I: 900 Hz and -7 dB, to tune by ear], so an open cab door or window (DV's rule) lets the outside back in, as stock.
// The cab's own sounds ("cab") and the horn have no wall. A copy of the WE-43P mod's en43_wall.cs (keep the two in step).

#nullable enable

using DV.Utils;

using UnityEngine;

namespace electric_sim.unit_A;

internal static class eu07_wall
{
    const float cutoff_hz = 900f, inside_db = -7f, ease_per_s = 4f;      // [I]
    static float _inside;
    static int _frame = -1;

    static void tick()
    {
        if (_frame == Time.frameCount)
            return;
        _frame = Time.frameCount;
        AudioManager? manager = SingletonBehaviour<AudioManager>.Instance;
        float want = manager != null ? manager.Internality : 0f;
        _inside = Mathf.MoveTowards(_inside, want, Time.unscaledDeltaTime * ease_per_s);
    }

    /// <summary>0 outside .. 1 inside a closed cab, eased (DV's Internality).</summary>
    public static float inside { get { tick(); return _inside; } }

    /// <summary>The level through the wall, a factor on an outside source's volume.</summary>
    public static float gain => Mathf.Pow(10f, inside_db * inside / 20f);

    /// <summary>The low-pass's cutoff through the wall.</summary>
    public static float cutoff => Mathf.Lerp(22000f, cutoff_hz, Mathf.Sqrt(inside));

    /// <summary>The wall's filter on a source's object (one filter takes every source on it).</summary>
    public static AudioLowPassFilter on(GameObject holder)
    {
        AudioLowPassFilter? f = holder.GetComponent<AudioLowPassFilter>();
        return f != null ? f : holder.AddComponent<AudioLowPassFilter>();
    }
}
