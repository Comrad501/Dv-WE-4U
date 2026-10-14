// EU07 for Derail Valley - the mod's entry, and the car spawner hook.
//
// Custom Car Loader builds the vehicle from the asset bundle; this gives it its electrics.
// Every car whose livery id begins with "EU07" gets an eu07_driver component.

using System;
using System.Diagnostics;
using System.Reflection;

using HarmonyLib;

using UnityModManagerNet;

using static UnityModManagerNet.UnityModManager;

namespace electric_sim;

// Knsgf's sources log through a class of this name.
public static class Main
{
    internal static ModEntry.ModLogger? logger;

    [Conditional("DEBUG")]
    public static void log(string message) => logger?.Log(message);

    [Conditional("DEBUG")]
    public static void background_log(string message) => Console.WriteLine($"[EU07 background] {message}");

    public static void release_log(string message) => logger?.Log(message);
}

// THE MOD'S OWN KEYS ("can we give it a custom keybing", the author, 2026-10-13, of the vigilance button). The game's
// keys are a fixed list and none of them is a vigilance button, so this one is the mod's: set on its page in the
// mod manager (Ctrl+F10), kept in its Settings.xml. It does what the yellow button on the desk does.
public class eu07_settings : ModSettings, IDrawable
{
    [Draw("Vigilance button (answers the vigilance device)")]
    public KeyBinding vigilance = new KeyBinding { keyCode = UnityEngine.KeyCode.KeypadEnter };

    public override void Save(ModEntry mod) => Save(this, mod);

    public void OnChange()
    {
    }
}

public static class eu07_mod
{
    public const string livery_prefix = "EU07";

    public static string folder { get; private set; } = "";
    public static eu07_settings settings { get; private set; } = new eu07_settings();

    public static bool Load(ModEntry mod)
    {
        Main.logger = mod.Logger;
        folder = mod.Path;
        try
        {
            settings = ModSettings.Load<eu07_settings>(mod);
        }
        catch (Exception problem)
        {
            mod.Logger.Log("EU07: the settings could not be read, and are as new: " + problem.Message);
        }
        mod.OnGUI = shown => settings.Draw(shown);
        mod.OnSaveGUI = shown => settings.Save(shown);
        Harmony? code_injector = null;
        try
        {
            code_injector = new Harmony(mod.Info.Id);
            code_injector.PatchAll(Assembly.GetExecutingAssembly());
            Main.release_log("EU07 electrics loaded");
            // WHAT THE PING IS ("the singular ping at every launch is a CCL error reminder. any idea what it might
            // be?", the author, 2026-10-12). It is the GAME's own: its ErrorSoundLogHandler, which Custom Car
            // Loader switches on, sounds once for every log entry of the kind Error or Exception, whoever
            // wrote it - and the game's log file does not say which entries are of that kind. So, with the
            // debug folder there, each such entry is set down in debug/errors.txt with the time.
            if (unit_A.eu07_debug.wanted)
                UnityEngine.Application.logMessageReceived += heard;
        }
        catch (Exception problem)
        {
            mod.Logger.LogException($"Failed to load {mod.Info.DisplayName}:", problem);
            code_injector?.UnpatchAll(mod.Info.Id);
            return false;
        }
        return true;
    }

