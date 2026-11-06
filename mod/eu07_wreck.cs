// WE-4U wreck: game's own isExploded state (saved; cleared on rerail/repair)
// wrecked: derail > 15 km/h, or slow derail then roll > 45 deg / slide > 15 km/h
// look: paint + glass swapped for materials on hidden "[EU07 wreck]" (eu07_build.cs)

using System;
using System.Collections.Generic;
using System.Linq;

using HarmonyLib;
using UnityEngine;

namespace electric_sim.unit_A;

internal class eu07_wreck : MonoBehaviour
{
    public const float slow_kmh = 15.0f, roll_degrees = 45.0f;
    private const string keeper_name = "[EU07 wreck]";

    private TrainCar _car = null!;
    private bool _watching;

    /// <summary>On every WE-4U at setup (eu07_driver.attach).</summary>
    public static void attach(TrainCar car)
    {
        if (car.GetComponent<eu07_wreck>() != null)
            return;
        eu07_wreck made = car.gameObject.AddComponent<eu07_wreck>();
        made._car = car;
        car.OnDerailed += made.derailed;
        if (car.isExploded)
            dress(car, true);                                   // wrecked in the save before this existed
    }

    private void OnDestroy()
    {
        if (_car != null)
            _car.OnDerailed -= derailed;
    }

    private void derailed(TrainCar car)
    {
        float kmh = car.GetAbsSpeed() * 3.6f;
        Main.release_log($"EU07 {car.ID}: derailed at {kmh:F0} km/h");
        if (kmh > slow_kmh)
            wreck(car, $"came off at {kmh:F0} km/h");
        else
            _watching = true;                                   // slow: wreck only if it gets worse
    }

    private void Update()
    {
        if (!_watching || _car == null)
            return;
        if (!_car.derailed || _car.isExploded)
        {
            _watching = false;
            return;
        }
        float tilt = Vector3.Angle(_car.transform.up, Vector3.up);
        float kmh = _car.rb != null ? _car.rb.velocity.magnitude * 3.6f : 0.0f;
        if (tilt > roll_degrees)
            wreck(_car, $"rolled to {tilt:F0} degrees");
        else if (kmh > slow_kmh)
            wreck(_car, $"slid off at {kmh:F0} km/h");
    }

    public static void wreck(TrainCar car, string why)
    {
        if (car.isExploded)
            return;
        Main.release_log($"EU07 {car.ID}: WRECKED - {why}");
        TrainCarExplosion.UpdateModelToExploded(car);
    }

    // ---- appearance ----

    private readonly Dictionary<Renderer, Material[]> _delivered = new();
    private readonly Dictionary<MeshFilter, Mesh> _whole_panes = new();

    /// <summary>Swap paint and glass: wrecked or delivered.</summary>
    public static void dress(TrainCar car, bool wrecked)
    {
        Renderer? keeper = car.GetComponentsInChildren<Renderer>(true).FirstOrDefault(found => found.name == keeper_name);
        eu07_wreck? own = car.GetComponent<eu07_wreck>();
        if (keeper == null || own == null)
            return;
        if (!wrecked)
        {
            foreach (KeyValuePair<Renderer, Material[]> was in own._delivered)
                if (was.Key != null)
                    was.Key.sharedMaterials = was.Value;
            own._delivered.Clear();
            foreach (KeyValuePair<MeshFilter, Mesh> was in own._whole_panes)
                if (was.Key != null)
                    was.Key.sharedMesh = was.Value;
            own._whole_panes.Clear();
            Main.release_log($"EU07 {car.ID}: as delivered again");
            return;
        }
        if (own._delivered.Count > 0)
            return;
        Material[] wreck = keeper.sharedMaterials;                  // shell, outside parts, bogies, broken glass
        Material? swap(Material? material)
        {
            if (material == null)
                return null;
            string name = material.name;
            if (name.StartsWith("EU07_paint_eu07_ext_a", StringComparison.Ordinal)) return wreck[0];
            if (name.StartsWith("EU07_paint_eu07_ext_b", StringComparison.Ordinal)) return wreck[1];
            if (name.StartsWith("EU07_paint_eu07_ext_e", StringComparison.Ordinal)) return wreck[2];
            if (name.StartsWith("EU07_body_eu07_glass", StringComparison.Ordinal)) return wreck[3];
            return null;
        }
        int swapped = 0;
        foreach (Renderer shown in car.GetComponentsInChildren<Renderer>(true))
        {
            if (shown == keeper)
                continue;
            Material[] now = shown.sharedMaterials;
            Material?[] then = now.Select(swap).ToArray();
            if (then.All(material => material == null))
                continue;
            own._delivered[shown] = now;
            shown.sharedMaterials = now.Select((material, index) => then[index] ?? material).ToArray();
            ++swapped;
            // broken-glass texture mapped in metres = panes' uv2: mesh copy with uv2 as uv
            if (then.Contains(wreck[3]) && shown.TryGetComponent(out MeshFilter pane) && pane.sharedMesh != null && pane.sharedMesh.isReadable
                && pane.sharedMesh.uv2 != null && pane.sharedMesh.uv2.Length == pane.sharedMesh.vertexCount)
            {
                Mesh broken = UnityEngine.Object.Instantiate(pane.sharedMesh);
                broken.uv = pane.sharedMesh.uv2;
                own._whole_panes[pane] = pane.sharedMesh;
                pane.sharedMesh = broken;
            }
        }
        Main.release_log($"EU07 {car.ID}: wrecked look on {swapped} parts");
    }
}

[HarmonyPatch(typeof(TrainCarExplosion), nameof(TrainCarExplosion.UpdateModelToExploded))]
internal static class eu07_wreck_patch
{
    private static void Postfix(TrainCar trainCar)
    {
        if (trainCar != null)
            eu07_wreck.dress(trainCar, true);
    }
}

[HarmonyPatch(typeof(TrainCarExplosion), nameof(TrainCarExplosion.RevertModelToUnexploded))]
internal static class eu07_unwreck_patch
{
    private static void Postfix(TrainCar trainCar)
    {
        if (trainCar != null)
            eu07_wreck.dress(trainCar, false);
    }
}
