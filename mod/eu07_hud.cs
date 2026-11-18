// F4 HUD (0.3.0): the fallback for VR testers, and for anyone who drives from the HUD.
// The build gives the loco an empty interior with the game's InteriorControlsManager (eu07_build.cs, make_hud); this
// hands that manager the LIVE cab's own controls, so a HUD click scrolls the real handle a notch, as the mouse
// wheel does: the controller never jumps notches, and the cut-out cab's lock still holds. Switching cabs moves the
// HUD to the other desk. Gauges are the live desk's own. Slots: see make_hud.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;

using DV.HUD;
using DV.Simulation.Ports;

namespace electric_sim;

internal static class eu07_hud
{
    private sealed class binding { public int cab = -2; public GameObject? desk; }
    private static readonly ConditionalWeakTable<InteriorControlsManager, binding> _bound = new ConditionalWeakTable<InteriorControlsManager, binding>();

    // HUD control -> port on the live desk ({0} = A or B); no {0} = a port both desks share: the copy nearest the live wheel
    private static readonly (InteriorControlsManager.ControlType type, string port)[] controls =
    {
        (InteriorControlsManager.ControlType.Throttle,         "throttle{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.Reverser,         "reverser{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.TrainBrake,       "brake{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.IndBrake,         "indBrake{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.Sander,           "sander{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.Horn,             "hornControl{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.TrainBrakeCutout, "brakeCutout{0}.EXT_IN"),
        (InteriorControlsManager.ControlType.DynamicBrake,     "fieldLever{0}.EXT_IN"),        // HUD "dynamic brake" = field weakening (its keys too)
        (InteriorControlsManager.ControlType.StarterControl,   "fastBreakerOn{0}.EXT_IN"),     // HUD "starter"
        (InteriorControlsManager.ControlType.FuelCutoff,       "fastBreakerOff{0}.EXT_IN"),    // HUD "fuel cutoff"
        (InteriorControlsManager.ControlType.StarterFuse,      "relayReset{0}.EXT_IN"),        // HUD "starter fuse"
        (InteriorControlsManager.ControlType.ElectricsFuse,    "batterySwitch.EXT_IN"),        // HUD "electrics"
    };
    // CCL's extra slots (ExtraLocoControlsReader): alerter = vigilance button, pantograph = front pantograph switch
    private const string port_alerter = "vigilance{0}.EXT_IN", port_pantograph = "pantograph2Ask{0}.EXT_IN";
    // HUD gauges -> gauge names on the desk (eu07_build.cs make_gauge); the copy nearest the live wheel
    private static readonly (string field, string gauge)[] gauges =
    {
        ("speed", "Speedometer"), ("amps", "AmmeterWN1"), ("voltage", "VoltmeterWN"), ("tmTemp", "MotorTemperature"),
        ("brakePipe", "BrakePipe"), ("mainReservoir", "MainReservoir"), ("brakeCylinder", "BrakeCylinder"),
    };
    private static Type? _extra_reader;
    private static bool _extra_looked;

    // every frame from eu07_driver.Update; cheap unless the interior or the live cab changed
    public static void step(TrainCar car, int live_cab)
    {
        GameObject? inside = car.loadedInterior, desk = car.loadedExternalInteractables;
        if (inside == null || desk == null)
            return;
        InteriorControlsManager? manager = inside.GetComponent<InteriorControlsManager>();
        if (manager == null)
            return;
        binding bound = _bound.GetOrCreateValue(manager);
        int cab = live_cab == 0 || live_cab == 1 ? live_cab : -1;
        if (bound.cab == cab && bound.desk == desk)
            return;
        bound.cab = cab;
        bound.desk = desk;
        try
        {
            bind(car, manager, desk, cab);
        }
        catch (Exception error)
        {
            Debug.LogError($"EU07 {car.ID}: the F4 HUD could not take the cab's controls: {error}");
        }
    }

    private static void bind(TrainCar car, InteriorControlsManager manager, GameObject desk, int cab)
    {
        // the HUD on this loco now: let go of the old desk first (the HUD unsubscribes by the references it finds)
        HUDInterfacer? hud = DV.Utils.SingletonBehaviour<HUDInterfacer>.Instance;
        bool showing = manager.Initialized && hud != null && hud.controlsManager == manager;
        if (showing)
            manager.UnsubFromHUD();

        string letter = cab == 0 ? "A" : "B";
        InteractablePortFeeder[] feeders = desk.GetComponentsInChildren<InteractablePortFeeder>(true);
        InteractablePortFeeder? wheel = cab < 0 ? null : feeders.FirstOrDefault(one => one.portId == string.Format("throttle{0}.EXT_IN", letter));
        Vector3 at = wheel != null ? wheel.transform.position : desk.transform.position;
        GameObject? find(string port)
        {
            if (cab < 0)
                return null;
            string id = string.Format(port, letter);
            InteractablePortFeeder? nearest = feeders.Where(one => one.portId == id)
                .OrderBy(one => (one.transform.position - at).sqrMagnitude).FirstOrDefault();
            // the HUD wants the object holding the control itself
            DV.CabControls.ControlImplBase? control = nearest == null ? null
                : nearest.GetComponent<DV.CabControls.ControlImplBase>() ?? nearest.GetComponentInParent<DV.CabControls.ControlImplBase>();
            return control != null ? control.gameObject : null;
        }

        int found = 0;
        foreach ((InteriorControlsManager.ControlType type, string port) in controls)
        {
            GameObject? control = find(port);
            if (control != null)
            {
                manager.SetupControl(control, type);
                ++found;
            }
            else
                manager.RemoveControl(type);
        }
        // the fuel-cutoff slot is drawn from the controls reader, not the manager
        GameObject holder = manager.gameObject;
        LocoControlsReader reader = holder.GetComponent<LocoControlsReader>() ?? holder.AddComponent<LocoControlsReader>();
        reader.fuelCutoff = find("fastBreakerOff{0}.EXT_IN");

        // CCL's extras (CCL.Types, no compile-time reference): set the fields, drop CCL's cached copy so it reads them again
        if (!_extra_looked)
        {
            _extra_looked = true;
            _extra_reader = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("CCL.Types.Components.HUD.ExtraLocoControlsReader", false)).FirstOrDefault(type => type != null);
        }
        if (_extra_reader != null)
        {
            Component extra = holder.GetComponent(_extra_reader) ?? holder.AddComponent(_extra_reader);
            _extra_reader.GetField("Alerter")?.SetValue(extra, find(port_alerter));
            _extra_reader.GetField("Pantograph")?.SetValue(extra, find(port_pantograph));
            Component? cached = holder.GetComponent("ExtraInteriorControlsManager");
            if (cached != null)
                UnityEngine.Object.DestroyImmediate(cached);
        }

        // gauges: the manager read its indicator reader once, at Awake; hand it ours
        LocoIndicatorReader indicators = holder.GetComponent<LocoIndicatorReader>() ?? holder.AddComponent<LocoIndicatorReader>();
        Indicator[] all = cab < 0 ? new Indicator[0] : desk.GetComponentsInChildren<Indicator>(true);
        foreach ((string field, string gauge) in gauges)
        {
            Indicator? nearest = all.Where(one => one.name == gauge).OrderBy(one => (one.transform.position - at).sqrMagnitude).FirstOrDefault();
            typeof(LocoIndicatorReader).GetField(field)?.SetValue(indicators, nearest);
        }
        manager.indicatorReader = indicators;

        if (showing)
            manager.SubToHUD();
        Main.release_log($"EU07 {car.ID}: F4 HUD on {(cab < 0 ? "no cab (none live)" : "cab " + letter)}, {found} of {controls.Length} controls{(showing ? ", HUD refreshed" : "")}");
    }
}

// The HUD moves its dynamic-brake slot only through the game's overridable control, and the WE-4U has none: one
// would hand our field lever to a stock diesel's dynamic brake over the MU cable. With no such control but a lever
// registered (eu07_hud: the field lever), scroll the lever instead. Any other loco: the game's own way, unchanged.
[HarmonyLib.HarmonyPatch(typeof(LocoHUDObserver), "DynamicBrakeUpdated")]
internal static class eu07_hud_field_lever
{
    private static bool Prefix(LocoHUDObserver __instance, float value)
    {
        HarmonyLib.Traverse observer = HarmonyLib.Traverse.Create(__instance);
        DV.Simulation.Cars.BaseControlsOverrider? game = observer.Field("baseControls").GetValue<DV.Simulation.Cars.BaseControlsOverrider>();
        InteriorControlsManager? manager = observer.Field("controlsManager").GetValue<InteriorControlsManager>();
        if ((game != null && game.DynamicBrake != null) || manager == null || !manager.TryGetControl(InteriorControlsManager.ControlType.DynamicBrake, out _))
            return true;
        manager.MoveScrollable(InteriorControlsManager.ControlType.DynamicBrake, (int) value);
        return false;
    }
}
