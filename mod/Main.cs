// EU07 mod entry + car spawner hook
// CCL builds the vehicle; liveries starting "EU07" get an eu07_driver

using System;
using System.Diagnostics;
using System.Reflection;

using HarmonyLib;

using UnityModManagerNet;

using static UnityModManagerNet.UnityModManager;

namespace electric_sim;

// Knsgf's sources log via a class of this name
public static class Main
{
    internal static ModEntry.ModLogger? logger;

    [Conditional("DEBUG")]
    public static void log(string message) => logger?.Log(message);

    [Conditional("DEBUG")]
    public static void background_log(string message) => Console.WriteLine($"[EU07 background] {message}");

    public static void release_log(string message) => logger?.Log(message);
}

// mod key bindings (UMM page, Ctrl+F10; Settings.xml)
// vigilance key = yellow desk button; the game has no such binding
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
            // ErrorSoundLogHandler (on via CCL) pings on every Error/Exception
            // debug folder: log them to debug/errors.txt
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

// missing save data on a sim component: game logs an error (= ping)
// our cars only: fill from current state (catenary electricityMeter: 0; new controls)
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

// stock vigilance box forces throttle 0 + full brake; ours vents the pipe instead
// our locos only: timer only; stop in eu07_driver.watch_vigilance
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
