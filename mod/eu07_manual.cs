// operating manual: booklet on cab A desk, by the battery panel
// clone of the job booklet (PageBook), our pages from manual/
// no InventoryItemSpec: game saves items by prefab name, would load a job booklet
// lives in the cab interior (made/destroyed as the player comes/goes): remade when missing

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using DV.CabControls;
using HarmonyLib;
using UnityEngine;

namespace electric_sim.unit_A;

/// <summary>Marks the manual, for the patches below.</summary>
internal class eu07_manual_mark : MonoBehaviour { }

internal class eu07_manual : MonoBehaviour
{
    // car frame: 1 cm over cab A desk top, left of battery panel (eu07_build.cs)
    private static readonly Vector3 lies_at = new(-1.06f, 1.66f + 0.79f + 0.010f, 6.199f + 0.36f);
    private const string template_name = "JobBooklet";

    private static Texture[]? _pages;
    /// <summary>Job booklet's placing preview, lent during R placement.</summary>
    public static GameObject? preview;
    private static bool _told;
    private TrainCar _car = null!;
    private GameObject? _made;
    private float _next_look;

    /// <summary>On every WE-4U at setup (eu07_driver.attach).</summary>
    public static void attach(TrainCar car)
    {
        if (car.GetComponent<eu07_manual>() == null)
            car.gameObject.AddComponent<eu07_manual>()._car = car;
        Main.log($"EU07 {car.ID}: the manual's keeper on, {(car.GetComponent<eu07_manual>()?.enabled == true ? "enabled" : "NOT enabled")}");
    }

    private void Update()
    {
        if (Time.time < _next_look)
            return;
        _next_look = Time.time + 1.0f;
        // wait for TrainPhysicsLod 0: item colliders off below it, would fall through
        string waiting = _made != null ? "" : _car == null ? "no car" : _car.interior == null ? "no interior" : !(_car.GetComponent<TrainPhysicsLod>()?.CurrentLod <= 0) ? $"items colliders off (physics level {_car.GetComponent<TrainPhysicsLod>()?.CurrentLod})" : "";
        if (waiting != _waiting)
            Main.log($"EU07 {(_car != null ? _car.ID : "?")}: the manual {(waiting.Length > 0 ? "waits: " + waiting : _made != null ? "is aboard" : "is made")}");
        _waiting = waiting;
        if (_made != null || waiting.Length > 0)
            return;
        _made = make(_car!);
    }

    private string _waiting = "-";

    private static GameObject? make(TrainCar car)
    {
        Texture[]? pages = load_pages();
        GameObject? template = Resources.Load<GameObject>(template_name);
        if (pages == null || template == null)
        {
            if (!_told)
                Main.release_log($"EU07: no manual - {(pages == null ? "no pages in manual/" : "the game has no " + template_name)}");
            _told = true;
            return null;
        }
        preview ??= template.GetComponent<InventoryItemSpec>()?.PreviewPrefab;
        // inactive holder: nothing wakes before stripping
        GameObject holder = new("[WE-4U manual holder]");
        holder.SetActive(false);
        GameObject made = Instantiate(template, holder.transform);
        made.name = "WE-4U operating manual";
        List<string> stripped = new(), kept = new();
        foreach (Component part in made.GetComponentsInChildren<Component>(true).Reverse())
        {
            if (part == null)
                continue;
            string type = part.GetType().FullName ?? part.GetType().Name;
            bool job_part = part is InventoryItemSpec || part is RespawnOnDrop || type.Contains("Booklets.Rendered") || type.Contains("RenderTextureSystem")
                         || part.GetType().Name == "JobBooklet";
            if (job_part)
            {
                stripped.Add(part.GetType().Name);
                DestroyImmediate(part);
            }
            else
                kept.Add(part.GetType().Name);
        }
        PageBook book = made.GetComponent<PageBook>();
        if (book == null)
        {
            Main.release_log("EU07: no manual - the job booklet has no PageBook");
            Destroy(holder);
            return null;
        }
        // no snapping: snap points need an InventoryItemSpec
        DV.CabControls.Spec.Item? spec = made.GetComponent<DV.CabControls.Spec.Item>();
        if (spec != null)
            spec.allowedSnapPointTypes = 0;
        book.pageTextures = pages;
        book.generateOnStart = true;
        book.currentPage = 0;
        made.AddComponent<eu07_manual_mark>();
        made.transform.SetPositionAndRotation(car.transform.TransformPoint(lies_at), car.transform.rotation * Quaternion.Euler(0.0f, 180.0f, 0.0f));   // readable from the seat
        made.transform.SetParent(car.interior, true);
        Destroy(holder);
        // register with the car like a dropped item
        ItemBase? held = made.GetComponent<ItemBase>();
        TrainPhysicsLod? lod = car.GetComponent<TrainPhysicsLod>();
        if (held != null && lod != null)
            lod.AddItem(held);
        if (!_told)
            Main.log($"EU07: the manual, {pages.Length} pages, from the game's {template_name}: stripped {string.Join(" ", stripped)}; kept {string.Join(" ", kept.Distinct())}");
        _told = true;
        Main.log($"EU07 {car.ID}: the manual on cab A's desk");
        return made;
    }