    private static void heard(string message, string stack, UnityEngine.LogType kind)
    {
        if (kind != UnityEngine.LogType.Error && kind != UnityEngine.LogType.Exception && kind != UnityEngine.LogType.Assert)
            return;
        try
        {
            string[] lines = (stack ?? "").Split((char) 10);
            string first = string.Join(" | ", lines, 0, Math.Min(3, lines.Length));
            System.IO.File.AppendAllText(System.IO.Path.Combine(folder, "debug", "errors.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind}: {message}{Environment.NewLine}    {first}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}

// PINGS AT EVERY LOAD, a locomotive of ours ("Heard a ping again", the author, 2026-10-12; debug/errors.txt:
// "Unexpected state: Missing save data for 'electricityMeter' component, skipping."). The game calls it an
// error when a save has nothing for a part of the simulation that says it keeps something. Two ways that
// comes about, neither a fault of the save:
//   - the meter is the catenary mod's, had from the railcar the vehicle was built on: it says it has save
//     data and writes none while nobody is billed for the current - a free-roam game;
//   - a control this vehicle has been GIVEN since the save was made (the leader's lamp selector, the same
//     evening: four more pings on the next load).
// So, for a vehicle of ours only: where a save has nothing for such a part, it is handed the part's own
// state as it stands - what it would have had anyway - and for the meter a nought, before the game looks.
[HarmonyPatch(typeof(LocoSim.Implementations.SimulationFlow), nameof(LocoSim.Implementations.SimulationFlow.SetSaveStateData))]
internal static class nothing_saved_for_it
{
    private const string meter = "electricityMeter";
    private static readonly FieldInfo? _whose = AccessTools.Field(typeof(LocoSim.Implementations.SimulationFlow), "simGO");

    public static void Prefix(LocoSim.Implementations.SimulationFlow __instance, Newtonsoft.Json.Linq.JObject savedData)
    {
        try
        {
            if (savedData == null)
                return;
            UnityEngine.GameObject? at = _whose?.GetValue(__instance) as UnityEngine.GameObject;
            TrainCar? car = at != null ? TrainCar.Resolve(at) : null;
            if (car == null || car.carLivery == null || !car.carLivery.id.StartsWith(eu07_mod.livery_prefix))
                return;
            foreach (LocoSim.Implementations.SimComponent part in __instance.OrderedSimComps)
            {
                if (!part.HasSaveData || savedData[part.id] != null)
                    continue;
                Newtonsoft.Json.Linq.JObject? own = part.GetSaveStateData();
                if (own == null && part.id == meter)
                    own = new Newtonsoft.Json.Linq.JObject { ["energyConsumed"] = 0.0 };
                if (own != null)
                    savedData[part.id] = own;
            }
        }
        catch (Exception problem)
        {
            Main.release_log("EU07: what the save lacks: " + problem.Message);
        }
    }
}

// THE GAME'S VIGILANCE BOX STOPS A TRAIN BY ITS HANDLES ("vigilancy now calls for another valve. a Czuwak brake cutoff
// valve. but how do we stop the game from pulling the brakes?", the author, 2026-10-13). Read in the game
// (GadgetATS.Update): while it is stopping a train it lets go of the driver's hand and, five times a second, moves
// the throttle to nothing and the train brake's handle to full. A real one touches no handle: it opens a valve of
// its own that empties the brake pipe - a valve with a cock before it, which is what the author asks for.
// So, for a box on a locomotive of OURS only, its Update is done here instead: the same count, the same warning,
// and nothing more. The stop is the locomotive's own (eu07_driver.watch_vigilance): the pipe emptied through that
// valve unless its cock is shut. On anything else the box is the game's, untouched.
[HarmonyPatch(typeof(DV.Customization.Gadgets.Implementations.GadgetATS), "Update")]
internal static class vigilance_box_on_ours
{
    public static bool Prefix(DV.Customization.Gadgets.Implementations.GadgetATS __instance)
    {
        try
        {
            TrainCar car = __instance.TrainCar;
            if (car == null || car.carLivery == null || !car.carLivery.id.StartsWith(eu07_mod.livery_prefix, StringComparison.Ordinal)
                || car.GetComponent<unit_A.eu07_driver>() == null)
                return true;
            if (!__instance.IsActive)
                __instance.currentTimer = __instance.CurrentRegimeTimerLength;
            else
                __instance.currentTimer -= UnityEngine.Time.deltaTime;
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

[HarmonyPatch(typeof(CarSpawner), "Awake")]
internal static class car_spawn_handler
{
    public static void Postfix(CarSpawner __instance)
    {
        __instance.CarSpawned -= on_car_spawned;
        __instance.CarSpawned += on_car_spawned;
        if (unit_A.eu07_debug.wanted && __instance.gameObject.GetComponent<unit_A.eu07_debug>() == null)
            __instance.gameObject.AddComponent<unit_A.eu07_debug>();
    }

    private static void on_car_spawned(TrainCar vehicle)
    {
        if (vehicle == null || vehicle.carLivery == null)
            return;
        if (!vehicle.carLivery.id.StartsWith(eu07_mod.livery_prefix, StringComparison.Ordinal))
            return;
        if (vehicle.gameObject.GetComponent<unit_A.eu07_driver>() != null)
            return;
        try
        {
            vehicle.gameObject.AddComponent<unit_A.eu07_driver>().attach(vehicle);
        }
        catch (Exception problem)
        {
            Main.logger?.LogException($"EU07 {vehicle.ID}: no electrics:", problem);
        }
    }
}
