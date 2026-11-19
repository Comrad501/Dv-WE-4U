// VR testers' log (0.3.0, docs/VR_PLAN.md): with VR on, one Player.log line per cab control grabbed, let go or used,
// so a tester's log shows which controls worked even when the report is vague. Nothing is logged outside VR.
using System.Runtime.CompilerServices;
using UnityEngine;

namespace electric_sim;

internal static class eu07_vr_log
{
    // one hook per desk instance (DV builds it again when the player comes back near). The cab's controls are the
    // external interactables, not the interior: the interior is the F4 HUD's empty holder (eu07_hud.cs), no controls
    private static readonly ConditionalWeakTable<GameObject, object> _hooked = new ConditionalWeakTable<GameObject, object>();

    public static void watch(TrainCar car)
    {
        GameObject? desk = car.loadedExternalInteractables;
        if (desk == null || !VRManager.IsVREnabled() || _hooked.TryGetValue(desk, out _))
            return;
        _hooked.Add(desk, new object());
        int count = 0;
        foreach (DV.CabControls.ControlImplBase control in desk.GetComponentsInChildren<DV.CabControls.ControlImplBase>(true))
        {
            string name = control.name, id = car.ID;
            control.Grabbed += _ => Debug.Log($"EU07 VR {id}: grabbed {name}");
            control.Ungrabbed += _ => Debug.Log($"EU07 VR {id}: let go {name}");
            control.Used += () => Debug.Log($"EU07 VR {id}: used {name}");
            ++count;
        }
        Debug.Log($"EU07 VR {car.ID}: logging {count} cab controls");
    }
}