    /// <summary>Pages, loaded once: manual/page_* in order.</summary>
    private static Texture[]? load_pages()
    {
        if (_pages != null)
            return _pages;
        string folder = Path.Combine(eu07_mod.folder, "manual");
        if (!Directory.Exists(folder))
            return null;
        string[] files = Directory.GetFiles(folder, "page_*.*").Where(file => file.EndsWith(".jpg") || file.EndsWith(".png")).OrderBy(file => file).ToArray();
        if (files.Length == 0)
            return null;
        _pages = files.Select(file =>
        {
            Texture2D page = new(2, 2, TextureFormat.RGBA32, true) { name = "WE-4U manual " + Path.GetFileNameWithoutExtension(file) };
            page.LoadImage(File.ReadAllBytes(file), true);
            page.wrapMode = TextureWrapMode.Clamp;
            page.anisoLevel = 4;
            return (Texture) page;
        }).ToArray();
        return _pages;
    }
}

/// <summary>No RespawnOnDrop: it reads the missing InventoryItemSpec.</summary>
[HarmonyPatch(typeof(CabItemRigidbody), "ShouldAddRespawnOnDrop")]
internal static class eu07_manual_no_respawn_patch
{
    private static bool Prefix(CabItemRigidbody __instance, ref bool __result)
    {
        if (__instance.GetComponent<eu07_manual_mark>() == null)
            return true;
        __result = false;
        return false;
    }
}

/// <summary>BelongsToPlayer/IsEssential log an error (= error ping) without a spec;
/// manual: never the player's, never essential.</summary>
[HarmonyPatch(typeof(ItemUtils), nameof(ItemUtils.BelongsToPlayer))]
internal static class eu07_manual_not_the_players_patch
{
    private static bool Prefix(ItemBase item, ref bool __result)
    {
        if (item == null || item.InventorySpecs != null || item.GetComponent<eu07_manual_mark>() == null)
            return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(ItemUtils), nameof(ItemUtils.IsEssential))]
internal static class eu07_manual_not_essential_patch
{
    private static bool Prefix(ItemBase item, ref bool __result)
    {
        if (item == null || item.InventorySpecs != null || item.GetComponent<eu07_manual_mark>() == null)
            return true;
        __result = false;
        return false;
    }
}

/// <summary>R placement reads the preview from InventoryItemSpec:
/// one lent for the placement only, so never pocketed or saved.</summary>
internal static class eu07_manual_placing
{
    public static ItemBase? placing(ItemPlacerNonVr placer) => Traverse.Create(placer).Field("itemToPlace").GetValue() as ItemBase;

    public static void lend(ItemBase? item)
    {
        if (item == null || item.GetComponent<eu07_manual_mark>() == null || item.GetComponent<InventoryItemSpec>() != null || eu07_manual.preview == null)
            return;
        item.gameObject.AddComponent<InventoryItemSpec>().PreviewPrefab = eu07_manual.preview;
    }

    public static void take_back(ItemBase? item)
    {
        if (item != null && item.GetComponent<eu07_manual_mark>() != null && item.TryGetComponent(out InventoryItemSpec lent))
            UnityEngine.Object.DestroyImmediate(lent);
    }
}

[HarmonyPatch(typeof(ItemPlacerNonVr), nameof(ItemPlacerNonVr.InitializePlacement))]
internal static class eu07_manual_placing_start_patch
{
    private static void Prefix(ItemPlacerNonVr __instance) => eu07_manual_placing.lend(eu07_manual_placing.placing(__instance));
}

[HarmonyPatch(typeof(ItemPlacerNonVr), nameof(ItemPlacerNonVr.FinalizePlacement))]
internal static class eu07_manual_placing_end_patch
{
    private static void Prefix(ItemPlacerNonVr __instance, out ItemBase? __state) => __state = eu07_manual_placing.placing(__instance);
    private static void Postfix(ItemBase? __state) => eu07_manual_placing.take_back(__state);
}

[HarmonyPatch(typeof(ItemPlacerNonVr), nameof(ItemPlacerNonVr.CancelPlacement))]
internal static class eu07_manual_placing_cancel_patch
{
    private static void Prefix(ItemPlacerNonVr __instance, out ItemBase? __state) => __state = eu07_manual_placing.placing(__instance);
    private static void Postfix(ItemBase? __state) => eu07_manual_placing.take_back(__state);
}
