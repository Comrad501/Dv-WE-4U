// Skin Manager skins on the desks: it only skins the car body and interior, the desks are in the interactables
// same rule as Skin Manager: a skin texture replaces every material texture of the same name
// read by reflection, so Skin Manager stays optional; originals put back when the skin goes

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using UnityEngine;

namespace electric_sim.unit_A;

internal class eu07_desk_skin : MonoBehaviour
{
    private static readonly string[] texture_slots = { "_MainTex", "_BumpMap", "_MetallicGlossMap", "_EmissionMap", "_OcclusionMap" };

    private TrainCar _car = null!;
    private GameObject? _desk;
    private UnityEngine.Object? _theme;
    private bool _pending;
    private float _next_look;
    // what was swapped, to put back
    private readonly List<(Material material, string slot, Texture original)> _swapped = new();
    // renderers given their own copies: if something sets them back (the desk's paint follows the theme too), again
    private readonly List<(Renderer part, Material[] worn)> _worn = new();

    /// <summary>On every WE-4U at setup (eu07_driver.attach).</summary>
    public static void attach(TrainCar car)
    {
        if (car.GetComponent<eu07_desk_skin>() == null)
            car.gameObject.AddComponent<eu07_desk_skin>()._car = car;
    }

    private void Update()
    {
        if (Time.time < _next_look)
            return;
        _next_look = Time.time + 0.5f;
        GameObject? desk = _car != null ? _car.loadedExternalInteractables : null;
        UnityEngine.Object? theme = _car != null ? (_car.PaintInterior != null ? _car.PaintInterior.CurrentTheme : _car.PaintExterior?.CurrentTheme) : null;
        bool undone = _worn.Any(one => one.part != null && !one.part.sharedMaterials.SequenceEqual(one.worn));
        if (desk == _desk && theme == _theme && !_pending && !undone)
            return;
        if (desk != _desk)
        {
            _swapped.Clear();                                       // a new desk wears its own textures
            _worn.Clear();
        }
        else
            put_back();
        _desk = desk;
        _theme = theme;
        _pending = false;
        if (desk == null || theme == null)
            return;
        IDictionary<string, Texture>? skin = skin_textures(theme, _car!.carLivery.id, out bool loading);
        _pending = loading;
        if (skin == null || skin.Count == 0)
            return;
        int swapped = 0;
        foreach (Renderer part in desk.GetComponentsInChildren<Renderer>(true))
        {
            Material[] shared = part.sharedMaterials;
            if (!shared.Any(material => material != null && texture_slots.Any(slot => material.HasProperty(slot) && material.GetTexture(slot) is Texture texture && skin.ContainsKey(texture.name))))
                continue;
            Material[] own = part.materials;                          // this desk's own copies, not every WE-4U's
            _worn.Add((part, own));
            foreach (Material material in own)
            {
                if (material == null)
                    continue;
                foreach (string slot in texture_slots)
                {
                    if (!material.HasProperty(slot) || !(material.GetTexture(slot) is Texture texture) || !skin.TryGetValue(texture.name, out Texture replacement) || replacement == texture)
                        continue;
                    _swapped.Add((material, slot, texture));
                    material.SetTexture(slot, replacement);
                    ++swapped;
                }
            }
        }
        Main.log($"EU07 {_car.ID}: desk skin {theme.name}: {swapped} textures{(loading ? ", more still loading" : "")}");
    }

    private void put_back()
    {
        foreach ((Material material, string slot, Texture original) in _swapped)
        {
            if (material != null)
                material.SetTexture(slot, original);
        }
        _swapped.Clear();
        _worn.Clear();
    }

    // Skin Manager's CustomPaintTheme._skins[livery].SkinTextures, the loaded ones; null if not a Skin Manager theme
    private static IDictionary<string, Texture>? skin_textures(UnityEngine.Object theme, string livery, out bool loading)
    {
        loading = false;
        if (theme.GetType().FullName != "SkinManagerMod.CustomPaintTheme")
            return null;
        try
        {
            if (!(theme.GetType().GetField("_skins", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(theme) is IDictionary skins) || !skins.Contains(livery))
                return null;
            object skin = skins[livery];
            if (!(skin.GetType().GetField("SkinTextures")?.GetValue(skin) is IEnumerable textures))
                return null;
            Dictionary<string, Texture> found = new();
            foreach (object texture in textures)
            {
                Type type = texture.GetType();
                string? name = type.GetField("Name")?.GetValue(texture) as string;
                if (name == null)
                    continue;
                if (type.GetProperty("LoadingTask")?.GetValue(texture) is System.Threading.Tasks.Task task && !task.IsCompleted)
                {
                    loading = true;                                 // TextureData would wait for it: try again later
                    continue;
                }
                if (type.GetProperty("TextureData")?.GetValue(texture) is Texture data)
                    found[name] = data;
            }
            return found;
        }
        catch (Exception problem)
        {
            Main.release_log("EU07: Skin Manager's skin not read for the desk - " + problem.Message);
            return null;
        }
    }
}
