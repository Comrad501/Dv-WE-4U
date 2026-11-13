// EU07 for Derail Valley: builds and exports the vehicle prefab, no editor window needed.
//   Unity.exe -projectPath <this project> -executeMethod eu07_build.run
//             -eu07out <folder for the mod> -logFile <log> [-eu07desk v1|v2]
// -eu07desk: v1 = Knsgf's desk with clones; v2 (default) = 303E desk from primitives (docs/desk/design_desk.md)
// Not -batchmode: 2019.4 batch mode doesn't see a Hub Personal licence; window opens and closes itself
// Base: Knsgf's WE4PT example railcar (MIT, Assets/_CCL_CARS/WE4PT); his sim kept, motors off the throttle
// Assets/_CCL_CARS/EU07 is regenerated every run: don't edit by hand

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using UnityEditor;
using UnityEngine;

using CCL.Types;
using CCL.Types.Catalog;
using CCL.Types.Catalog.Diagram;
using CCL.Types.Components;
using CCL.Types.Proxies;
using CCL.Types.Proxies.Weather;
using CCL.Types.Proxies.Controllers;
using CCL.Types.Proxies.Controls;
using CCL.Types.Proxies.Customization;
using CCL.Types.Proxies.Headlights;
using CCL.Types.Proxies.Indicators;
using CCL.Types.Proxies.Ports;
using DVLangHelper.Data;

public static class eu07_build
{
    // DV naming: Wire, Electric, 4 axles, Universal
    // id stays EU07: the mod finds the vehicle by it
    private const string display_name = "WE-4U";

    private const string source = "Assets/_CCL_CARS/WE4PT", target = "Assets/_CCL_CARS/EU07";

    // 303E DTR, metres
    private const float over_buffers = 16.315f, pivot_half = 8.550f * 0.5f, body_width = 2.90f, roof_top = 3.90f;
    private const float wheel_radius = 0.625f, mass_kg = 83400.0f;
    // as the railcar: coupler rig 0.53 short of the buffer faces
    private const float rig_z = over_buffers * 0.5f - 0.53f;
    // body figures from model/eu07_blockout.py; cab_front = end of free cab space under the windscreen
    // one cab door each, assistant's side: left (-x) cab A, right cab B
    private const float body_half = 7.354f, body_bottom = 1.285f, floor_top = 1.66f;
    private const float cab_back = 5.20f, cab_front = 7.25f, cab_length = cab_front - cab_back;
    private const float door_z = 5.5375f, door_half = 0.25f, pantograph_z = 3.41f;
    // desk near edge (controller axis 0.22 on, 6.419 from middle); desk top over cab floor
    private const float desk_z = 6.199f, desk_top = 0.79f;
    // desk bulk starts here behind DeskA's origin: near edge 165 mm behind controller axis
    private const float desk_front = 0.055f;
    private const string body_fbx = target + "/EU07_body.fbx", parts_fbx = target + "/EU07_desk_parts.fbx", bogie_fbx = target + "/EU07_bogie.fbx", panto_fbx = target + "/EU07_pantograph.fbx";
    private const float axle_half = 3.050f * 0.5f;    // bogie wheelbase, halved

    // desk labels are translation keys; unknown keys read "Regulator", so the pack brings its own
    private static readonly (string term, string text)[] labels =
    {
        ("eu07/fast_breaker"      , "Wyl. szybki"),
        ("eu07/relay_reset"       , "Odblok. przek."),
        ("eu07/line_contactors_off", "Styczniki lin. wyl."),
        ("eu07/high_start"        , "Wysoki rozruch"),
        ("eu07/field_lever"       , "Bocznikowanie"),
    };

    private static readonly (string id, DVPortType type, DVPortValueType value)[] ports =
    {
        ("TORQUE_OUT"         , DVPortType.OUT         , DVPortValueType.TORQUE),
        ("LINE_CURRENT"       , DVPortType.READONLY_OUT, DVPortValueType.AMPS),
        ("ZERO"               , DVPortType.READONLY_OUT, DVPortValueType.CONTROL),
        ("AMMETER_12"         , DVPortType.READONLY_OUT, DVPortValueType.AMPS),
        ("AMMETER_34"         , DVPortType.READONLY_OUT, DVPortValueType.AMPS),
        ("CONTROLLER_POSITION", DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("FIELD_POSITION"     , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("RESISTOR_FAN_SPEED" , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("CONVERTER_SPEED"    , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("RESISTOR_HEAT"      , DVPortType.READONLY_OUT, DVPortValueType.HEAT_RATE),
        ("MOTOR_HEAT"         , DVPortType.READONLY_OUT, DVPortValueType.HEAT_RATE),
        ("LAMP_LSS"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS10"          , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS5"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS4"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS8"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS7"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS9"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LS6"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LSPO"          , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_LSC"           , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        // speed recorder: clock (hours 0-12, minutes 0-60), six counter drums (0-10 each)
        ("CLOCK_HOURS"        , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("CLOCK_MINUTES"      , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_0"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_1"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_2"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_3"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_4"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
        ("ODOMETER_5"         , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),
    };

    // "v1" or "v2", from -eu07desk
    private static string desk = "v2";
    private static bool desk_v2 => desk == "v2";

    public static void run()
    {
        try
        {
            desk = argument("-eu07desk") ?? "v2";
            if (desk != "v1" && desk != "v2")
                throw new ArgumentException("-eu07desk is v1 or v2, not " + desk);
            Debug.Log("EU07 BUILD: desk " + desk);
            string output = argument("-eu07out") ?? throw new ArgumentException("no -eu07out <folder>");
            CustomCarPack pack = make_assets();
            export(pack, output);
            Debug.Log("EU07 BUILD OK: " + output);
            EditorApplication.Exit(0);
        }
        catch (Exception problem)
        {
            Debug.LogError("EU07 BUILD FAILED: " + problem);
            EditorApplication.Exit(1);
        }
    }

    private static string argument(string name)
    {
        string[] all = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(all, name);
        return at >= 0 && at + 1 < all.Length ? all[at + 1] : null;
    }

    // ---- assets ---------------------------------------------------------------------------
    private static CustomCarPack make_assets()
    {
        AssetDatabase.DeleteAsset(target);
        AssetDatabase.CreateFolder("Assets/_CCL_CARS", "EU07");
        foreach (string name in new[] { "template.prefab", "interactables.prefab", "cartype.asset", "livery.asset", "pack.asset" })
        {
            if (!AssetDatabase.CopyAsset($"{source}/WE4PT_{name}", $"{target}/EU07_{name}"))
                throw new IOException("could not copy WE4PT_" + name);
        }
        // body: block-out model from the repo's model folder
        string model_file = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_blockout.fbx"));
        File.Copy(model_file, Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_body.fbx"), true);
        check_paint();
        AssetDatabase.Refresh();
        AssetDatabase.ImportAsset(body_fbx, ImportAssetOptions.ForceSynchronousImport);
        body_materials.Clear();
        paint_pictures.Clear();
        finished.Clear();
        inside_cans.Clear();
        // desk right-hand group (wheel, pedestal, key, field lever, brake valve): model/eu07_desk_parts.py
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_desk_parts.fbx")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_desk_parts.fbx"), true);
        AssetDatabase.ImportAsset(parts_fbx, ImportAssetOptions.ForceSynchronousImport);
        parts_model = AssetDatabase.LoadAssetAtPath<GameObject>(parts_fbx) ?? throw new IOException("no model at " + parts_fbx);
        // bogie: model/eu07_bogie.py; meshes readable, as CCL wants for bogies
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_bogie.fbx")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_bogie.fbx"), true);
        AssetDatabase.ImportAsset(bogie_fbx, ImportAssetOptions.ForceSynchronousImport);
        ModelImporter bogie_importer = (ModelImporter) AssetImporter.GetAtPath(bogie_fbx) ?? throw new IOException("no model at " + bogie_fbx);
        bogie_importer.isReadable = true;
        bogie_importer.SaveAndReimport();
        // pantograph parts: model/eu07_pantograph.py
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_pantograph.fbx")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_pantograph.fbx"), true);
        AssetDatabase.ImportAsset(panto_fbx, ImportAssetOptions.ForceSynchronousImport);
        part_meshes.Clear();
        bogie_meshes.Clear();
        dial_faces.Clear();
        bezels.Clear();
        dial_disc = null;

        edit_prefab($"{target}/EU07_template.prefab", shape_vehicle);
        if (desk_v2)
        {
            edit_prefab($"{target}/EU07_interactables.prefab", shape_desk_v2);
            edit_prefab($"{target}/EU07_template.prefab", desk_item_colliders);
        }
        else
            edit_prefab($"{target}/EU07_interactables.prefab", shape_desk);
        parts_skinnable();

        CustomCarType    type   = AssetDatabase.LoadAssetAtPath<CustomCarType>($"{target}/EU07_cartype.asset");
        CustomCarVariant livery = AssetDatabase.LoadAssetAtPath<CustomCarVariant>($"{target}/EU07_livery.asset");
        CustomCarPack    pack   = AssetDatabase.LoadAssetAtPath<CustomCarPack>($"{target}/EU07_pack.asset");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{target}/EU07_template.prefab");
        GameObject desk   = AssetDatabase.LoadAssetAtPath<GameObject>($"{target}/EU07_interactables.prefab");

        set(type, o =>
        {
            text(o, "id", "EU07");
            text(o, "carIdPrefix", "-");    // as stock: L-080
            text(o, "localizationKey", "ccl/car/EU07");
            text(o, "NameTranslations.Items.Array.data[0].Value", display_name);
            text(o, "NameTranslationJson", "[{\"Language\":\"English\",\"Value\":\"" + display_name + "\"}]");
            o.FindProperty("mass").floatValue = mass_kg;
            o.FindProperty("wheelRadius").floatValue = wheel_radius;
            o.FindProperty("liveries.Array.data[0]").objectReferenceValue = livery;
        });
        set(livery, o =>
        {
            text(o, "id", "EU07");
            text(o, "localizationKey", "ccl/livery/EU07");
            text(o, "NameTranslations.Items.Array.data[0].Value", display_name);
            text(o, "NameTranslationJson", "[{\"Language\":\"English\",\"Value\":\"" + display_name + "\"}]");
            o.FindProperty("parentType").objectReferenceValue = type;
            o.FindProperty("prefab").objectReferenceValue = prefab;
            o.FindProperty("externalInteractablesPrefab").objectReferenceValue = desk;
            o.FindProperty("UseCustomHosePositions").boolValue = true;    // brake hose where shape_vehicle puts it
            // MU by DV's own system: works with stock diesels; CCL adds MultipleUnitModule and cables
            // WE-4U-only shared controls: mu_shared
            o.FindProperty("HasMUCable").boolValue = true;
        });
        // repair cost per part; sum = catalogue price
        type.damage.bodyPrice                 = 45000.0f;
        type.damage.wheelsPrice               = 40000.0f;
        type.damage.electricalPowertrainPrice = 65000.0f;
        type.damage.mechanicalPowertrainPrice = 0.0f;
        // own licence via DV Custom Licenses (WhistleWiz/dv-custom-licenses, MIT, mod id DVCustomLicenses)
        // CCL adds it to Info.json requirements; export() writes license.json
        type.GeneralLicense = license_id;
        type.OnValidate();
        EditorUtility.SetDirty(type);
        CatalogPage page = catalog_page();
        Sprite icon = catalog_icon();
        set(livery, o =>
        {
            o.FindProperty("icon").objectReferenceValue = icon;
            o.FindProperty("CatalogPage").objectReferenceValue = page;
        });
        // SPAWNS: 1500 V stations only (Knsgf's Catenary-DC scenery, WE6SIM/catenary/scenery/*.json)
        // under the wire: CME A-01, IME A-02; others a short battery run; SM turntable 1 reserved
        livery.LocoSpawnGroups = new[] { SpawnTrack.CoalMineEast, SpawnTrack.IronMineEast, SpawnTrack.FoodFactory1, SpawnTrack.FoodFactory2,
                SpawnTrack.HarbourA1, SpawnTrack.HarbourRoundhouse1, SpawnTrack.HarbourRoundhouse2, SpawnTrack.HarbourRoundhouse5, SpawnTrack.HarbourRoundhouse7,
                SpawnTrack.IronMineWest, SpawnTrack.SteelMillTurntable2, SpawnTrack.Farm, SpawnTrack.OilWellCentral, SpawnTrack.OilWellNorth }
            .Select(track => new LocoSpawnGroup { Track = track, AdditionalLiveries = new string[0] }).ToArray();
        // private OnValidate writes the list into _spawnGroupJson, which the game reads
        typeof(CustomCarVariant).GetMethod("OnValidate", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(livery, null);
        EditorUtility.SetDirty(livery);
        ExtraTranslations words = ScriptableObject.CreateInstance<ExtraTranslations>();
        words.Terms = (desk_v2
            ? labels_v2.Select(label => new ExtraTranslations.TermData { Term = label.term, Data = both(label.english, label.polish) })
            : labels.Select(label => new ExtraTranslations.TermData { Term = label.term, Data = TranslationData.Default(label.text) }))
            .Concat(catalog_terms.Select(term => new ExtraTranslations.TermData { Term = term.term, Data = TranslationData.Default(term.english) }))
            .ToArray();
        words.OnValidate();
        AssetDatabase.CreateAsset(words, $"{target}/EU07_translations.asset");

        ProceduralMaterialDefinitions wear = ScriptableObject.CreateInstance<ProceduralMaterialDefinitions>();
        foreach (string painted_name in painted_names)
            wear.Entries.Add(new ProceduralMaterialDefinitions.GeneratorSet { Type = ProceduralMaterialDefinitions.MaterialType.PaintDetailsOld, Original = body_material(painted_name) });
        List<PaintSubstitutions> cans = new List<PaintSubstitutions>();
        foreach ((string state, string theme, ProceduralMaterialDefinitions.MaterialType kind) in paint_cans)
        {
            PaintSubstitutions can = ScriptableObject.CreateInstance<PaintSubstitutions>();
            can.Paint = theme;
            can.Substitutions = painted_names.Select(painted_name =>
            {
                Material other = state == "worn" ? body_material(painted_name) : painted(painted_name, state);
                if (state != "worn")
                    wear.Entries.Add(new ProceduralMaterialDefinitions.GeneratorSet { Type = kind, Original = other });
                return new PaintSubstitutions.Substitution { Original = body_material(painted_name), Substitute = other };
            }).Concat(inside_cans.Where(one => one.state == state).Select(one => new PaintSubstitutions.Substitution { Original = one.original, Substitute = one.substitute })).ToArray();
            can.OnValidate();
            AssetDatabase.CreateAsset(can, $"{target}/EU07_paint_{theme}.asset");
            cans.Add(can);
        }
        wear.OnValidate();
        AssetDatabase.CreateAsset(wear, $"{target}/EU07_wear.asset");
        set(pack, o =>
        {
            SerializedProperty offered = o.FindProperty("PaintSubstitutions");
            offered.arraySize = cans.Count;
            for (int index = 0; index < cans.Count; ++index)
                offered.GetArrayElementAtIndex(index).objectReferenceValue = cans[index];
            o.FindProperty("ProceduralMaterials").objectReferenceValue = wear;
            o.FindProperty("ExtraTranslations").objectReferenceValue = words;
            text(o, "PackId", "EU07");
            text(o, "PackName", desk_v2 ? display_name + " (EU07, indev)" : display_name + " (EU07, indev, old desk)");
            text(o, "Author", "Comrad_501");
            text(o, "Version", "0.2.2");
            text(o, "Repository", "https://raw.githubusercontent.com/Comrad501/Dv-WE-4U/refs/heads/main/repository.json");
            o.FindProperty("Cars.Array.data[0]").objectReferenceValue = type;
        });
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return pack;
    }

    // CATALOGUE PAGE (CCL builds it from the DE2's); load ratings on DV's scale, between DH4 and DE6
    private static readonly (string term, string english)[] catalog_terms =
    {
        ("eu07/catalog/power",        "1500 V DC Overhead Line, Battery"),
        ("eu07/catalog/transmission", "4x DC Traction Motor, Rheostatic"),
        ("eu07/catalog/cooling",      "Forced Air Cooling, Resistor Fans"),
        ("eu07/catalog/cab",          "Dual-End, Front-Facing"),
        ("eu07/catalog/equipment",    "Gadget Cupboard"),
    };

    private static CatalogPage catalog_page()
    {
        string path = $"{target}/EU07_catalog.asset";
        AssetDatabase.DeleteAsset(path);
        CatalogPage page = ScriptableObject.CreateInstance<CatalogPage>();
        page.HeaderColour    = new Color(0xD6 / 255.0f, 0xB3 / 255.0f, 0x40 / 255.0f);    // line yellow: name unreadable on blue
        page.PageName        = TranslationData.Default(display_name);
        page.ConsistUnits    = "1/1";
        page.HasNickname     = false;
        page.ProductionYears = "1975-1992";
        page.Type  = CCL.Types.Catalog.VehicleType.Locomotive;
        page.Role1 = VehicleRole.HeavyHauling;
        page.Role2 = VehicleRole.None;
        page.ShowLoadRatings = true;
        page.LoadFlat       = new LoadRating { Tonnage = 2000, Rating = TonnageRating.Good };
        page.LoadIncline    = new LoadRating { Tonnage =  800, Rating = TonnageRating.Medium };
        page.LoadInclineWet = new LoadRating { Tonnage =  600, Rating = TonnageRating.Medium };
        page.DiagramLayout  = catalog_diagram();
        page.DiagramExtras  = new VehicleDiagramExtras { Length = 16315, Width = 2903, Height = 4343, IsThinVehicle = false, HasFrontBumper = true, HasRearBumper = true };
        page.TechList.Tech1 = new TechEntry { Icon = TechIcon.ElectricPowerSupplyAndTransmission, Description = "eu07/catalog/power",        Type = "vc/techtype/electric_power" };
        page.TechList.Tech2 = new TechEntry { Icon = TechIcon.ElectricPowerSupplyAndTransmission, Description = "eu07/catalog/transmission", Type = "vc/techtype/electric_trans" };
        page.TechList.Tech3 = new TechEntry { Icon = TechIcon.HeatManagement,           Description = "eu07/catalog/cooling",   Type = "vc/techtype/heat_management" };
        page.TechList.Tech4 = new TechEntry { Icon = TechIcon.CompressedAirBrakeSystem, Description = "vc/techdesc/air_brake/selflap", Type = "vc/techtype/air_brake" };
        page.TechList.Tech5 = new TechEntry { Icon = TechIcon.ClosedCab,                Description = "eu07/catalog/cab",       Type = "vc/techtype/cab_closed" };
        page.TechList.Tech6 = new TechEntry { Icon = TechIcon.ExternalControlInterface, Description = "vc/techdesc/additional_control_interface/dh4", Type = "vc/techtype/additional_control_interface" };
        page.TechList.Tech7 = new TechEntry { Icon = TechIcon.SpecializedEquipment,     Description = "eu07/catalog/equipment", Type = "vc/techtype/specialized_equipment" };
        // in page order; energy 3: wire $10/kWh vs diesels $8-15
        // dynamic braking 1, not "n/a" (n/a blanks the total)
        scores(page.EaseOfOperation, 1, 4, 4, 1, 3, 3, 3, 2, 3, 2);
        scores(page.Maintenance,     3, 3, 3, 4, 3, 4);
        scores(page.Hauling,         4, 5, 3, 2, 5, 4, 2, 1);
        scores(page.Shunting,        3, 2, 3, 1, 4, 4, 2, 3);
        page.OnValidate();
        AssetDatabase.CreateAsset(page, path);
        return page;
    }

    private static void scores(ScoreList list, params int[] values)
    {
        CatalogScore[] all = list.AllScores;
        if (all.Length != values.Length)
            throw new ArgumentException($"{list.GetType().Name} has {all.Length} scores, given {values.Length}");
        for (int i = 0; i < all.Length; i++)
        {
            all[i].ScoreType = ScoreType.Score;
            all[i].Value = values[i];
        }
    }

    // side-view symbols, page pixels from body centre: cabs at ends (rear turned), supply/cooling/brake/control between
    private static GameObject catalog_diagram()
    {
        string path = $"{target}/EU07_catalog_diagram.prefab";
        GameObject root = new GameObject("EU07_catalog_diagram", typeof(RectTransform));
        foreach ((TechIcon icon, float x, bool flip) in new[]
        {
            (TechIcon.ClosedCab, -150.0f, false), (TechIcon.ElectricPowerSupplyAndTransmission, -85.0f, false),
            (TechIcon.HeatManagement, -30.0f, false), (TechIcon.CompressedAirBrakeSystem, 25.0f, false),
            (TechIcon.ExternalControlInterface, 80.0f, false), (TechIcon.ClosedCab, 150.0f, true),
        })
        {
            GameObject made = new GameObject("Icon_" + icon, typeof(RectTransform));
            made.transform.SetParent(root.transform, false);
            TechnologyIcon symbol = made.AddComponent<TechnologyIcon>();
            symbol.Icon = icon;
            symbol.Flip = flip;
            symbol.AutoAlign = false;
            made.transform.localPosition = new Vector3(x, 20.0f, 0.0f);
        }
        foreach (float x in new[] { -105.0f, 105.0f })
        {
            GameObject made = new GameObject("Bogie", typeof(RectTransform));
            made.transform.SetParent(root.transform, false);
            BogieLayout bogie = made.AddComponent<BogieLayout>();
            bogie.Pivots = true;
            bogie.Wheels = new[] { true, true };
            bogie.AutoAlign = false;
            made.transform.localPosition = new Vector3(x, -75.0f, 0.0f);
        }
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
        UnityEngine.Object.DestroyImmediate(root);
        return saved;
    }

    // header icon: model/catalog_icon.png, 227 x 80 (smaller gets stretched)
    private static Sprite catalog_icon()
    {
        string picture = $"{target}/EU07_icon.png";
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/catalog_icon.png")), Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_icon.png"), true);
        AssetDatabase.ImportAsset(picture, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = (TextureImporter) AssetImporter.GetAtPath(picture) ?? throw new IOException("no picture at " + picture);
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Sprite>(picture) ?? throw new IOException("no sprite at " + picture);
    }

    private static TranslationData both(string english, string polish)
    {
        TranslationData data = TranslationData.Default(english);
        data.Items.Add(new TranslationItem(DVLanguage.Polish, polish));
        return data;
    }

    private static void set(UnityEngine.Object asset, Action<SerializedObject> change)
    {
        SerializedObject serialized = new SerializedObject(asset);
        change(serialized);
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(asset);
    }

    private static void text(SerializedObject serialized, string path, string value)
    {
        SerializedProperty property = serialized.FindProperty(path) ?? throw new MissingFieldException(serialized.targetObject.name + " has no " + path);
        property.stringValue = value;
    }

    private static void number(SerializedObject serialized, string path, float value)
    {
        SerializedProperty property = serialized.FindProperty(path) ?? throw new MissingFieldException(serialized.targetObject.name + " has no " + path);
        property.floatValue = value;
    }

    private static void flag(SerializedObject serialized, string path, bool value)
    {
        SerializedProperty property = serialized.FindProperty(path) ?? throw new MissingFieldException(serialized.targetObject.name + " has no " + path);
        property.boolValue = value;
    }

    private static void edit_prefab(string path, Action<GameObject> change)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            change(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static Transform child(Transform parent, string name)
    {
        return parent.Find(name) ?? throw new MissingMemberException($"{parent.name} has no child {name}");
    }

    private static void move_z(Transform item, float z)
    {
        Vector3 position = item.localPosition;
        item.localPosition = new Vector3(position.x, position.y, z);
    }

    // ---- VEHICLE ----
    private static void shape_vehicle(GameObject root)
    {
        root.name = "EU07_template";
        Transform top = root.transform;

        // drop CCL's port plotter (pantograph graphs): dev tool, opens for every spawn
        foreach (SimPortPlotter plotter in root.GetComponentsInChildren<SimPortPlotter>(true))
            UnityEngine.Object.DestroyImmediate(plotter);
        // template's "CableBase model" sockets float beside the bogies: off (MU cable has its own)
        int sockets = 0;
        foreach (Transform stray in root.GetComponentsInChildren<Transform>(true).Where(part => part.name == "CableBase model").ToArray())
        {
            stray.gameObject.SetActive(false);
            ++sockets;
        }
        Debug.Log($"EU07 BUILD: {sockets} stray cable sockets of the template switched off");

        // GAME GADGETS: TrainCarCustomization needs a power fuse (or gadgets can't mount) + standard readings
        // fuse set by the mod only (110 V + control breaker); GadgetATS counts on wheel speed; mounts: the whole cab
        // (desk_item_colliders) and the desk's gadget cupboard (shape_desk_v2)
        TrainCarCustomizationProxy gadget_needs = root.GetComponent<TrainCarCustomizationProxy>();
        if (gadget_needs == null)
            gadget_needs = root.AddComponent<TrainCarCustomizationProxy>();
        gadget_needs.electronicsFuseID = desk_v2 ? fed_gadgets : "fusebox.ELECTRICS_MAIN";
        gadget_needs.Ports = new[]
        {
            new TrainCarCustomizationProxy.STDPortDefinition { port = TrainCarCustomizationProxy.STDSimPort.WheelSpeedKMH, name = "traction.WHEEL_SPEED_KMH_EXT_IN", readOnly = true },
            new TrainCarCustomizationProxy.STDPortDefinition { port = TrainCarCustomizationProxy.STDSimPort.Temperature, name = "tmHeat.TEMPERATURE", readOnly = true },
        };
        gadget_needs.OnValidate();
        EditorUtility.SetDirty(gadget_needs);

        move_z(child(top, "[coupler_rig_front]"),  rig_z);
        move_z(child(top, "[coupler_rig_rear]" ), -rig_z);
        // brake hose + cock at the model's bracket left of hook: 0.446 off middle, 0.964 over rail, 0.20 ahead of headstock
        // rear rig's frame is turned round
        foreach (string rig in new[] { "[coupler_rig_front]", "[coupler_rig_rear]" })
        {
            Transform hose = child(child(child(child(top, rig), "BuffersAndChainRig"), "hoses"), "CouplingHoseRig");
            hose.localPosition = new Vector3(-0.446f, 0.964f - child(top, rig).localPosition.y, body_half + 0.007f + 0.20f - rig_z);
        }
        foreach (Transform stem in child(top, "[BufferStems]"))
            move_z(stem, Mathf.Sign(stem.localPosition.z) * (rig_z - 0.13f));
        move_z(child(top, "BogieF"),  pivot_half);
        move_z(child(top, "BogieR"), -pivot_half);
        // our bogie replaces the stock freight one part for part: wheelset on [axle], frame, brake blocks
        // (where DV looks for glowing pads), contact points under the axles
        GameObject bogie_model = AssetDatabase.LoadAssetAtPath<GameObject>(bogie_fbx) ?? throw new IOException("no model at " + bogie_fbx);
        foreach (string end in new[] { "BogieF", "BogieR" })
        {
            Transform car = child(child(top, end), "bogie_car");
            int axles = 0;
            foreach (Transform axle in car.Cast<Transform>().Where(part => part.name == "[axle]").ToArray())
            {
                axle.localPosition = new Vector3(0.0f, wheel_radius, Mathf.Sign(axle.localPosition.z) * axle_half);
                bogie_part(child(axle, "axle"), bogie_model, "Bogie_wheelset", end);
                ++axles;
            }
            if (axles != 2)
                throw new InvalidDataException($"{end} has {axles} axles, expected 2");
            child(car, "bogie2").localPosition = Vector3.zero;
            bogie_part(child(child(car, "bogie2"), "bogie2_LOD0"), bogie_model, "Bogie_frame", end);
            Transform brakes = child(car, "bogie2brakes");
            brakes.localPosition = Vector3.zero;
            UnityEngine.Object.DestroyImmediate(child(brakes, "Bogie2Brakes").gameObject);
            bogie_part(child(brakes, "Bogie2BrakePads"), bogie_model, "Bogie_shoes", end);
            foreach (Transform point in child(car, "ContactPoints"))
                point.localPosition = new Vector3(point.localPosition.x, point.localPosition.y, Mathf.Sign(point.localPosition.z) * axle_half);
        }
        // scripts that turn wheels by their own radius must use the real one, or wheels seem to slip
        foreach (MonoBehaviour script in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            SerializedObject serialized = new SerializedObject(script);
            SerializedProperty radius = serialized.FindProperty("wheelRadius");
            if (radius != null && radius.propertyType == SerializedPropertyType.Float)
            {
                Debug.Log($"EU07 BUILD: {script.GetType().Name} on {script.name}: wheel radius {radius.floatValue} -> {wheel_radius}");
                radius.floatValue = wheel_radius;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        Transform colliders = child(top, "[colliders]");
        foreach (Transform bogie in child(colliders, "[bogies]"))
        {
            if (Mathf.Abs(bogie.localPosition.z) > 1.0f)
                move_z(bogie, Mathf.Sign(bogie.localPosition.z) * pivot_half);
        }
        // panto rig kept (animated pivots); each cube gets our part; parts drawn in the rig's frames
        GameObject panto_model = AssetDatabase.LoadAssetAtPath<GameObject>(panto_fbx) ?? throw new IOException("no model at " + panto_fbx);
        foreach (string pantograph in new[] { "[Pantograph]", "[Pantograph2]" })
        {
            Transform rig = child(top, pantograph), lower = child(rig, "Pivot1"), upper = child(lower, "Pivot2"), head = child(upper, "HeadPivot");
            bogie_part(rig.Find("Base") ?? child(rig, "Base2"), panto_model, "Panto_base", pantograph);    // second panto's is "Base2"
            bogie_part(child(lower, "LowerArm"), panto_model, "Panto_lower", pantograph);
            bogie_part(child(upper, "UpperArm"), panto_model, "Panto_upper", pantograph);
            bogie_part(child(head, "Head"), panto_model, "Panto_head", pantograph);
            foreach (string horn in new[] { "Head (1)", "Head (2)" })
                UnityEngine.Object.DestroyImmediate(child(head, horn).gameObject);
        }
        foreach (string pantograph in new[] { "[Pantograph]", "[Pantograph2]" })
        {
            Transform item = child(top, pantograph);
            item.localPosition = new Vector3(0.0f, roof_top + 0.19f, Mathf.Sign(item.localPosition.z) * pantograph_z);    // on the model's insulators
        }
        foreach (string plate in new[] { "[car plate anchor1]", "[car plate anchor2]" })
        {
            Transform item = child(top, plate);
            Vector3 at = item.localPosition;
            // in the dark band, as the real number: toward cab A right side, toward cab B left
            item.localPosition = new Vector3(Mathf.Sign(at.x) * (body_width * 0.5f + 0.015f), 2.215f, Mathf.Sign(at.x) * 3.83f);
        }
        child(top, "AOShadow").localScale = new Vector3(body_width + 1.4f, 1.0f, over_buffers);

        // COLLIDERS: [collision] = one body box for vehicles/world
        // [walkable] = player: floor, machine room, cab walls with door gaps, desk, roof, landings + invisible treads
        float room_height = roof_top - floor_top, half_width = body_width * 0.5f;
        List<Vector3> doors = new List<Vector3>();
        foreach (string group in new[] { "[collision]", "[walkable]", "[items]", "[camera dampening]" })
        {
            Transform slab = child(child(colliders, group), "Cube");
            void block(string name, float x, float y, float z, float size_x, float size_y, float size_z)
            {
                Transform made = UnityEngine.Object.Instantiate(slab.gameObject, slab.parent).transform;
                made.name = name;
                made.localPosition = new Vector3(x, y, z);
                made.localScale = new Vector3(size_x, size_y, size_z);
            }
            if (group == "[collision]")
            {
                slab.localPosition = new Vector3(0.0f, (body_bottom + roof_top) * 0.5f, 0.0f);
                slab.localScale = new Vector3(body_width, roof_top - body_bottom, body_half * 2.0f);
                continue;
            }
            slab.localPosition = new Vector3(0.0f, floor_top - 0.15f, 0.0f);
            slab.localScale = new Vector3(body_width - 0.06f, 0.30f, body_half * 2.0f - 0.06f);
            // desks' own [items] boxes: desk_item_colliders, after the desks are built
            if (group != "[walkable]")
                continue;
            float wall_y = floor_top + room_height * 0.5f;
            // MACHINE ROOM: roof, side walls; per end in its own frame (x to cab's right, z toward cab, as eu07_blockout.py)
            // back wall round the door, resistors, HV side (locked), frame, compressor, converter; passage left free
            const float aisle = 0.36f, resistors = 1.55f, door_from = -0.30f, door_to = 0.40f, door_high = 1.85f;
            block("Room_roof", 0.0f, roof_top - 0.15f, 0.0f, body_width, 0.30f, cab_back * 2.0f);
            foreach (float side in new[] { 1.0f, -1.0f })
            {
                // side wall doorway behind the cab on whose right it is: right wall cab A end, left cab B; 4.70-5.19 m, 1.74 high
                const float side_door_from = 4.700f, side_door_to = 5.190f, side_door_high = 1.74f;
                string wall = $"Room_wall_{(side > 0.0f ? "R" : "L")}";
                float wall_x = side * (half_width + 0.01f);
                block(wall, wall_x, wall_y, side * (side_door_from - cab_back) * 0.5f, 0.10f, room_height, cab_back + side_door_from);
                block(wall + "_over_door", wall_x, floor_top + side_door_high + (room_height - side_door_high) * 0.5f, side * (side_door_from + side_door_to) * 0.5f,
                    0.10f, room_height - side_door_high, side_door_to - side_door_from);
                doors.Add(new Vector3(side * (half_width + 0.17f), 0.0f, side * (side_door_from + side_door_to) * 0.5f));
            }
            foreach (float end in new[] { 1.0f, -1.0f })
            {
                string at_end = end > 0.0f ? "RoomA" : "RoomB";
                void fill(string name, float x_from, float x_to, float z_from, float z_to, float high)
                {
                    block($"{at_end}_{name}", end * (x_from + x_to) * 0.5f, floor_top + high * 0.5f, end * (z_from + z_to) * 0.5f, Mathf.Abs(x_to - x_from), high, Mathf.Abs(z_to - z_from));
                }
                fill("wall_left", -half_width, door_from, cab_back - 0.10f, cab_back, room_height);
                fill("wall_right", door_to, half_width, cab_back - 0.10f, cab_back, room_height);
                block($"{at_end}_wall_over_door", end * (door_from + door_to) * 0.5f, floor_top + door_high + (room_height - door_high) * 0.5f, end * (cab_back - 0.05f),
                    door_to - door_from, room_height - door_high, 0.10f);
                fill("resistors_left", -half_width, -aisle, 0.0f, resistors, room_height);
                fill("resistors_right", aisle, half_width, 0.0f, resistors, room_height);
                fill("high_voltage", -half_width, -aisle, resistors, cab_back - 0.10f, room_height);
                // frame incl. reservoir (+ small compressor table at cab A); converter incl. fan casing and cabinet
                fill("frame", 0.45f, half_width, resistors, resistors + 0.34f, room_height);
                fill("compressor", 0.43f, half_width, 1.93f, 2.63f, 1.00f);
                fill("converter", aisle, half_width, 2.63f, end > 0.0f ? 4.60f : 4.29f, room_height);
                // battery rack: inside "frame", at the resistors' end
            }
            foreach (float end in new[] { 1.0f, -1.0f })
            {
                string cab = end > 0.0f ? "CabA" : "CabB";
                block($"{cab}_roof", 0.0f, roof_top - 0.15f, end * (cab_back + body_half) * 0.5f, body_width, 0.30f, body_half - cab_back);
                // WINDOWS ARE HOLES: radio beam / pointer pass through; wall = sill + head + pillars
                // openings as the model: 3 windscreen panes, side 1.07 m driver / 0.71 m other (heights over rail)
                const float screen_sill = 2.657f, screen_head = 3.305f, side_sill = 2.595f, side_head = 3.325f;
                float front_z = end * (cab_front + 0.10f);
                block($"{cab}_front_sill", 0.0f, (floor_top + screen_sill) * 0.5f, front_z, body_width, screen_sill - floor_top, 0.20f);
                block($"{cab}_front_head", 0.0f, (screen_head + roof_top) * 0.5f, front_z, body_width, roof_top - screen_head, 0.20f);
                foreach ((float from, float to) in new[] { (-half_width, -1.286f), (-0.340f, -0.287f), (0.287f, 0.340f), (1.286f, half_width) })
                    block($"{cab}_front_pillar_{from:F2}", (from + to) * 0.5f, (screen_sill + screen_head) * 0.5f, front_z, to - from, screen_head - screen_sill, 0.20f);
                void solid_wall(string name, float x, float from, float to)
                {
                    block(name, x, wall_y, end * (from + to) * 0.5f, 0.10f, room_height, to - from);
                }
                void windowed_wall(string name, float x, float from, float to)
                {
                    block(name + "_sill", x, (floor_top + side_sill) * 0.5f, end * (from + to) * 0.5f, 0.10f, side_sill - floor_top, to - from);
                    block(name + "_head", x, (side_head + roof_top) * 0.5f, end * (from + to) * 0.5f, 0.10f, roof_top - side_head, to - from);
                }
                // desk, both cabs
                block($"{cab}_desk", 0.0f, floor_top + desk_top * 0.5f, end * (desk_z + desk_front + cab_front) * 0.5f, body_width - 0.2f, desk_top, cab_front - desk_z - desk_front);
                foreach (float side in new[] { 1.0f, -1.0f })
                {
                    // cab narrows to the nose: walls straight at the narrowest
                    string tag = cab + (side > 0.0f ? "_R" : "_L");
                    float wall_x = side * 1.36f, ahead = door_z + door_half, behind = door_z - door_half;
                    if (side == end)    // driver's side: no door
                    {
                        solid_wall($"{tag}_wall_behind", wall_x, cab_back, 5.665f);
                        windowed_wall($"{tag}_window", wall_x, 5.665f, 6.765f);
                        solid_wall($"{tag}_wall_ahead", wall_x, 6.765f, cab_front);
                        continue;
                    }
                    solid_wall($"{tag}_wall_ahead", wall_x, ahead, 6.040f);
                    windowed_wall($"{tag}_window", wall_x, 6.040f, 6.795f);
                    solid_wall($"{tag}_wall_nose", wall_x, 6.795f, cab_front);
                    block($"{tag}_wall_behind", wall_x, wall_y, end * (cab_back + behind) * 0.5f, 0.10f, room_height, behind - cab_back);
                    block($"{tag}_over_door", wall_x, floor_top + 2.0f + (room_height - 2.0f) * 0.5f, end * door_z, 0.10f, room_height - 2.0f, door_half * 2.0f);
                    doors.Add(new Vector3(side * (half_width + 0.17f), 0.0f, end * door_z));
                }
            }
        }

        // LADDERS: DV climbs a ladder box; one at each cab door, ground to above cab floor, facing the body
        Transform ladders = new GameObject("[ladders]").transform;
        ladders.SetParent(top, false);
        foreach (Vector3 door in doors)
        {
            GameObject ladder = new GameObject(door.z > 0.0f ? "Ladder_A" : "Ladder_B");
            ladder.transform.SetParent(ladders, false);
            ladder.transform.localPosition = new Vector3(door.x, 1.30f, door.z);
            ladder.transform.localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Sign(door.x), 0.0f, 0.0f), Vector3.up);
            ladder.AddComponent<BoxCollider>().size = new Vector3(0.50f, 2.30f, 0.34f);
            ladder.AddComponent<Ladder>();
        }
        // cab sound trigger per cab: DV damps outside sounds when the camera is inside
        foreach (float end in new[] { 1.0f, -1.0f })
        {
            GameObject room = new GameObject(end > 0.0f ? "[cab A sound]" : "[cab B sound]");
            room.transform.SetParent(top, false);
            room.transform.localPosition = new Vector3(0.0f, floor_top + 1.0f, end * (cab_back + cab_front) * 0.5f);
            BoxCollider inside = room.AddComponent<BoxCollider>();
            inside.isTrigger = true;
            inside.size = new Vector3(body_width - 0.16f, 2.0f, cab_length + 0.10f);
            CameraTriggerProxy watcher = room.AddComponent<CameraTriggerProxy>();
            watcher.box = inside;
            room.AddComponent<InternalExternalSnapshotSwitcherProxy>().trigger = watcher;
        }

        // body: his cubes out, block-out model in, minus what game/prefab bring (bogies, pantos, bus bar, buffers, hooks)
        Transform model = child(top, "Model");
        int layer = model.GetComponentInChildren<MeshRenderer>().gameObject.layer;
        for (int index = model.childCount - 1; index >= 0; --index)
            UnityEngine.Object.DestroyImmediate(model.GetChild(index).gameObject);
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(body_fbx) ?? throw new IOException("no model at " + body_fbx);
        GameObject body = UnityEngine.Object.Instantiate(source);
        body.name = "Body";
        body.transform.SetParent(model, false);
        body.transform.localPosition = Vector3.zero;
        // doors hinge with the desk; wipers on spindles; opening side panes slide with the desk
        string[] not_ours = { "Bogie", "Pantograph_", "Buffer_", "Hook_", "Door_cab_", "Door_side_", "Door_room_", "Wiper_", "Window_cab_opening_" };
        // hung where they stand in the vehicle: frames taken from the doors before removal
        door_frames.Clear();
        foreach (Transform part in body.GetComponentsInChildren<Transform>(true))
        {
            if (part.name.StartsWith("Door_cab_") || part.name.StartsWith("Door_side_") || part.name.StartsWith("Door_room_") || part.name.StartsWith("Wiper_")
                || part.name.StartsWith("Window_cab_opening_"))
                door_frames[part.name] = top.worldToLocalMatrix * part.localToWorldMatrix;
        }
        foreach (Transform part in body.GetComponentsInChildren<Transform>(true).ToArray())
        {
            if (part != null && part != body.transform && not_ours.Any(prefix => part.name.StartsWith(prefix)))
                UnityEngine.Object.DestroyImmediate(part.gameObject);
        }
        foreach (Transform part in body.GetComponentsInChildren<Transform>(true))
            part.gameObject.layer = layer;
        foreach (MeshRenderer renderer in body.GetComponentsInChildren<MeshRenderer>(true))
        {
            renderer.sharedMaterials = renderer.sharedMaterials.Select(material => body_material(material.name)).ToArray();
            // plates and markings 0.5 mm over the paint: no shadows
            if (renderer.sharedMaterials.Any(material => material != null && material.name.StartsWith("EU07_decals")))
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }
        // model must come in right way up, right way round, in metres
        Bounds shell = child(body.transform, "Body_shell").GetComponent<MeshRenderer>().bounds;
        Debug.Log($"EU07 BUILD: body shell centre {shell.center:F3} size {shell.size:F3}");
        foreach (string painted_part in new[] { "Body_shell", "Parts_outside" })
        {
            Mesh painted_mesh = child(body.transform, painted_part).GetComponent<MeshFilter>().sharedMesh;
            Debug.Log($"EU07 BUILD: {painted_part}: {painted_mesh.vertexCount} corners, atlas UVs {painted_mesh.uv.Length}, world UVs {painted_mesh.uv2.Length}, " +
                $"materials {string.Join(" ", child(body.transform, painted_part).GetComponent<MeshRenderer>().sharedMaterials.Select(m => m.name))}");
            if (painted_part == "Body_shell" && child(body.transform, painted_part).GetComponent<MeshRenderer>().sharedMaterials.Length != 2)
                throw new InvalidDataException("the shell came in without its outside and its inside as two materials");
            if (painted_mesh.uv.Length != painted_mesh.vertexCount || painted_mesh.uv2.Length != painted_mesh.vertexCount)
                throw new InvalidDataException(painted_part + " came in without its two sets of UVs");
        }
        if (Mathf.Abs(shell.size.x - body_width) > 0.02f || Mathf.Abs(shell.size.z - body_half * 2.0f) > 0.02f || Mathf.Abs(shell.max.y - roof_top) > 0.02f)
            throw new InvalidDataException($"the body model came in wrong: centre {shell.center:F3} size {shell.size:F3}");

        make_lights(top, body.transform);
        make_wipers(top, model, source, layer);
        // WRECK: mod puts it in DV's TrainCarExplosion state after a hard derailment (mod/eu07_wreck.cs)
        // wreck paint + broken panes kept on a hidden object for the mod; rerail/repair restores
        {
            Material broken = new Material(body_material("eu07_glass")) { name = "EU07_wreck_glass", color = Color.white };
            broken.mainTexture = paint_picture("glass_broken", true, 1024, false, paint_folder);    // tiles, laid in metres
            broken.SetTextureScale("_MainTex", new Vector2(1.2f, 1.2f));
            AssetDatabase.CreateAsset(broken, $"{target}/EU07_wreck_glass.mat");
            Material[] wrecked = { painted("eu07_ext_a", "wrecked"), painted("eu07_ext_b", "wrecked"), painted("eu07_ext_e", "wrecked"), broken };
            GameObject wreck_keeper = shaped(top, "[EU07 wreck]", blocks("wreck_keeper", (Vector3.zero, new Vector3(0.001f, 0.001f, 0.001f))), wrecked[0], Vector3.zero);
            wreck_keeper.GetComponent<MeshRenderer>().sharedMaterials = wrecked;
            wreck_keeper.GetComponent<MeshRenderer>().enabled = false;
            // broken picture uses UV2 (metres); copy it into the panes' UV1 here (meshes not readable in game)
            Material clear = body_material("eu07_glass");
            Mesh pane_holder = null;
            int panes = 0;
            foreach (MeshFilter pane in top.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer shown = pane.GetComponent<MeshRenderer>();
                Mesh whole = pane.sharedMesh;
                if (shown == null || whole == null || !shown.sharedMaterials.All(material => material == clear))
                    continue;
                Vector2[] world = whole.uv2;
                if (world.Length != whole.vertexCount)
                    continue;
                Mesh laid = UnityEngine.Object.Instantiate(whole);
                laid.name = whole.name + "_panes";
                laid.uv = world;
                if (pane_holder == null)
                {
                    pane_holder = laid;
                    AssetDatabase.CreateAsset(laid, $"{target}/EU07_panes.asset");
                }
                else
                    AssetDatabase.AddObjectToAsset(laid, pane_holder);
                pane.sharedMesh = laid;
                ++panes;
            }
            Debug.Log($"EU07 BUILD: the wreck: {panes} pane meshes given the world UVs first");
            // LOUVRE SCREEN: likewise, laid in metres
            Material screen = body_material("eu07_cage_louvre");
            int screens = 0;
            foreach (MeshFilter piece in top.GetComponentsInChildren<MeshFilter>(true))
            {
                MeshRenderer shown = piece.GetComponent<MeshRenderer>();
                Mesh flat = piece.sharedMesh;
                if (shown == null || flat == null || !shown.sharedMaterials.All(material => material == screen) || flat.uv2.Length != flat.vertexCount)
                    continue;
                Mesh laid = UnityEngine.Object.Instantiate(flat);
                laid.name = flat.name + "_metres";
                laid.uv = flat.uv2;
                AssetDatabase.CreateAsset(laid, $"{target}/EU07_louvre_screen_{screens}.asset");
                piece.sharedMesh = laid;
                ++screens;
            }
            Debug.Log($"EU07 BUILD: the louvre's screen: {screens} meshes laid in metres");
            // FANS: traction motor fan impellers on pivots along the body, turned with the converter
            // max 7 rev/s: faster, 8 blades strobe
            List<Transform> spun = new List<Transform>();
            foreach (MeshRenderer blade in top.GetComponentsInChildren<MeshRenderer>(true).Where(one => one.name.StartsWith("Fan_impeller_")).ToArray())
            {
                Transform pivot = node(top, "[fan " + blade.name.Substring("Fan_impeller_".Length) + "]", top.InverseTransformPoint(blade.bounds.center)).transform;
                blade.transform.SetParent(pivot, true);
                spun.Add(pivot);
            }
            if (spun.Count != 2)
                throw new InvalidDataException($"the body brought {spun.Count} fan impellers, expected 2");
            CCL.Types.Proxies.Ports.RotatorPortReaderProxy turning = node(top, "[fans turning]", Vector3.zero).AddComponent<CCL.Types.Proxies.Ports.RotatorPortReaderProxy>();
            turning.portId = "eu07.CONVERTER_SPEED";
            turning.transformsToRotate = spun.Select(pivot => new CCL.Types.Proxies.Ports.RotatorPortReaderProxy.RotationData
                { transformToRotate = pivot, rotationAxis = Vector3.forward, maxRps = 7.0f }).ToArray();
            turning.OnValidate();
            Debug.Log($"EU07 BUILD: the fans: {spun.Count} impellers turning with the converter");
        }
        // what DV paint cans repaint: every painted material (outside doors are in the desk prefab: mod paints them)
        TrainCarPaintProxy repaint = top.GetComponent<TrainCarPaintProxy>() ?? top.gameObject.AddComponent<TrainCarPaintProxy>();
        foreach (MaterialSet old_set in top.GetComponents<MaterialSet>())
            UnityEngine.Object.DestroyImmediate(old_set);
        repaint.CurrentTheme = "DVRT";
        repaint.targetArea = TrainCarPaintProxy.Target.Exterior;
        repaint.Sets.Clear();
        foreach (string painted_name in painted_names)
        {
            Material original = body_material(painted_name);
            MaterialSet places = top.gameObject.AddComponent<MaterialSet>();
            places.OriginalMaterial = original;
            places.Replacements = top.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => Enumerable.Range(0, renderer.sharedMaterials.Length)
                .Where(index => renderer.sharedMaterials[index] == original)
                .Select(index => new MaterialSet.RendererMaterialReplacement { Renderer = renderer, MaterialIndex = index })).ToArray();
            places.OnValidate();
            repaint.Sets.Add(places);
            Debug.Log($"EU07 BUILD: paint cans: {painted_name} in {places.Replacements.Length} place(s): {string.Join(" ", places.Replacements.Select(place => place.Renderer.name))}");
        }
        // inside paint: shell inside + machine room
        TrainCarPaintProxy inside_paint = top.gameObject.AddComponent<TrainCarPaintProxy>();
        inside_paint.CurrentTheme = "DVRT";
        inside_paint.targetArea = TrainCarPaintProxy.Target.Interior;
        foreach (string inside_name in new[] { "eu07_int_a", "eu07_int_d" })
        {
            Material original = body_material(inside_name);
            MaterialSet places = top.gameObject.AddComponent<MaterialSet>();
            places.OriginalMaterial = original;
            places.Replacements = top.GetComponentsInChildren<Renderer>(true).SelectMany(renderer => Enumerable.Range(0, renderer.sharedMaterials.Length)
                .Where(index => renderer.sharedMaterials[index] == original)
                .Select(index => new MaterialSet.RendererMaterialReplacement { Renderer = renderer, MaterialIndex = index })).ToArray();
            places.OnValidate();
            inside_paint.Sets.Add(places);
            Material fresh = original;
            if (inside_name == "eu07_int_a")
            {
                // shell inside, new state: its own new pictures
                fresh = new Material(Shader.Find("Standard")) { color = Color.white };
                Texture2D fresh_mask = paint_picture("ext_c_new_mask", false, 1024);
                fresh.mainTexture = paint_picture("ext_c_new_albedo", true, 2048);
                fresh.SetTexture("_MetallicGlossMap", fresh_mask);
                fresh.EnableKeyword("_METALLICGLOSSMAP");
                fresh.SetFloat("_GlossMapScale", 1.0f);
                fresh.SetTexture("_OcclusionMap", fresh_mask);
                fresh.SetFloat("_OcclusionStrength", 0.6f);
                AssetDatabase.CreateAsset(fresh, $"{target}/EU07_paint_{inside_name}_new.mat");
            }
            inside_cans.Add(("worn", original, original));
            inside_cans.Add(("new", original, fresh));
        }
        // cab walls and ceiling, all paints: indoors
        foreach (Renderer wall_part in top.GetComponentsInChildren<Renderer>(true))
            foreach (Material wall_paint in wall_part.sharedMaterials)
                if (wall_paint != null && (wall_paint.name.Contains("eu07_int") || machine_room.Any(room_colour => wall_paint.name.EndsWith(room_colour))))
                    occlude_indoors(wall_paint);
        foreach (var (_, _, substitute) in inside_cans)
            occlude_indoors(substitute);
        wire_simulation(child(top, "[sim]"));
        if (desk_v2)
        {
            foreach ((string connection, string control, int notches) in mu_shared)
            {
                CCL.Types.Components.MultipleUnit.MultipleUnitGenericControl shared = top.gameObject.AddComponent<CCL.Types.Components.MultipleUnit.MultipleUnitGenericControl>();
                shared.ConnectionId = connection;
                shared.PortId = control + ".EXT_IN";
                shared.ResetOnConnectionChange = false;    // coupling must not switch a converter off
                shared.EnsureNotches = notches > 2;
                shared.Notches = notches;
                EditorUtility.SetDirty(shared);
            }
            // MU STATE OBSERVER: DV's only unit-to-unit signal (temp lamp: steady 90, blinking 105)
            // mod writes the port (eu07_driver.cs, said_celsius); CCL's default observer has none
            CCL.Types.Proxies.MultipleUnitStateObserverProxy says = top.gameObject.AddComponent<CCL.Types.Proxies.MultipleUnitStateObserverProxy>();
            says.temperaturePortId = "eu07.SAID_CELSIUS";
            EditorUtility.SetDirty(says);
        }
    }

    // ---- WIPERS ----
    // 4, one per outer pane, own valve each; rotary wiper: off/slow/fast; no rain (plain glass)
    // model frame x across, y along, z up = vehicle (-x, z, -y): model cab B is vehicle cab A
    private static readonly (string part, string name, string control, Vector3 spindle, Vector3 points)[] wiper_parts =
    {
        ("Wiper_B_L", "WiperLeftA" , "wiperLeftA" , new Vector3( 0.8300f, -7.0185f, 3.3700f), new Vector3( 0.2578f, -0.9395f, 0.2255f)),
        ("Wiper_B_R", "WiperRightA", "wiperRightA", new Vector3(-0.8300f, -7.0185f, 3.3700f), new Vector3(-0.2578f, -0.9395f, 0.2255f)),
        ("Wiper_A_L", "WiperLeftB" , "wiperLeftB" , new Vector3(-0.8300f,  7.0185f, 3.3700f), new Vector3(-0.2578f,  0.9395f, 0.2255f)),
        ("Wiper_A_R", "WiperRightB", "wiperRightB", new Vector3( 0.8300f,  7.0185f, 3.3700f), new Vector3( 0.2578f,  0.9395f, 0.2255f)),
    };
    private static readonly float[] wiper_speeds = { 0.0f, 0.7f, 1.3f };    // sweeps/s: off, slow, fast

    private static void make_wipers(Transform top, Transform model, GameObject source, int layer)
    {
        foreach ((string part, string name, string control, Vector3 spindle, Vector3 points) in wiper_parts)
        {
            if (!door_frames.TryGetValue(part, out Matrix4x4 frame))
                throw new KeyNotFoundException("the body brought no " + part);
            Vector3 at = new Vector3(-spindle.x, spindle.z, -spindle.y), axis = new Vector3(-points.x, points.z, -points.y).normalized;
            Transform shape = child(source.transform, part);
            // spindle: own z along the turning axis
            Transform driver = node(model, name, Vector3.zero).transform;
            driver.position = top.TransformPoint(at);
            driver.rotation = top.rotation * Quaternion.LookRotation(axis, Vector3.up);
            Transform arm = node(driver, "Arm", Vector3.zero).transform;
            // part stays where it stood in the body
            Matrix4x4 own = (top.worldToLocalMatrix * driver.localToWorldMatrix).inverse * frame;
            GameObject shown = node(arm, "Blade", own.GetColumn(3));
            shown.transform.localRotation = own.rotation;
            shown.transform.localScale = own.lossyScale;
            Mesh mesh = shape.GetComponent<MeshFilter>().sharedMesh;
            shown.AddComponent<MeshFilter>().sharedMesh = mesh;
            shown.AddComponent<MeshRenderer>().sharedMaterials = shape.GetComponent<MeshRenderer>().sharedMaterials.Select(material => body_material(material.name)).ToArray();
            foreach (Transform piece in driver.GetComponentsInChildren<Transform>(true))
                piece.gameObject.layer = layer;
            // far end; how near the part comes to the given spindle
            Vector3 far = Vector3.zero;
            float nearest = float.MaxValue;
            foreach (Vector3 point in mesh.vertices)
            {
                Vector3 seen = own.MultiplyPoint3x4(point);
                Vector2 flat = new Vector2(seen.x, seen.y);
                nearest = Mathf.Min(nearest, flat.magnitude);
                if (flat.sqrMagnitude > new Vector2(far.x, far.y).sqrMagnitude)
                    far = seen;
            }
            if (nearest > 0.03f)
                throw new InvalidDataException($"{part} comes no nearer than {nearest:F3} m to the spindle the build has for it: the model and these figures disagree");
            // parked arm slants one side of straight down, swings as far the other; try both, keep end under spindle
            Vector3 down = Quaternion.Inverse(Quaternion.LookRotation(axis, Vector3.up)) * Vector3.down;
            float slant = Vector2.Angle(new Vector2(far.x, far.y), new Vector2(down.x, down.y));
            float sideways(Vector3 point) => Vector3.Dot(point, Vector3.Cross(Vector3.forward, down).normalized);
            float sweep = Mathf.Abs(sideways(Quaternion.Euler(0.0f, 0.0f, 10.0f) * far)) < Mathf.Abs(sideways(far)) ? 2.0f * slant : -2.0f * slant;
            Debug.Log($"EU07 BUILD: {name} spindle {at:F3} axis {axis:F3} reach {new Vector2(far.x, far.y).magnitude:F3} slant {slant:F1} sweep {sweep:F1}");
            if (slant < 20.0f || slant > 60.0f)
                throw new InvalidDataException($"{name} is parked {slant:F1} degrees off straight down: not a wiper's place");

            WiperProxy wiper = arm.gameObject.AddComponent<WiperProxy>();
            wiper.start = node(arm, "Start", far * 0.23f).transform;
            wiper.end = node(arm, "End", far).transform;
            RotaryWiperDriverProxy rotary = driver.gameObject.AddComponent<RotaryWiperDriverProxy>();
            rotary.wiper = wiper;
            rotary.rotationaryTransforms = new[] { arm };
            rotary.maxAngle = sweep;
            WiperControllerProxy controller = driver.gameObject.AddComponent<WiperControllerProxy>();
            controller.speeds = wiper_speeds;
            controller.timeBetweenWipes = wiper_speeds.Select(speed => 0.0f).ToArray();
            controller.wiperDrivers = new WiperDriverProxy[] { rotary };
            // no WipersSimControlInput: DV takes only one per vehicle; mod drives each by name (eu07_driver.cs)
            SimpleWiperAudio sound = driver.gameObject.AddComponent<SimpleWiperAudio>();
            sound.Type = SimpleWiperAudio.WiperType.Pneumatic;
            sound.wiperController = controller;
            sound.driver = rotary;
            sound.pitchMultiplierValues = new[] { 1.0f, 1.0f, 1.25f };
            serialised(rotary, "wiper", "rotationaryTransforms", "maxAngle", "speedCurve");
            serialised(controller, "speeds", "timeBetweenWipes", "wiperDrivers");
            serialised(sound, "Type", "wiperController", "driver", "pitchMultiplierValues");
        }
    }

    // ---- LIGHTS ----
    // DV headlights: one number per end; 8 switches = 256 setups; mod builds it (eu07_driver.cs)
    // left 1, top 2, right 4, red L 8, red R 16, NOT dimmed 32/64/128 (L/T/R, as seen from that cab)
    private static void make_lights(Transform top, Transform body)
    {
        Material white_unlit = body_material("eu07_lamp_glass"), red_unlit = body_material("eu07_red_lens");
        Material white_lit = glowing("lamp_white_lit", new Color(1.00f, 0.95f, 0.80f), 3.0f), red_lit = glowing("lamp_red_lit", new Color(1.00f, 0.05f, 0.03f), 2.5f);
        if (top.GetComponentInChildren<HeadlightsMainControllerProxy>(true) != null || top.GetComponentInChildren<CarLightsOptimizerProxy>(true) != null)
            throw new InvalidDataException("the template has headlights of its own after all: look before adding a second set");
        HeadlightsMainControllerProxy main = top.gameObject.AddComponent<HeadlightsMainControllerProxy>();
        main.headlightControlFrontId = "headlightDecoder.FRONT_HEADLIGHTS_EXT_IN";
        main.headlightControlRearId  = "headlightDecoder.REAR_HEADLIGHTS_EXT_IN";
        main.powerFuseId = desk_v2 ? fed_headlights : "fusebox.ELECTRICS_MAIN";
        HeadlightBeamControllerProxy beams = top.gameObject.AddComponent<HeadlightBeamControllerProxy>();
        beams.headlightsMainController = main;
        CarLightsOptimizerProxy optimizer = top.gameObject.AddComponent<CarLightsOptimizerProxy>();
        optimizer.beamController = beams;
        optimizer.positionCheckTransform = top;
        optimizer.headLightGlassDisableDistance = 200.0f;    // CCL's "recommended closed cab values"
        Transform lights = node(top, "[headlights]", Vector3.zero).transform;
        MeshRenderer[] lenses = body.GetComponentsInChildren<MeshRenderer>(true).Where(one => one.name.StartsWith("Lamp_") || one.name.StartsWith("Headlight_top_")).ToArray();
        if (lenses.Length != 10)
            throw new InvalidDataException($"the body brought {lenses.Length} lamp lenses, expected 10 (six white, four red)");

        foreach (bool front in new[] { true, false })
        {
            float end = front ? 1.0f : -1.0f;
            Transform group = node(lights, front ? "Front" : "Rear", Vector3.zero).transform;
            // lenses by position, not name (body turned half round); side +1 = right lamp seen from this cab
            MeshRenderer lens(string kind, float side) => lenses.Single(one => one.name.StartsWith(kind) && one.bounds.center.z * end > 0.0f
                && (side == 0.0f || one.bounds.center.x * end * side > 0.0f));
            Transform holder(string name, Renderer of)
            {
                Transform made = node(group, name, top.InverseTransformPoint(of.bounds.center) + new Vector3(0.0f, 0.0f, end * 0.02f)).transform;
                made.localRotation = Quaternion.LookRotation(new Vector3(0.0f, 0.0f, end), Vector3.up);
                return made;
            }
            HeadlightsSubControllerStandardProxy lamp(Transform at, string name, Renderer of, bool red, bool bright)
            {
                GameObject made = node(at, name, Vector3.zero);
                HeadlightProxy headlight = made.AddComponent<HeadlightProxy>();
                headlight.headlightRenderer = of;
                headlight.emissionMaterialLit = red ? red_lit : white_lit;
                headlight.emissionMaterialUnlit = red ? red_unlit : white_unlit;
                // glare: flat quad, DV's material, seen from afar
                GameObject glare = GameObject.CreatePrimitive(PrimitiveType.Quad);
                glare.name = "Glare";
                glare.transform.SetParent(made.transform, false);
                glare.transform.localScale = red ? new Vector3(1.5f, 1.5f, 1.0f) : bright ? new Vector3(4.0f, 4.0f, 1.0f) : new Vector3(2.5f, 2.5f, 1.0f);
                UnityEngine.Object.DestroyImmediate(glare.GetComponent<Collider>());
                Renderer shown = glare.GetComponent<Renderer>();
                shown.sharedMaterial = null;
                MaterialGrabberRenderer grabber = glare.AddComponent<MaterialGrabberRenderer>();
                grabber.RenderersToAffect = new[] { shown };
                grabber.Replacements = new[] { new MaterialGrabberRenderer.IndexToName { RendererIndex = 0, ReplacementName = red ? "TaillightsGlare" : "HeadlightsGlare" } };
                grabber.OnValidate();
                headlight.glare = glare;
                List<Light> sources = new List<Light>();
                if (!red)
                {
                    if (bright)
                    {
                        VolumetricLightBeamProxy beam = node(made.transform, "Beam", Vector3.zero).AddComponent<VolumetricLightBeamProxy>();
                        beam.transform.localEulerAngles = new Vector3(1.0f, 0.0f, 0.0f);
                        beam.colorFromLight = false;
                        beam.color = new Color(1.0f, 0.95f, 0.85f);
                        beam.intensityFromLight = false;
                        beam.intensityModeAdvanced = true;
                        beam.intensityInside = 0.42f;    // DV's DE6 / DH4 / DM3 figures (CCL defaults)
                        beam.intensityOutside = 0.03f;
                        beam.spotAngleFromLight = false;
                        beam.spotAngle = 25.0f;
                        beam.glareFrontal = 0.308f;
                        headlight.beamData = new VolumetricBeamControllerBaseProxy.VolumetricBeamData { beam = beam, intensityOutsideMax = 0.04f, intensityInsideMax = 0.1f };
                    }
                    Light source = node(made.transform, "Light", new Vector3(0.0f, 0.0f, 0.05f)).AddComponent<Light>();
                    source.type = LightType.Spot;
                    source.color = new Color(1.0f, 0.93f, 0.80f);
                    source.spotAngle = bright ? 45.0f : 80.0f;
                    source.range = bright ? 220.0f : 45.0f;
                    source.intensity = bright ? 2.2f : 0.9f;
                    source.shadows = LightShadows.None;
                    source.enabled = false;
                    sources.Add(source);
                }
                headlight.OnValidate();
                HeadlightsSubControllerStandardProxy control = made.AddComponent<HeadlightsSubControllerStandardProxy>();
                control.isFront = front;
                // whole end goes dark with that end's MU cable in, as stock
                control.multipleUnityDependent = front ? HeadlightsSubControllerBaseProxy.HeadlightMUDependency.Front : HeadlightsSubControllerBaseProxy.HeadlightMUDependency.Rear;
                control.headlights = new[] { headlight };
                control.lightSources = sources.ToArray();
                return control;
            }
            Renderer top_lens = lens("Headlight_top_", 0.0f), right_lens = lens("Lamp_white_", 1.0f), left_lens = lens("Lamp_white_", -1.0f);
            Transform at_top = holder("Top", top_lens), at_right = holder("Right", right_lens), at_left = holder("Left", left_lens);
            HeadlightsSubControllerStandardProxy[] whites_dim = { lamp(at_left, "White", left_lens, false, false), lamp(at_top, "White", top_lens, false, false), lamp(at_right, "White", right_lens, false, false) },
                whites_bright = { lamp(at_left, "WhiteBright", left_lens, false, true), lamp(at_top, "WhiteBright", top_lens, false, true), lamp(at_right, "WhiteBright", right_lens, false, true) },
                reds = { lamp(holder("LeftRed", lens("Lamp_red_", -1.0f)), "Red", lens("Lamp_red_", -1.0f), true, false),
                        lamp(holder("RightRed", lens("Lamp_red_", 1.0f)), "Red", lens("Lamp_red_", 1.0f), true, false) };
            HeadlightSetup[] setups = Enumerable.Range(0, 256).Select(index =>
            {
                List<HeadlightsSubControllerBaseProxy> burning = new List<HeadlightsSubControllerBaseProxy>();
                for (int which = 0; which < 3; ++which)
                {
                    if ((index & (1 << which)) != 0)
                        burning.Add((index & (32 << which)) != 0 ? whites_bright[which] : whites_dim[which]);
                }
                for (int which = 0; which < 2; ++which)
                {
                    if ((index & (8 << which)) != 0)
                        burning.Add(reds[which]);
                }
                HeadlightSetup setup = node(group, $"Setup{index:000}", Vector3.zero).AddComponent<HeadlightSetup>();
                setup.setting = burning.Count == 0 ? HeadlightSetup.HeadlightSetting.Off : HeadlightSetup.HeadlightSetting.HeadlightSetting02;
                setup.subControllers = burning.ToArray();
                setup.mainOffSetup = index == 0;
                return setup;
            }).ToArray();
            if (front)
                main.headlightSetupsFront = setups;
            else
                main.headlightSetupsRear = setups;
        }
        optimizer.headLightGlass = lenses.Select(one => one.gameObject).ToArray();

        // CAB DOME LAMP, cab-light switch: black ring in the lining (starts 14 mm over crown) + 6 cm opal glass
        // centre line, 0.5 m ahead of back wall
        const float cab_ceiling = 3.8715f;    // lining on centre line over rail, measured in model
        List<Renderer> covers = new List<Renderer>();
        GameObject[] cab_lights = new[] { 1.0f, -1.0f }.Select(end =>
        {
            string name = end > 0.0f ? "CabLightA" : "CabLightB";
            Vector3 at = new Vector3(0.0f, cab_ceiling, end * (cab_back + 0.50f));
            Transform fitting = node(top, name + "Fitting", at).transform;
            fitting.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);    // turned shape points along its -z: down
            shaped(fitting, "Base", turned("cab_dome_base", 32, false, (0.118f, -0.014f), (0.118f, 0.026f), (0.110f, 0.033f), (0.099f, 0.033f), (0.099f, 0.020f)),
                body_material("eu07_black"), Vector3.zero);
            // glass inside ring's bore, 2 mm clear, through its mouth
            GameObject glass = shaped(fitting, "Cover", domed("cab_dome_glass", 32, 9, 0.097f, 0.022f, 0.060f), body_material("eu07_lamp"), Vector3.zero);
            covers.Add(glass.GetComponent<MeshRenderer>());
            // light: shadowed spot down (back wall keeps machine room dark) + shadowless fill forward, nothing behind
            // group = distance (CarLightsOptimizer), lamps inside = switch (CabLightsController); same object breaks it
            GameObject group = node(top, name + "Group", Vector3.zero);
            GameObject made = node(group.transform, name, at + new Vector3(0.0f, -0.105f, 0.0f));
            // switch side needs a Light on its object (ItemLight errors/throws without): dummy light
            Light none = made.AddComponent<Light>();
            none.type = LightType.Point;
            none.intensity = 0.0f;
            none.range = 0.01f;
            none.shadows = LightShadows.None;
            GameObject down = node(made.transform, "LampDown", Vector3.zero);
            down.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Light under = down.AddComponent<Light>();
            under.type = LightType.Spot;
            under.spotAngle = 150.0f;
            under.color = new Color(1.0f, 0.88f, 0.70f);
            under.range = 3.6f;
            under.intensity = 1.30f;
            under.shadows = LightShadows.Hard;
            under.shadowStrength = 1.0f;
            under.shadowBias = 0.02f;
            under.shadowNearPlane = 0.1f;
            GameObject lamp = node(made.transform, "Lamp", Vector3.zero);
            lamp.transform.localRotation = Quaternion.LookRotation(new Vector3(0.0f, -Mathf.Sin(42.0f * Mathf.Deg2Rad), end * Mathf.Cos(42.0f * Mathf.Deg2Rad)));
            Light source = lamp.AddComponent<Light>();
            source.type = LightType.Spot;
            source.spotAngle = 94.0f;    // 47 a side of 48 from upright: nothing behind
            source.color = new Color(1.0f, 0.88f, 0.70f);
            source.range = 4.0f;
            source.intensity = 1.40f;
            source.shadows = LightShadows.None;
            made.SetActive(false);
            return made;
        }).ToArray();
        CabLightsControllerProxy cab = top.gameObject.AddComponent<CabLightsControllerProxy>();
        cab.controlId = "cabLight.EXT_IN";
        cab.powerFuseId = desk_v2 ? fed_lighting : "fusebox.ELECTRICS_MAIN";
        cab.lights = cab_lights;
        cab.lightRenderers = covers.ToArray();
        cab.lightsLit = white_lit;
        cab.lightsUnlit = white_unlit;
        optimizer.cabLights = cab_lights.Select(one => one.transform.parent.gameObject).ToArray();

        // MACHINE ROOM LAMPS: 4 on passage ceiling, EU07RoomLights, switched by the mod (eu07_driver.cs)
        // end lamps cast shadows (stopped by cab walls); inner two stay in the room
        if (desk_v2)
        {
            // at the model's roof lamps (0.9 and 3.4 m from middle); lit disc on the glass
            GameObject room_lamps = node(top, "EU07RoomLights", Vector3.zero);
            foreach (float along in new[] { -3.40f, -0.90f, 0.90f, 3.40f })
            {
                Vector3 glass = new Vector3(0.0f, 3.795f, along);    // foot of the model's glass
                GameObject lit = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                lit.name = "RoomLampLit";
                lit.transform.SetParent(room_lamps.transform, false);
                lit.transform.localPosition = glass + new Vector3(0.0f, -0.003f, 0.0f);
                lit.transform.localScale = new Vector3(0.118f, 0.002f, 0.118f);
                UnityEngine.Object.DestroyImmediate(lit.GetComponent<Collider>());
                lit.GetComponent<MeshRenderer>().sharedMaterial = white_lit;
                bool at_end = Mathf.Abs(along) > 2.0f;
                GameObject lamp = node(room_lamps.transform, "Lamp", glass + new Vector3(0.0f, -0.03f, 0.0f));
                lamp.transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                Light source = lamp.AddComponent<Light>();
                source.type = LightType.Spot;
                source.spotAngle = 140.0f;
                source.color = new Color(1.0f, 0.88f, 0.70f);
                source.range = at_end ? 3.0f : 2.8f;
                source.intensity = 0.60f;
                source.shadows = at_end ? LightShadows.Hard : LightShadows.None;
                source.shadowStrength = 1.0f;
                source.shadowBias = 0.02f;
                source.shadowNearPlane = 0.1f;
            }
            room_lamps.SetActive(false);
        }
    }

    // self-lit material for lit lenses
    private static Material glowing(string name, Color colour, float strength)
    {
        Material made = new Material(Shader.Find("Standard")) { color = colour };
        made.EnableKeyword("_EMISSION");
        made.SetColor("_EmissionColor", colour * strength);
        made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.mat");
        return made;
    }

    // model materials by eu07_blockout.py's names: colour, metallic, smoothness, (panes) alpha
    private static readonly Dictionary<string, Material> body_materials = new Dictionary<string, Material>();
    private static readonly (string name, Color colour, float metallic, float smooth)[] body_colours =
    {
        ("eu07_green"     , new Color(0.47f, 0.72f, 0.62f), 0.0f, 0.35f),          // the body: light green
        ("eu07_dark_green"     , new Color(0.17f, 0.40f, 0.30f), 0.0f, 0.35f),     // the ribbed band
        ("eu07_stripe_yellow"  , new Color(0.86f, 0.68f, 0.26f), 0.0f, 0.35f),
        ("eu07_handrail_ochre" , new Color(0.76f, 0.58f, 0.18f), 0.0f, 0.40f),
        ("eu07_handrail_yellow", new Color(0.88f, 0.74f, 0.30f), 0.0f, 0.40f),
        ("eu07_plate_white"    , new Color(0.86f, 0.85f, 0.86f), 0.0f, 0.30f),
        ("eu07_number_plate"   , new Color(0.17f, 0.40f, 0.30f), 0.0f, 0.35f),
        ("eu07_housing_grey"   , new Color(0.72f, 0.73f, 0.74f), 0.0f, 0.35f),
        ("eu07_bus_copper"     , new Color(0.60f, 0.40f, 0.30f), 0.6f, 0.50f),
        ("eu07_porcelain_brown", new Color(0.45f, 0.24f, 0.19f), 0.0f, 0.75f),
        ("eu07_drum_ochre"     , new Color(0.74f, 0.60f, 0.42f), 0.0f, 0.35f),
        ("eu07_roof_board"     , new Color(0.60f, 0.58f, 0.55f), 0.0f, 0.10f),
        ("eu07_box_brown"      , new Color(0.36f, 0.31f, 0.28f), 0.0f, 0.15f),
        ("eu07_roof_grey" , new Color(0.55f, 0.56f, 0.57f), 0.0f, 0.20f),
        ("eu07_black"     , new Color(0.04f, 0.04f, 0.04f), 0.0f, 0.15f),
        ("eu07_red"       , new Color(0.70f, 0.14f, 0.10f), 0.0f, 0.30f),
        ("eu07_glass"     , new Color(0.55f, 0.65f, 0.65f, 0.18f), 0.0f, 0.95f),
        ("eu07_dark_glass", new Color(0.03f, 0.04f, 0.05f), 0.3f, 0.90f),
        ("eu07_steel"     , new Color(0.55f, 0.55f, 0.55f), 0.8f, 0.55f),
        ("eu07_insulator" , new Color(0.75f, 0.72f, 0.65f), 0.0f, 0.60f),
        ("eu07_lamp"      , new Color(0.90f, 0.90f, 0.80f), 0.0f, 0.80f),
        ("eu07_lamp_glass"    , new Color(0.90f, 0.92f, 0.92f, 0.20f), 0.0f, 0.97f),     // a headlamp's clear glass (lit, the game swaps it for lamp_white_lit)
        ("eu07_lamp_reflector", new Color(0.70f, 0.70f, 0.68f), 0.85f, 0.55f),    // bowl behind it: dulled
        ("eu07_lamp_bulb"     , new Color(0.95f, 0.93f, 0.86f), 0.0f, 0.85f),
        ("eu07_red_lens"  , new Color(0.50f, 0.03f, 0.03f), 0.0f, 0.80f),
        ("eu07_grille"    , new Color(0.10f, 0.11f, 0.11f), 0.5f, 0.40f),
        ("eu07_lining"    , new Color(0.62f, 0.66f, 0.60f), 0.0f, 0.15f),
        ("eu07_cab_floor" , new Color(0.20f, 0.20f, 0.21f), 0.0f, 0.10f),
        // machine room (model/eu07_blockout.py)
        ("eu07_room_floor"    , new Color(0.300f, 0.310f, 0.320f), 0.4f, 0.18f),      // chequer plate, trodden dull
        // grey steel, as the real room
        ("eu07_cabinet"       , new Color(0.460f, 0.470f, 0.470f), 0.0f, 0.25f),      // cases and cabinets: grey sheet
        ("eu07_trim"          , new Color(0.360f, 0.360f, 0.310f), 0.0f, 0.30f),      // the cover strips over the cab lining's joints
        ("eu07_machine"       , new Color(0.230f, 0.240f, 0.250f), 0.3f, 0.35f),      // rotating machines: dark grey castings
        ("eu07_compressor"    , new Color(0.520f, 0.540f, 0.560f), 0.2f, 0.35f),      // compressors and their vessels: light grey
        ("eu07_guard_red"     , new Color(0.400f, 0.100f, 0.060f), 0.0f, 0.20f),      // the pipe rail round the main compressor
        ("eu07_board_blue"    , new Color(0.550f, 0.680f, 0.760f), 0.0f, 0.30f),      // the insulating board in the low-voltage cabinet
        ("eu07_brass"         , new Color(0.700f, 0.550f, 0.200f), 0.8f, 0.50f),      // fuse clips
        ("eu07_cage"          , new Color(0.050f, 0.050f, 0.050f, 0.45f), 0.3f, 0.30f),   // expanded-metal screens: seen through
        ("eu07_cage_louvre"   , new Color(1.000f, 1.000f, 1.000f, 0.99f), 0.3f, 0.30f),    // big louvre screen: expanded metal; picture gives colour and see-through
        ("eu07_visor"         , new Color(0.060f, 0.085f, 0.070f, 0.80f), 0.0f, 0.85f),   // the sun visors: smoked sheet, seen through darkly
        ("eu07_contact"       , new Color(0.470f, 0.270f, 0.180f), 0.6f, 0.45f),          // a contactor's arm and contact: worked copper
        ("eu07_panto_red"     , new Color(0.330f, 0.075f, 0.055f), 0.1f, 0.30f),      // the pantograph's frame: red oxide
        ("eu07_mirror"        , new Color(0.800f, 0.820f, 0.840f), 1.0f, 0.96f),      // the side mirrors' glass: all metal, all smooth
        // bogie (model/eu07_bogie.py): flat colours, black under brake dust (worn, as the body)
        ("eu07_bogie"         , new Color(0.150f, 0.132f, 0.115f), 0.0f, 0.12f),
        ("eu07_bogie_spring"  , new Color(0.165f, 0.145f, 0.125f), 0.2f, 0.18f),
        ("eu07_wheel_centre"  , new Color(0.185f, 0.155f, 0.125f), 0.1f, 0.14f),
        ("eu07_brake_shoe"    , new Color(0.200f, 0.130f, 0.090f), 0.0f, 0.10f),
        ("eu07_axlebox_cover" , new Color(0.560f, 0.450f, 0.150f), 0.0f, 0.18f),
        // desk parts (model/eu07_desk_parts.py): matte as the desk; handles a little worn bright
        ("eu07_wheel"       , new Color(0.06f, 0.065f, 0.07f), 0.0f, 0.30f),
        ("eu07_desk_grey"   , new Color(0.62f, 0.63f, 0.62f), 0.0f, 0.25f),
        ("eu07_desk_beige"  , new Color(0.62f, 0.55f, 0.42f), 0.0f, 0.15f),
        ("eu07_chrome"      , new Color(0.78f, 0.79f, 0.80f), 0.9f, 0.80f),
        ("eu07_bare_steel"  , new Color(0.70f, 0.68f, 0.62f), 0.6f, 0.50f),
        ("eu07_valve_green" , new Color(0.05f, 0.27f, 0.09f), 0.0f, 0.22f),
        ("eu07_air_pipe"    , new Color(0.13f, 0.27f, 0.52f), 0.0f, 0.25f),      // the cab's air pipes: blue
        ("eu07_desk_green"  , new Color(0.10f, 0.42f, 0.14f), 0.0f, 0.22f),
        ("eu07_handle_green", new Color(0.09f, 0.45f, 0.16f), 0.0f, 0.28f),
        ("eu07_grip_green"  , new Color(0.07f, 0.38f, 0.30f), 0.0f, 0.15f),
        ("eu07_cap_red"     , new Color(0.58f, 0.06f, 0.05f), 0.0f, 0.35f),
        ("eu07_white"       , new Color(0.85f, 0.85f, 0.80f), 0.0f, 0.25f),
    };

    // bogie model part put on a template bogie object, replacing what was there
    private static readonly Dictionary<string, Mesh> bogie_meshes = new Dictionary<string, Mesh>();

    private static void bogie_part(Transform onto, GameObject model, string name, string end)
    {
        Transform source = child(model.transform, name);
        if ((source.localPosition).magnitude > 0.001f || Quaternion.Angle(source.localRotation, Quaternion.identity) > 0.1f || (source.localScale - Vector3.one).magnitude > 0.001f)
            throw new InvalidDataException($"bogie part {name} came in moved, turned or scaled: {source.localPosition:F3} {source.localRotation.eulerAngles:F1} {source.localScale:F3}");
        if (!bogie_meshes.TryGetValue(name, out Mesh mesh))
        {
            mesh = UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
            mesh.name = name;
            AssetDatabase.CreateAsset(mesh, $"{target}/EU07_{name}.asset");
            bogie_meshes[name] = mesh;
            Debug.Log($"EU07 BUILD: {name} centre {mesh.bounds.center:F3} size {mesh.bounds.size:F3} readable {mesh.isReadable}");
        }
        onto.localPosition = Vector3.zero;
        onto.localRotation = Quaternion.identity;
        onto.localScale = Vector3.one;
        onto.GetComponent<MeshFilter>().sharedMesh = mesh;
        onto.GetComponent<MeshRenderer>().sharedMaterials =
            source.GetComponent<MeshRenderer>().sharedMaterials.Select(material => body_material(material.name)).ToArray();
    }

    // desk part from model/eu07_desk_parts.py: mesh + materials by name
    // mesh saved as a text asset so tools/desk_clearance.py can read it
    private static GameObject parts_model;
    private static readonly Dictionary<string, Mesh> part_meshes = new Dictionary<string, Mesh>();

    private static GameObject desk_part(Transform parent, string name, Vector3 at)
    {
        Transform source = child(parts_model.transform, name);
        if (!part_meshes.TryGetValue(name, out Mesh mesh))
        {
            mesh = UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
            mesh.name = name;
            AssetDatabase.CreateAsset(mesh, $"{target}/EU07_part_{name}.asset");
            part_meshes[name] = mesh;
        }
        GameObject made = node(parent, name, at);
        made.AddComponent<MeshFilter>().sharedMesh = mesh;
        made.AddComponent<MeshRenderer>().sharedMaterials =
            source.GetComponent<MeshRenderer>().sharedMaterials.Select(material => body_material(material.name)).ToArray();
        return made;
    }

    // part origin in the model = its place here; must match this script to the millimetre
    private static Vector3 placed(string name, Vector3 expected)
    {
        Vector3 found = child(parts_model.transform, name).localPosition;
        if ((found - expected).magnitude > 0.001f)
            throw new InvalidDataException($"desk part {name}: the model puts it at {found:F4}, this script at {expected:F4}");
        return expected;
    }

    // OUTSIDE PAINT: eu07_ext_a (shell), eu07_ext_b (other outside parts); UV1 = atlas, UV2 = metres
    // model/paint/ext_<a|b>_<state>_albedo + _mask (R metallic, G shade, A smoothness), tools/paint.py
    // DV's DE2 wear detail maps added by CCL at game start ("procedural materials"); stand-ins until then
    // a bake belongs to one atlas packing: check_paint refuses others
    private const float paint_wear_tiles = 1.0f;    // DV's wear picture: one per metre
    private static readonly Dictionary<string, Texture2D> paint_pictures = new Dictionary<string, Texture2D>();
    private static string paint_folder => Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/paint"));

    private static void check_paint()
    {
        string of_model = File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_paint_uv.txt"))).Replace("\r", "").Trim();
        // bogie atlas e: own model (eu07_bogie.py), fingerprint written beside it
        string bogie_mark = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_bogie_paint_uv.txt"));
        if (File.Exists(bogie_mark))
            of_model = string.Join("\n", (of_model + "\n" + File.ReadAllText(bogie_mark).Replace("\r", "").Trim()).Split('\n').OrderBy(line => line));
        string about = Path.Combine(paint_folder, "paint_uv.txt");
        if (!File.Exists(about))
            throw new FileNotFoundException("no paint: run model/eu07_blockout.py with --bake 4096, then tools/paint.py", about);
        string of_paint = File.ReadAllText(about).Replace("\r", "").Trim();
        if (of_paint != of_model)
            throw new InvalidDataException($"the paint in model/paint is for atlases [{of_paint.Replace("\n", ", ")}], the model's are [{of_model.Replace("\n", ", ")}]: bake and paint again");
        Debug.Log("EU07 BUILD: paint for the atlases " + of_model.Replace("\n", ", "));
    }

    private static Texture2D paint_picture(string file, bool colour, int largest, bool normal = false, string folder = null)
    {
        if (paint_pictures.TryGetValue(file, out Texture2D known))
            return known;
        string picture = $"{target}/EU07_paint_{file}.png";
        string from = Path.Combine(folder ?? paint_folder, file + ".png"), to = Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_paint_" + file + ".png");
        if (File.Exists(from))
            File.Copy(from, to, true);
        else if (!File.Exists(to))
            throw new FileNotFoundException("no such picture of the paint", from);
        AssetDatabase.ImportAsset(picture, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = (TextureImporter) AssetImporter.GetAtPath(picture) ?? throw new IOException("no picture at " + picture);
        importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = colour;
        importer.mipmapEnabled = true;
        importer.anisoLevel = 4;
        importer.maxTextureSize = largest;
        importer.wrapMode = largest <= 16 || folder != null ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;    // stand-ins and finishes tile
        importer.alphaIsTransparency = false;
        importer.textureCompression = colour ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
        importer.SaveAndReimport();
        Texture2D made = AssetDatabase.LoadAssetAtPath<Texture2D>(picture) ?? throw new IOException("no picture at " + picture);
        paint_pictures[file] = made;
        return made;
    }

    // small one-colour picture: stands in for DV's wear until the game adds it
    private static Texture2D stand_in(string file, Color32 colour, bool normal)
    {
        Texture2D drawn = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        drawn.SetPixels32(Enumerable.Repeat(colour, 64).ToArray());
        File.WriteAllBytes(Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_paint_" + file + ".png"), drawn.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(drawn);
        return paint_picture(file, false, 8, normal);
    }

    // PAINT CANS: as delivered = worn (DVRT); fresh -> DVRT_New, rust -> Relic_Rusty, primer -> Null (stripped)
    // extra states at half size (full size made a ~60 MB bundle)
    private static readonly (string state, string theme, ProceduralMaterialDefinitions.MaterialType wear)[] paint_cans =
    {
        ("worn" , "DVRT"       , ProceduralMaterialDefinitions.MaterialType.PaintDetailsOld),    // itself: so DV counts the worn theme as one this car takes
        ("new"  , "DVRT_New"   , ProceduralMaterialDefinitions.MaterialType.PaintDetailsNew),
        ("rusty", "Relic_Rusty", ProceduralMaterialDefinitions.MaterialType.PaintDetailsRusted),
        ("primer", "Null"      , ProceduralMaterialDefinitions.MaterialType.PaintDetailsNew),
    };
    private static readonly string[] painted_names = { "eu07_ext_a", "eu07_ext_b" };
    // shared over the MU cable on top of DV's six (only WE-4Us know the names)
    // not shared: 110 V battery, breakers (made ready by hand per unit), lights, hauled-run
    private static readonly (string connection, string control, int notches)[] mu_shared =
    {
        ("EU07_PANTOGRAPH_REAR", "pantographAsk", 2), ("EU07_PANTOGRAPH_FRONT", "pantograph2Ask", 2),
        ("EU07_WS_ON", "fastBreakerOn", 2), ("EU07_WS_OFF", "fastBreakerOff", 2),
        ("EU07_RELAY_RESET", "relayReset", 2), ("EU07_AUX_RESET", "auxRelayReset", 2), ("EU07_LINE_OFF", "lineContactorsOff", 2), ("EU07_WHEEL_SLIP", "wheelSlip", 2),
        ("EU07_CONVERTER", "converterSwitch", 2), ("EU07_COMPRESSORS", "compressorSwitch", 2), ("EU07_HEATING", "heatingSwitch", 2),
        ("EU07_HIGH_START", "highStart", 2), ("EU07_FIELD", "fieldLever", 7),
        // battery switch shared: units go onto battery together; each keeps own charge and breaker
        ("EU07_LAST_MILE", "batterySwitch", 2),
    };
    // INSIDE PAINT: second TrainCarPaint "Interior" + one on the cab fittings that follows it
    // worn as delivered (DVRT); DVRT_New = plain enamel desk, new walls; (state, original, substitute)
    private static readonly List<(string state, Material original, Material substitute)> inside_cans = new List<(string state, Material original, Material substitute)>();

    // one picture from the decal sheet (tools/decals.py) on a build panel: flat, facing -z, sized by the sheet
    private static void decal(Transform panel, string what, Vector3 at)
    {
        string sheet = File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/decals/places.json")));
        System.Text.RegularExpressions.Match entry = System.Text.RegularExpressions.Regex.Match(sheet, "\"" + what + @"""\s*:\s*\{([^}]*)\}");
        if (!entry.Success)
            throw new KeyNotFoundException("the sheet of plates has no " + what);
        float of(string field)
        {
            System.Text.RegularExpressions.Match value = System.Text.RegularExpressions.Regex.Match(entry.Groups[1].Value, "\"" + field + @"""\s*:\s*([-0-9.eE]+)");
            return float.Parse(value.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        float w = of("wide") * 0.5f, h = of("tall") * 0.5f;
        Mesh piece = new Mesh { name = "decal_" + what };
        piece.vertices = new[] { new Vector3(-w, -h, 0.0f), new Vector3(w, -h, 0.0f), new Vector3(w, h, 0.0f), new Vector3(-w, h, 0.0f) };
        piece.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
        piece.uv = new[] { new Vector2(of("u0"), of("v0")), new Vector2(of("u1"), of("v0")), new Vector2(of("u1"), of("v1")), new Vector2(of("u0"), of("v1")) };
        piece.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };    // both windings: seen from one side only
        piece.RecalculateBounds();
        AssetDatabase.CreateAsset(piece, $"{target}/EU07_decal_{what}_{Guid.NewGuid():N}.asset");
        GameObject shown = node(panel, "Decal_" + what, at);
        shown.AddComponent<MeshFilter>().sharedMesh = piece;
        shown.AddComponent<MeshRenderer>().sharedMaterial = body_material("eu07_decals");
    }

    private static Material painted(string name, string state)
    {
        string atlas = name.Substring(name.Length - 1);
        int largest = state == "worn" ? 4096 : 2048;
        Texture2D mask = paint_picture($"ext_{atlas}_{state}_mask", false, largest / 2);
        Material made = new Material(Shader.Find("Standard")) { color = Color.white };
        made.mainTexture = paint_picture($"ext_{atlas}_{state}_albedo", true, largest);
        made.SetTexture("_MetallicGlossMap", mask);
        made.EnableKeyword("_METALLICGLOSSMAP");
        made.SetFloat("_GlossMapScale", 1.0f);
        made.SetTexture("_OcclusionMap", mask);
        made.SetFloat("_OcclusionStrength", 0.8f);
        made.SetTexture("_BumpMap", stand_in("flat", new Color32(128, 128, 255, 255), true));
        made.EnableKeyword("_NORMALMAP");
        made.SetTexture("_DetailAlbedoMap", stand_in("grey", new Color32(128, 128, 128, 255), false));
        made.SetTexture("_DetailNormalMap", stand_in("flat", new Color32(128, 128, 255, 255), true));
        made.EnableKeyword("_DETAIL_MULX2");
        made.SetFloat("_UVSec", 1.0f);    // wear by UV2, metres
        made.SetTextureScale("_DetailAlbedoMap", new Vector2(paint_wear_tiles, paint_wear_tiles));
        AssetDatabase.CreateAsset(made, $"{target}/EU07_paint_{name}_{state}.mat");
        return made;
    }

    private static Material body_material(string name)
    {
        if (body_materials.TryGetValue(name, out Material known))
            return known;
        if (name == "eu07_ext_a" || name == "eu07_ext_b" || name == "eu07_ext_e")    // e: bogie's, worn only
        {
            body_materials[name] = painted(name, "worn");
            return body_materials[name];
        }
        if (name == "eu07_decals" || name == "eu07_decals_cut")
        {
            // PLATES AND SIGNS: tools/decals.py -> model/decals/sheet.png, by UV1
            // lettering cut out by sheet alpha (own material); plates ignore alpha
            string sheet = $"{target}/EU07_decals.png";
            Texture2D drawn = AssetDatabase.LoadAssetAtPath<Texture2D>(sheet);
            if (!body_materials.ContainsKey("eu07_decals") && !body_materials.ContainsKey("eu07_decals_cut"))
            {
                File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/decals/sheet.png")), Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_decals.png"), true);
                AssetDatabase.ImportAsset(sheet, ImportAssetOptions.ForceSynchronousImport);
                TextureImporter sheet_importer = (TextureImporter) AssetImporter.GetAtPath(sheet) ?? throw new IOException("no picture at " + sheet);
                sheet_importer.mipmapEnabled = true;
                sheet_importer.anisoLevel = 8;
                sheet_importer.maxTextureSize = 4096;
                sheet_importer.wrapMode = TextureWrapMode.Clamp;
                sheet_importer.alphaSource = TextureImporterAlphaSource.FromInput;
                sheet_importer.alphaIsTransparency = true;    // letter edges take the ink's colour
                sheet_importer.mipMapsPreserveCoverage = true;    // cut-out letters don't thin with distance
                sheet_importer.alphaTestReferenceValue = 0.5f;
                sheet_importer.textureCompression = TextureImporterCompression.CompressedHQ;
                sheet_importer.SaveAndReimport();
                drawn = AssetDatabase.LoadAssetAtPath<Texture2D>(sheet);
            }
            Material plates = new Material(Shader.Find("Standard")) { color = Color.white };
            plates.mainTexture = drawn ?? throw new IOException("no picture at " + sheet);
            plates.SetFloat("_Glossiness", 0.30f);
            if (name == "eu07_decals_cut")
            {
                plates.SetFloat("_Mode", 1.0f);                                  // Cutout
                plates.SetFloat("_Cutoff", 0.5f);
                plates.SetOverrideTag("RenderType", "TransparentCutout");
                plates.EnableKeyword("_ALPHATEST_ON");
                plates.renderQueue = (int) UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            AssetDatabase.CreateAsset(plates, $"{target}/EU07_{name.Substring(5)}.mat");
            body_materials[name] = plates;
            return plates;
        }
        if (name == "eu07_int_a" || name == "eu07_int_c" || name == "eu07_int_d")
        {
            // INSIDE: a = shell inside (atlas c, no outside wear, no cans); c = cab fittings (half size); d = machine room
            string pictures = name == "eu07_int_a" ? "ext_c_worn" : "int_" + name.Substring(name.Length - 1);
            int largest = name == "eu07_int_c" ? 2048 : 4096;
            Material indoors = new Material(Shader.Find("Standard")) { color = Color.white };
            Texture2D indoors_mask = paint_picture(pictures + "_mask", false, largest / 2);
            indoors.mainTexture = paint_picture(pictures + "_albedo", true, largest);
            indoors.SetTexture("_MetallicGlossMap", indoors_mask);
            indoors.EnableKeyword("_METALLICGLOSSMAP");
            indoors.SetFloat("_GlossMapScale", 1.0f);
            indoors.SetTexture("_OcclusionMap", indoors_mask);
            indoors.SetFloat("_OcclusionStrength", 0.6f);
            if (name == "eu07_int_a")
            {
                // LINING GRAIN: enamel orange peel + brush marks (tools/finishes.py, peel), 3 per metre over the 5 mm/dot picture
                string finishes = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/finishes"));
                indoors.SetTexture("_BumpMap", stand_in("flat", new Color32(128, 128, 255, 255), true));
                indoors.EnableKeyword("_NORMALMAP");
                indoors.SetTexture("_DetailAlbedoMap", paint_picture("peel_albedo", true, 512, false, finishes));
                indoors.SetTexture("_DetailNormalMap", paint_picture("peel_normal", false, 512, true, finishes));
                indoors.SetFloat("_DetailNormalMapScale", 0.28f);    // higher reads as corduroy in grazing light
                indoors.EnableKeyword("_DETAIL_MULX2");
                indoors.SetFloat("_UVSec", 1.0f);
                indoors.SetTextureScale("_DetailAlbedoMap", new Vector2(3.0f, 3.0f));
            }
            AssetDatabase.CreateAsset(indoors, $"{target}/EU07_paint_{name}.mat");
            body_materials[name] = indoors;
            return indoors;
        }
        int index = Array.FindIndex(body_colours, entry => entry.name == name);
        if (index < 0)
            throw new KeyNotFoundException("the body model has a material this script does not know: " + name);
        Material made = new Material(Shader.Find("Standard")) { color = body_colours[index].colour };
        made.SetFloat("_Metallic", body_colours[index].metallic);
        made.SetFloat("_Glossiness", body_colours[index].smooth);
        if (body_colours[index].colour.a < 1.0f)
        {
            // Standard shader "Transparent" mode, set by hand as its inspector does
            made.SetFloat("_Mode", 3.0f);
            made.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            made.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            made.SetInt("_ZWrite", 0);
            made.DisableKeyword("_ALPHATEST_ON");
            made.DisableKeyword("_ALPHABLEND_ON");
            made.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            made.renderQueue = 3000;
        }
        if (name == "eu07_cage_louvre")
        {
            // EXPANDED METAL (tools/paint.py, expanded_metal): 16.7 tiles/m, by mesh metres (see louvre screen)
            made.mainTexture = paint_picture("expanded_metal", true, 256, false, paint_folder);
            made.SetTextureScale("_MainTex", new Vector2(16.7f, 16.7f));
        }
        if (name == "eu07_brake_shoe")
        {
            // DV makes hot brake blocks glow via _EmissionColor: keyword must be on (see lens())
            made.EnableKeyword("_EMISSION");
            made.SetColor("_EmissionColor", new Color(0.002f, 0.002f, 0.002f));
            made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(made, $"{target}/EU07_body_{name}.mat");
        body_materials[name] = made;
        return made;
    }

    // EU07 handles: one sim control each, fed by a desk switch or lever
    private static readonly string[] controls = { "fastBreaker", "relayReset", "lineContactorsOff", "highStart", "fieldLever" };

    private static void wire_simulation(Transform sim)
    {
        // railcar's brake cut-out starts CUT OUT (pipe never fills): cut it in, at every load
        ExternalControlDefinitionProxy[] cutouts = sim.GetComponentsInChildren<ExternalControlDefinitionProxy>(true).Where(one => new SerializedObject(one).FindProperty("ID").stringValue == "brakeCutout").ToArray();
        if (cutouts.Length != 1)
            throw new System.Exception($"EU07: {cutouts.Length} brakeCutout controls in the simulation, expected 1");
        SerializedObject cut_in = new SerializedObject(cutouts[0]);
        number(cut_in, "defaultValue", 1.0f);
        flag(cut_in, "saveState", false);
        cut_in.ApplyModifiedPropertiesWithoutUndo();
        // HORN: real lever rests in the MIDDLE (low one way, high the other): control rests at 0.5
        // mod reads the direction (eu07_driver.cs)
        if (desk_v2)
        {
            // smoothing control, not ExternalControlDefinition: found by ID
            MonoBehaviour[] horns = sim.GetComponentsInChildren<MonoBehaviour>(true).Where(one => one != null && new SerializedObject(one).FindProperty("ID")?.stringValue == "hornControl"
                && new SerializedObject(one).FindProperty("defaultValue") != null).ToArray();
            HornControlProxy[] horn_handles = sim.GetComponentsInChildren<HornControlProxy>(true);
            HornDefinitionProxy[] horn_itself = sim.GetComponentsInChildren<HornDefinitionProxy>(true);
            if (horns.Length != 1 || horn_handles.Length != 1 || horn_itself.Length != 1)
                throw new System.Exception($"EU07: {horns.Length} hornControl controls, {horn_handles.Length} horn handles, {horn_itself.Length} horns in the simulation, expected 1 of each");
            SerializedObject rest = new SerializedObject(horns[0]);
            number(rest, "defaultValue", 0.5f);
            rest.ApplyModifiedPropertiesWithoutUndo();
            foreach ((UnityEngine.Object part, string field) in new (UnityEngine.Object, string)[] { (horn_handles[0], "neutralAt0"), (horn_itself[0], "controlNeutralAt0") })
            {
                SerializedObject middle = new SerializedObject(part);
                flag(middle, field, false);
                middle.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        foreach (string control in desk_v2 ? controls_v2 : controls)
        {
            GameObject handle = new GameObject(control);
            handle.transform.SetParent(sim, false);
            handle.transform.SetSiblingIndex(child(sim, "traction").GetSiblingIndex());
            SerializedObject made = new SerializedObject(handle.AddComponent<ExternalControlDefinitionProxy>());
            text(made, "ID", control);
            if (desk_v2)
            {
                // DV lamp selectors rest at 0.4 = "off" of six positions
                number(made, "defaultValue", on_at_start_v2.Contains(control) ? 1.0f : control.StartsWith("trainLights") ? 0.4f : 0.0f);
                flag(made, "saveState", saved_v2.Contains(control));
            }
            made.ApplyModifiedPropertiesWithoutUndo();
        }
        if (desk_v2)
        {
            // per-cab handles (cab_driving) + cab B's own controls; driving ones not saved: mod loads them into the live cab
            IEnumerable<(string id, float rest, bool saved)> per_cab = cab_driving
                .SelectMany(one => new[] { (one.control + "A", one.rest, false), (one.control + "B", one.rest, false) })
                .Concat(cab_b_own.Select(own => (own + "B", own == "cabCutout" ? 1.0f : on_at_start_v2.Contains(own) ? 1.0f : 0.0f, saved_v2.Contains(own))));
            foreach ((string id, float rest, bool saved) in per_cab)    // cab B starts cut out: cab A drives a new loco
            {
                GameObject handle = new GameObject(id);
                handle.transform.SetParent(sim, false);
                handle.transform.SetSiblingIndex(child(sim, "traction").GetSiblingIndex());
                SerializedObject made = new SerializedObject(handle.AddComponent<ExternalControlDefinitionProxy>());
                text(made, "ID", id);
                number(made, "defaultValue", rest);
                flag(made, "saveState", saved);
                made.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        if (desk_v2)
        {
            IndependentFusesDefinitionProxy[] boxes = sim.GetComponentsInChildren<IndependentFusesDefinitionProxy>(true)
                .Where(one => new SerializedObject(one).FindProperty("ID").stringValue == "fusebox").ToArray();
            CCL.Types.Proxies.Simulation.SanderDefinitionProxy[] sanders = sim.GetComponentsInChildren<CCL.Types.Proxies.Simulation.SanderDefinitionProxy>(true);
            if (boxes.Length != 1 || sanders.Length != 1)
                throw new System.Exception($"EU07: {boxes.Length} fuse boxes and {sanders.Length} sanders in the simulation, expected 1 of each");
            List<FuseDefinition> fuses = boxes[0].fuses.ToList();
            foreach (string fuse in breakers_far.Concat(breakers_near).Select(one => one.fuse).Concat(new[] { fed_headlights, fed_lighting, fed_sanding, fed_gadgets }))
            {
                string id = fuse.Substring("fusebox.".Length);
                if (!fuses.Any(one => one.id == id))
                    fuses.Add(new FuseDefinition(id, true));
            }
            // COLD START: battery off, motor breaker and flap breakers out; only the mod's three fuses in (saves keep state)
            foreach (FuseDefinition one in fuses)
                one.initialState = one.id.StartsWith("FED_");
            boxes[0].fuses = fuses.ToArray();
            boxes[0].OnValidate();
            EditorUtility.SetDirty(boxes[0]);
            Debug.Log("EU07 BUILD: fuses " + string.Join(" ", fuses.Select(one => one.id + (one.initialState ? "=in" : "=out"))) + "; the sander's was " + sanders[0].powerFuseId);
            sanders[0].powerFuseId = fed_sanding;
            EditorUtility.SetDirty(sanders[0]);
        }
        if (desk_v2)
        {
            // MOTOR ISOLATOR LOCKS (DV ControlBlocker), as behind the real HV door:
            // door shut while cock at P; cock can't go to P with door open; isolators held unless door open and wheel at 0
            GameObject locks = new GameObject("eu07Locks");
            locks.transform.SetParent(sim, false);
            ControlBlockerProxy.BlockerDefinition by(string port, float at, bool above) => new ControlBlockerProxy.BlockerDefinition
            {
                blockerPortId = port, thresholdValue = at,
                blockType = above ? ControlBlockerProxy.BlockerDefinition.BlockType.BLOCK_ON_ABOVE_THRESHOLD : ControlBlockerProxy.BlockerDefinition.BlockType.BLOCK_ON_BELOW_THRESHOLD,
            };
            void hold(string port, params ControlBlockerProxy.BlockerDefinition[] holders)
            {
                ControlBlockerProxy held = locks.AddComponent<ControlBlockerProxy>();
                held.blockedControlPortId = port;
                held.resetToZeroOnBlock = false;
                held.blockers = holders;
                held.OnValidate();
                EditorUtility.SetDirty(held);
            }
            hold("isolatorDoor.EXT_IN", by("hvInterlock.EXT_IN", 0.5f, true));
            hold("hvInterlock.EXT_IN", by("isolatorDoor.EXT_IN", 0.05f, true));
            foreach (string isolator in new[] { "motorIsolator12.EXT_IN", "motorIsolator34.EXT_IN" })
                hold(isolator, by("isolatorDoor.EXT_IN", 0.3f, false), by("hvInterlock.EXT_IN", 0.5f, true), by("eu07.CONTROLLER_POSITION", 0.5f, true));
            // dead cab's handles held while it isn't the live cab
            foreach (var (control, _, _) in cab_driving)
                foreach (string cab in new[] { "A", "B" })
                    hold($"{control}{cab}.EXT_IN", by($"eu07.CAB_{cab}_LIVE", 0.5f, false));
            // FOLLOWER DESK HELD: DV's MUSlaveBlockAllControls only holds handles with a lock
            // so every leader control gets an empty lock; holds when DV (or the mod, cut-out cab) says follower
            HashSet<string> locked = new HashSet<string>(sim.GetComponentsInChildren<ControlBlockerProxy>(true).Select(one => one.blockedControlPortId));
            foreach (string port in new[] { "throttle.EXT_IN", "reverser.CONTROL_EXT_IN", "brake.EXT_IN", "indBrake.EXT_IN", "sander.CONTROL_EXT_IN", "trainLightsFront.EXT_IN", "trainLightsRear.EXT_IN" }
                .Concat(mu_shared.Select(shared => shared.control + ".EXT_IN")))
            {
                if (locked.Add(port))
                    hold(port);
            }
        }
        if (desk_v2)
        {
            // independent brake: railcar's brake set-up has one, no handle; DV takes it via an overridable control
            GameObject direct = new GameObject("indBrake");
            direct.transform.SetParent(sim, false);
            direct.transform.SetSiblingIndex(child(sim, "traction").GetSiblingIndex());
            SerializedObject made = new SerializedObject(direct.AddComponent<ExternalControlDefinitionProxy>());
            text(made, "ID", "indBrake");
            made.ApplyModifiedPropertiesWithoutUndo();
            OverridableControlProxy overridable = direct.AddComponent<OverridableControlProxy>();
            overridable.portId = "indBrake.EXT_IN";
            overridable.ControlType = OverridableControlType.IndBrake;
            // each overridable control knows its lock: keys/remote go through it, not the handle
            // headlight ports off DV's overridable controls (MU would share our 256-setup number as a selector)
            // trainLightsFront/Rear: only what a leading selector says; mod applies it, never writes it
            foreach (OverridableControlProxy shared_lamps in sim.GetComponentsInChildren<OverridableControlProxy>(true))
            {
                if (shared_lamps.ControlType == OverridableControlType.HeadlightsFront || shared_lamps.ControlType == OverridableControlType.HeadlightsRear)
                {
                    Debug.Log($"EU07 build: the game's control of {shared_lamps.portId} ({shared_lamps.ControlType}) is taken off the lamps' own number");
                    UnityEngine.Object.DestroyImmediate(shared_lamps);
                }
            }
            foreach ((string control, OverridableControlType kind) in new[] { ("trainLightsFront", OverridableControlType.HeadlightsFront), ("trainLightsRear", OverridableControlType.HeadlightsRear) })
            {
                OverridableControlProxy listens = child(sim, control).gameObject.AddComponent<OverridableControlProxy>();
                listens.portId = control + ".EXT_IN";
                listens.ControlType = kind;
            }
            ControlBlockerProxy[] made_locks = sim.GetComponentsInChildren<ControlBlockerProxy>(true);
            foreach (OverridableControlProxy driven in sim.GetComponentsInChildren<OverridableControlProxy>(true))
            {
                if (driven.controlBlocker == null)
                {
                    driven.controlBlocker = made_locks.FirstOrDefault(one => one.blockedControlPortId == driven.portId);
                    EditorUtility.SetDirty(driven);
                }
                Debug.Log($"EU07 build: the game's control of {driven.portId} ({driven.ControlType}) {(driven.controlBlocker != null ? "knows its lock" : "HAS NO LOCK")}");
            }
        }

        GameObject holder = new GameObject("eu07");
        holder.transform.SetParent(sim, false);
        holder.transform.SetSiblingIndex(child(sim, "traction").GetSiblingIndex());
        ConfigurablePortsDefinitionProxy definition = holder.AddComponent<ConfigurablePortsDefinitionProxy>();
        definition.Ports = (desk_v2 ? ports.Concat(ports_v2) : ports).Select(port => new ConfigurablePortsDefinitionProxy.PortStartValue(new PortDefinition(port.type, port.value, port.id), 0.0f)).ToArray();
        SerializedObject serialized = new SerializedObject(definition);
        text(serialized, "ID", "eu07");
        serialized.ApplyModifiedPropertiesWithoutUndo();
        definition.OnValidate();

        SimConnectionsDefinitionProxy connections = sim.GetComponent<SimConnectionsDefinitionProxy>()
            ?? throw new MissingComponentException("[sim] has no connections component");
        if (connections.connections.RemoveAll(link => link.fullPortIdIn == "traction.TORQUE_IN") != 1)
            throw new InvalidOperationException("expected exactly one feed into traction.TORQUE_IN");
        connections.connections.Add(new PortConnectionProxy { fullPortIdOut = "eu07.TORQUE_OUT", fullPortIdIn = "traction.TORQUE_IN" });
        int rewired = 0;
        foreach (PortReferenceConnectionProxy reference in connections.portReferenceConnections)
        {
            if (reference.portReferenceId == "busBar.CURRENT_DRAW" || reference.portReferenceId == "electricityMeter.CURRENT_DRAW")
            {
                reference.portId = "eu07.LINE_CURRENT";
                ++rewired;
            }
        }
        if (rewired != 2)
            throw new InvalidOperationException($"expected two readers of the line current, found {rewired}");

        // MOTOR HEAT: DV's tmHeat (lamp, wear, fuse, burnout) fed from eu07.MOTOR_HEAT (eu07_driver.cs)
        // DV's own cooler stopped: cooling is in our model
        int heated = 0;
        foreach (PortReferenceConnectionProxy reference in connections.portReferenceConnections)
        {
            if (reference.portReferenceId == "tmHeat.HEAT_IN_0")
            {
                reference.portId = "eu07.MOTOR_HEAT";
                ++heated;
            }
        }
        MonoBehaviour[] coolers = sim.GetComponentsInChildren<MonoBehaviour>(true).Where(one => one != null && new SerializedObject(one).FindProperty("ID")?.stringValue == "tmPassiveCooler"
            && new SerializedObject(one).FindProperty("coolingRate") != null).ToArray();
        if (heated != 1 || coolers.Length != 1)
            throw new InvalidOperationException($"expected one heat input of the motors and one cooler, found {heated} and {coolers.Length}");
        SerializedObject cooler = new SerializedObject(coolers[0]);
        number(cooler, "coolingRate", 0.0f);
        cooler.ApplyModifiedPropertiesWithoutUndo();

        // his traction motors: off the wheels AND the throttle, or DV blows them up
        // reference can't be left open (sim won't start): reads a port that is always 0
        int idled = 0;
        foreach (PortReferenceConnectionProxy reference in connections.portReferenceConnections)
        {
            if (reference.portReferenceId == "tm.THROTTLE" || reference.portReferenceId == "voltageRegulator.THROTTLE")
            {
                reference.portId = "eu07.ZERO";
                ++idled;
            }
        }
        if (idled != 2)
            throw new InvalidOperationException($"expected his motors and regulator each to read the throttle once, found {idled}");
        connections.PopulateComponents();
        connections.AutoSort();
        connections.OnValidate();
    }

    // ---- DESK (v1) ----
    private static void shape_desk(GameObject root)
    {
        root.name = "EU07_interactables";
        Transform desk = child(root.transform, "ControlTemplate");
        move_z(desk, body_half - 0.47f);
        notches(child(desk, "Throttle"), 44);

        // his train brake lever carries the throttle's key actions: fix
        keys(child(desk, "TrainBrake"), "BrakeIncremental", "BrakeAbsolute");
        // all four switches answer the electrics fuse's key
        keys(child(desk, "MotorFuse"), "TractionMotorFuse");
        keys(child(desk, "Pantograph"), "Pantograph");
        no_keys(child(desk, "Pantograph2"));

        // field lever: his train brake cloned, 7 notches, on the dynamic brake keys
        Transform field = clone(child(desk, "TrainBrake"), "FieldLever", new Vector3(0.05f, 1.00f, 0.0f), "fieldLever.EXT_IN", "eu07/field_lever");
        notches(field, 7);
        keys(field, "DynamicBrakeIncremental", "DynamicBrakeAbsolute");

        // four switches after his four, same row
        Transform pattern = child(desk, "Pantograph2");
        Vector3 row = pattern.localPosition;
        (string name, string port, string label)[] switches =
        {
            ("FastBreaker"      , "fastBreaker.EXT_IN"      , "eu07/fast_breaker"),
            ("RelayReset"       , "relayReset.EXT_IN"       , "eu07/relay_reset"),
            ("LineContactorsOff", "lineContactorsOff.EXT_IN", "eu07/line_contactors_off"),
            ("HighStart"        , "highStart.EXT_IN"        , "eu07/high_start"),
        };
        for (int index = 0; index < switches.Length; ++index)
        {
            Transform made = clone(pattern, switches[index].name, new Vector3(row.x + 0.12f + 0.10f * index, row.y, row.z), switches[index].port, switches[index].label);
            no_keys(made);
        }
    }

    private static void notches(Transform control, int count)
    {
        LeverProxy lever = control.GetComponentInChildren<LeverProxy>(true)
            ?? throw new MissingComponentException(control.name + " is not a lever");
        SerializedObject serialized = new SerializedObject(lever);
        serialized.FindProperty("notches").intValue = count;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    // key actions of a control, in keyboard component order
    private static void keys(Transform control, params string[] actions)
    {
        int next = 0;
        foreach (AKeyboardInputProxy input in control.GetComponentsInChildren<AKeyboardInputProxy>(true))
        {
            foreach (FieldInfo field in input.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.GetValue(input) is AKeyboardInputProxy.ActionReference action)
                {
                    action.name = actions[Math.Min(next, actions.Length - 1)];
                    ++next;
                }
            }
            input.OnValidate();
            EditorUtility.SetDirty(input);
        }
        if (next == 0)
            throw new MissingComponentException(control.name + " takes no keys at all");
    }

    // no key = no keyboard component: an empty action fails CCL's InputValidator ("useAction is invalid")
    private static void no_keys(Transform control)
    {
        foreach (AKeyboardInputProxy input in control.GetComponentsInChildren<AKeyboardInputProxy>(true))
            UnityEngine.Object.DestroyImmediate(input);
    }

    private static Transform clone(Transform pattern, string name, Vector3 at, string port, string label)
    {
        Transform made = UnityEngine.Object.Instantiate(pattern.gameObject, pattern.parent).transform;
        made.name = name;
        made.localPosition = at;
        InteractablePortFeederProxy feeder = made.GetComponentInChildren<InteractablePortFeederProxy>(true)
            ?? throw new MissingComponentException(pattern.name + " feeds no port");
        feeder.portId = port;
        EditorUtility.SetDirty(feeder);
        // label = whichever component has a text key
        foreach (MonoBehaviour component in made.GetComponentsInChildren<MonoBehaviour>(true))
        {
            SerializedObject serialized = new SerializedObject(component);
            SerializedProperty key = serialized.FindProperty("key");
            if (key != null && key.propertyType == SerializedPropertyType.String)
            {
                key.stringValue = label;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }
        return made;
    }

    // ---- DESK V2 ----
    // docs/desk/design_desk.md; layout per docs/research/research_cab.md; primitives under one parent, DeskA
    // desk frame: origin cab floor, centreline, driver-side edge; +X driver's right, +Y up, +Z windscreen; driver at x +0.50
    // panel frame: X right, Y up the panel, -Z out to the viewer (Knsgf's frame)
    // PanelTop flat (-Z up, +Y to windscreen); PanelSlope 45 deg; PanelPedestal flat on the controller pedestal
    // angles: AngleAxis sense, clockwise seen from the axis's + end; assumed, one constant each below

    // captions: English by default; real plate wording as the Polish translation
    private static readonly (string term, string english, string polish)[] labels_v2 =
    {
        ("eu07/light_left"         , "Headlight, left"         , "Reflektor lewy"),
        ("eu07/light_top"          , "Headlight, top"          , "Reflektor górny"),
        ("eu07/light_right"        , "Headlight, right"        , "Reflektor prawy"),
        ("eu07/red_left"           , "Tail lamp, left"         , "Końcówka lewa"),
        ("eu07/red_right"          , "Tail lamp, right"        , "Końcówka prawa"),
        ("eu07/dim_left"           , "Dim left headlight"      , "Przyciemn. refl. lewego"),
        ("eu07/dim_top"            , "Dim top headlight"       , "Przyciemn. refl. głównego"),
        ("eu07/dim_right"          , "Dim right headlight"     , "Przyciemn. refl. prawego"),
        ("eu07/cab_light"          , "Cab light"               , "Oświetlenie kabiny"),
        ("eu07/panto_front"        , "Front pantograph"        , "Pantograf przedni"),
        ("eu07/panto_rear"         , "Rear pantograph"         , "Pantograf tylny"),
        ("eu07/compressors"        , "Compressors"             , "Sprężarka"),
        ("eu07/converter"          , "Converter"               , "Przetwornica"),
        ("eu07/battery"            , "Traction battery"        , "Bateria trakcyjna"),
        ("eu07/horn"               , "Horn: low - high"        , "Syrena: niski - wysoki"),
        ("eu07/sanding"            , "Sanding"                 , "Piasecznice"),
        ("eu07/battery_110"        , "Battery"                 , "Bateria"),
        ("eu07/low_voltmeter"      , "Battery voltage"         , "Napięcie baterii"),
        ("eu07/lamp_fault"         , "Fault"                   , "Awaria"),
        ("eu07/fault_ack"          , "Acknowledge"             , "Potwierdzenie"),
        ("eu07/cupboard"           , "Cupboard"                , "Szafka"),
        ("eu07/gadget_cupboard"    , "Instrument cupboard"     , "Szafka przyrządów"),
        ("eu07/vigilance"          , "Vigilance"               , "Czuwak"),
        ("eu07/lamp_vigilance"     , "Vigilance"               , "Czuwak"),
        ("eu07/lamp_shp"           , "SHP"                     , "SHP"),
        ("eu07/cock_vigilance"     , "Vigilance brake cut-off" , "Kurek odcinający czuwaka"),
        ("eu07/hv_interlock"       , "High-voltage interlock"  , "Blokada WN"),
        ("eu07/hauled_run"         , "Hauled - no power"       , "Jazda ciągniona"),
        ("eu07/cab_cutout"         , "Cab cut out"             , "Kabina odłączona"),
        ("eu07/brake_cutout"       , "Brake valve cut-out"     , "Kurek odcinający zaworu"),
        ("eu07/cock_control_air"   , "Control air"             , "Powietrze sterujące"),
        ("eu07/cock_panto_air"     , "Pantograph air"          , "Powietrze pantografów"),
        ("eu07/cock_drain"         , "Drain"                   , "Odwodnienie"),
        ("eu07/cock_main_res"      , "Main reservoir"          , "Zbiornik główny"),
        ("eu07/isolator_door"      , "Motor isolators"         , "Odłączniki silników"),
        ("eu07/lv_door"            , "Low-voltage cabinet"     , "Szafa niskiego napięcia"),
        ("eu07/isolator_12"        , "Motor isolator 1-2"      , "Odłącznik silników 1-2"),
        ("eu07/isolator_34"        , "Motor isolator 3-4"      , "Odłącznik silników 3-4"),
        ("eu07/converter_knife"    , "Converter isolator"      , "Odłącznik przetwornicy"),
        ("eu07/panto_air"          , "Pantograph air"          , "Powietrze pantografów"),
        ("eu07/small_compressor"   , "Small compressor"        , "Sprężarka pomocnicza"),
        ("eu07/panto_cock"         , "Up: small compressor"    , "W górę: sprężarka pomocnicza"),
        ("eu07/room_light"         , "Machine room light"      , "Oświetlenie przedziału maszynowego"),
        ("eu07/cab_heating"        , "Cab heating"             , "Ogrzewanie kabiny"),
        ("eu07/br_fans"            , "Fans"                    , "Wentylatory"),
        ("eu07/br_converter"       , "Converter"               , "Przetwornica"),
        ("eu07/br_compressors"     , "Compressors"             , "Sprężarki"),
        ("eu07/br_cab_heating"     , "Cab heating"             , "Ogrzewanie kabiny"),
        ("eu07/br_pantographs"     , "Pantographs"             , "Pantografy"),
        ("eu07/br_sanding"         , "Sanding"                 , "Piasecznice"),
        ("eu07/br_control"         , "Control"                 , "Rozrząd"),
        ("eu07/br_windscreens"     , "Window heating"          , "Ogrzewanie szyb"),
        ("eu07/br_lighting"        , "Lighting"                , "Oświetlenie ogólne"),
        ("eu07/br_headlights"      , "Headlights"              , "Reflektory"),
        ("eu07/wiper_left"         , "Wiper, left"             , "Wycieraczka lewa"),
        ("eu07/wiper_right"        , "Wiper, right"            , "Wycieraczka prawa"),
        ("eu07/lamp_battery"       , "On battery"              , "Jazda z baterii"),
        ("eu07/battery_charge"     , "Battery charge"          , "Naładowanie baterii"),
        ("eu07/heating"            , "Train heating"           , "Ogrzewanie pociągu"),
        ("eu07/high_start"         , "High start"              , "Rozruch wysoki"),
        ("eu07/aux_reset"          , "Reset: conv./heating", "Odbl. przek. nadm. przetw. i ogrz."),
        ("eu07/comp_reset"         , "Reset: compressors"      , "Odbl. przek. nadm. sprężarki"),
        ("eu07/ws_off"             , "Main breaker OFF"        , "Wył. szybki wył."),
        ("eu07/ws_on"              , "Main breaker ON"         , "Wył. szybki zał."),
        ("eu07/relay_reset"        , "Reset: motors, ground fault", "Odbl. przek. nadm. i różnic."),
        ("eu07/line_contactors_off", "Line contactors off"     , "Styczniki liniowe wył."),
        ("eu07/slip_button"        , "Wheel slip"              , "Poślizg"),
        ("eu07/lamp_lsc"           , "Compressor overload"     , "Przek. nadmiar. sprężarki"),
        ("eu07/lamp_ls6"           , "Resistor fans"           , "Wentylat. oporów"),
        ("eu07/lamp_lss"           , "Main breaker"            , "Wyłącznik szybki"),
        ("eu07/lamp_ls8"           , "Motor overload"          , "Przek. nadmiar. silników trakc."),
        ("eu07/lamp_ls7"           , "Ground fault"            , "Przek. różnicowy"),
        // captions wrap to two lines; too long ends in "..."
        ("eu07/lamp_lspo"          , "Conv./heating overload"  , "Przek. nadm. przetw. i ogrzew."),
        ("eu07/lamp_ls10"          , "Line contactors open"    , "Styczniki liniowe wył."),
        ("eu07/lamp_ls9"           , "Wheel slip"              , "Poślizg"),
        ("eu07/lamp_ls4"           , "High start"              , "Syg. ciężkiego rozruchu"),
        ("eu07/lamp_ls5"           , "Running on resistors"    , "Jazda na oporach rozr."),
        ("eu07/lamp_lsgp"          , "Train heating"           , "Ogrzewanie pociągu"),
        ("eu07/ammeter_1"          , "Ammeter, motors 1-2"     , "Amperomierz WN"),
        ("eu07/ammeter_2"          , "Ammeter, motors 3-4"     , "Amperomierz WN"),
        ("eu07/voltmeter"          , "Line voltmeter"          , "Woltomierz WN"),
        ("eu07/controller"         , "Controller"              , "Nastawnik"),
        ("eu07/field_lever"        , "Field weakening"         , "Bocznikowanie"),
    };

    // docs/desk/design_desk.md (b8); the mod (eu07_driver.cs) reads them by these ids
    private static readonly string[] controls_v2 =
    {
        "fastBreakerOn", "fastBreakerOff", "relayReset", "auxRelayReset", "lineContactorsOff", "wheelSlip",
        "faultAck",    // fault lamp "seen it" button
        "vigilance",    // answers a vigilance gadget in the instrument cupboard
        "vigilanceCock",    // cock before that device's brake valve, cab back wall: 1 open (start), 0 shut
        "cupboardLeft", "cupboardMiddle", "cupboardRight",    // three plywood doors under the desk: 0 shut .. 1 open
        // HV BY HAND
        "hvInterlock",    // cab back wall cock: 1 = P (pantos may rise, HV shut away), 0 = open
        "hauledRun",    // back wall behind driver: 1 = jazda ciagniona (hauled, no power), 0 = train running
        "cabCutout",    // beside it: 1 = cab cut out (MU leader drives, shows its lamps)
        "motorIsolator12", "motorIsolator34",    // knife switch per motor pair, in a box in the cage: 1 = pulled, pair out
        "isolatorDoor",    // that box's door: 0 shut (start) .. 1 open
        "trainLightsFront", "trainLightsRear",    // no handle: leading vehicle's lamp selector (cable/radio, wire_simulation)
        "lvDoor",    // LV cabinet door over the converter knife switch, likewise
        "converterKnife",    // LV cabinet knife switch: 1 = pulled, converter can't start
        "instrumentLight",    // lamps over the meters
        "highStart", "converterSwitch", "compressorSwitch", "heatingSwitch", "fieldLever",
        "lightLeftA", "lightTopA", "lightRightA", "redLeftA", "redRightA", "lightDimLeftA", "lightDimTopA", "lightDimRightA",
        "lightLeftB", "lightTopB", "lightRightB", "redLeftB", "redRightB", "lightDimLeftB", "lightDimTopB", "lightDimRightB",
        "odometer",    // no handle: km run; mod writes, game saves
        "wiperLeftA", "wiperRightA", "wiperLeftB", "wiperRightB",    // valve per wiper: off, slow, fast
        "batterySwitch",    // last-mile battery switch
        "batteryCharge",    // no handle: its charge (0-1); mod writes, game saves
        // panto desk switches ASK; mod writes pantographSwitch if 110 V and panto breaker in (eu07_driver.cs)
        "pantographAsk", "pantograph2Ask",
        "cabHeatingSwitch",    // under the flap
        "lowBatteryCharge",    // 110 V battery charge (0-1), as above
        "smallCompressor",    // panto small compressor switch, LV board
        "pantoCock",    // pneumatic frame 3-way cock: 1 = pantos on the small compressor
        "controlAirCock", "pantoAirCock", "drainCock", "mainResCock",    // frame's labelled cocks: 1 = open
        "pantoAir",    // no handle: panto air (bar + 1); mod writes, game saves
        "roomLightA", "roomLightB",    // machine room lamps: switch by each passage door, either toggles
        // no handle: 48 contactors' soot + heat, 3 bits each, 6 per number; mod writes (eu07_driver.cs, mark_contactors)
        "contactorMarks0", "contactorMarks1", "contactorMarks2", "contactorMarks3", "contactorMarks4", "contactorMarks5",
        "contactorMarks6", "contactorMarks7", "contactorMarks8", "contactorMarks9", "contactorMarks10", "contactorMarks11",
        "contactorMarks12", "contactorMarks13", "contactorMarks14", "contactorMarks15",
    };
    // 110 V BREAKERS under the assistant's flap: DV fuse box fuses (saved, trip moves the lever)
    // order: far row then near row, from the side wall; DV's traction-motor fuse = "control"
    private static readonly (string fuse, string label)[] breakers_far =
    {
        ("fusebox.B_FANS", "eu07/br_fans"), ("fusebox.B_CONVERTER", "eu07/br_converter"), ("fusebox.B_COMPRESSORS", "eu07/br_compressors"),
        ("fusebox.B_CAB_HEATING", "eu07/br_cab_heating"), ("fusebox.B_PANTOGRAPHS", "eu07/br_pantographs"),
    };
    private static readonly (string fuse, string label)[] breakers_near =
    {
        ("fusebox.B_SANDING", "eu07/br_sanding"), ("fusebox.TM_POWER", "eu07/br_control"), ("fusebox.B_WINDSCREENS", "eu07/br_windscreens"),
        ("fusebox.B_LIGHTING", "eu07/br_lighting"), ("fusebox.B_HEADLIGHTS", "eu07/br_headlights"),
    };
    // no lever, set by the mod: FED = 110 V present + breaker in; power fuse for DV lights and sander
    private const string fed_headlights = "fusebox.FED_HEADLIGHTS", fed_lighting = "fusebox.FED_LIGHTING", fed_sanding = "fusebox.FED_SANDING";
    // power fuse for mounted gadgets (shape_vehicle): 110 V + control breaker in
    private const string fed_gadgets = "fusebox.FED_GADGETS";
    // switches and field lever saved, buttons not
    // trainLights...: DV sends selector only on move, so keep it over a load
    private static readonly string[] saved_v2 = { "trainLightsFront", "trainLightsRear", "highStart", "converterSwitch", "compressorSwitch", "heatingSwitch", "fieldLever",
        "lightLeftA", "lightTopA", "lightRightA", "redLeftA", "redRightA", "lightDimLeftA", "lightDimTopA", "lightDimRightA",
        "lightLeftB", "lightTopB", "lightRightB", "redLeftB", "redRightB", "lightDimLeftB", "lightDimTopB", "lightDimRightB", "odometer",
        "batterySwitch", "batteryCharge", "pantographAsk", "pantograph2Ask", "cabHeatingSwitch", "lowBatteryCharge", "pantoAir", "pantoCock", "controlAirCock", "pantoAirCock", "drainCock", "mainResCock", "roomLightA", "roomLightB",
        "hvInterlock", "hauledRun", "cabCutout", "motorIsolator12", "motorIsolator34", "converterKnife", "instrumentLight", "vigilanceCock",
        "contactorMarks0", "contactorMarks1", "contactorMarks2", "contactorMarks3", "contactorMarks4", "contactorMarks5",
        "contactorMarks6", "contactorMarks7", "contactorMarks8", "contactorMarks9", "contactorMarks10", "contactorMarks11",
        "contactorMarks12", "contactorMarks13", "contactorMarks14", "contactorMarks15" };
    // spawn cold: converter/compressor switches OFF; batteries FULL (also for older saves)
    private static readonly string[] on_at_start_v2 = { "batteryCharge", "lowBatteryCharge", "hvInterlock", "controlAirCock", "pantoAirCock", "mainResCock", "vigilanceCock" };

    private static readonly (string id, DVPortType type, DVPortValueType value)[] ports_v2 =
    {
        ("LAMP_LSGP", DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_BATTERY"  , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("BATTERY_CHARGE", DVPortType.READONLY_OUT, DVPortValueType.GENERIC),    // 0-100
        ("PANTO_AIR"     , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),    // bar
        ("LOW_VOLTS"     , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),    // 110 V bus
        ("LAMP_FAULT"    , DVPortType.READONLY_OUT, DVPortValueType.STATE),    // DV lamp codes: 0 off, 1 on, 3 blinking
        ("LAMP_VIGILANCE", DVPortType.READONLY_OUT, DVPortValueType.STATE),    // blinking while vigilance asks
        ("LAMP_SHP"      , DVPortType.READONLY_OUT, DVPortValueType.STATE),    // train stop lamp: unused (no SHP built)
        ("SAID_CELSIUS"  , DVPortType.READONLY_OUT, DVPortValueType.TEMPERATURE),    // told to other units: see shape_vehicle
        ("CAB_A_LIVE"    , DVPortType.READONLY_OUT, DVPortValueType.STATE),    // 1 = cab A drives (mod writes; cab_driving locks read)
        ("CAB_B_LIVE"    , DVPortType.READONLY_OUT, DVPortValueType.STATE),    // cab B likewise
    };

    // model runs at 0.5x volts, 1.5x amps (eu07_traction_circuit.cs): 600 A relays act at 900 model A
    // meters show model figures: voltmeter 0-2 kV (1.5 kV wire), ammeters 0-1200 A, red at 900 / 1125 (high start)
    private const float ammeter_scale = 1.0f, voltmeter_scale = 1.0f;

    // ASSUMED SIGN: if the wheel adds power anticlockwise in game, use Vector3.down
    private static readonly Vector3 controller_axis = Vector3.up;
    // field bar points left at 0, swings toward the driver: anticlockwise from above
    private static readonly Vector3 field_lever_axis = Vector3.down;

    private static Material black, white, grey_panel, desk_green, valve_green, teal, silver, red_cap, green_cap, chrome, bakelite, ivory, door_metal, desk_wood, yellow_cap, instrument_glow;

    private static void shape_desk_v2(GameObject root)
    {
        root.name = "EU07_interactables";
        make_materials();
        Transform old = child(root.transform, "ControlTemplate");
        // LAYOUT as the real desk, driver on the right: toggle panel, lamp/button panel, controller pedestal, brake valve
        // positions checked against MaSzyna's 303E cab (looked at, not copied); rows a little wider for 65 mm plates
        // handbrake wheels on the cab back walls, driver's side of the door; CCL dummy faces +z, replaced at load
        // cab doors: model's leaves, DV hinge at the rear edge, swing into the cab
        GameObject body_model = AssetDatabase.LoadAssetAtPath<GameObject>(body_fbx) ?? throw new IOException("no model at " + body_fbx);
        // hinges: foot of each leaf's rear edge (eu07_blockout.py); body turned half round, so model "B" = cab A
        make_door(root.transform, body_model, "Door_cab_B", "DoorCabA", new Vector3(-1.4487f, 1.690f,  5.287f));
        make_door(root.transform, body_model, "Door_cab_A", "DoorCabB", new Vector3( 1.4487f, 1.690f, -5.287f));
        // driver-side outside doors, behind the cab back wall into the machine room; hinged rear edge, swing in
        make_door(root.transform, body_model, "Door_side_B", "DoorSideA", new Vector3( 1.4515f, 1.690f,  4.700f), false, 88.0f);
        make_door(root.transform, body_model, "Door_side_A", "DoorSideB", new Vector3(-1.4515f, 1.690f, -4.700f), false, 88.0f);
        // cab-to-room doors: back wall, right of middle, hinged on the left edge, swing into the cab; "B" = cab A
        make_door(root.transform, body_model, "Door_room_B", "DoorRoomA", new Vector3(-0.296f, floor_top,  cab_back), true);
        make_door(root.transform, body_model, "Door_room_A", "DoorRoomB", new Vector3( 0.296f, floor_top, -cab_back), true);
        // opening side panes and sun visors
        foreach (string pane in new[] { "Window_cab_opening_A_L", "Window_cab_opening_A_R", "Window_cab_opening_B_L", "Window_cab_opening_B_R" })
            make_cab_window(root.transform, body_model, pane);
        foreach ((Vector3 from, Vector3 to) in visor_rails)
            make_visor(root.transform, from, to);
        make_contactors(root.transform);
        Transform[] handbrakes = root.transform.Cast<Transform>().Where(item => item.name == "[brake small]").ToArray();
        if (handbrakes.Length != 2)
            throw new InvalidDataException($"{handbrakes.Length} handbrake dummies in the desk prefab, expected 2");
        for (int index = 0; index < 2; ++index)
        {
            float end = index == 0 ? 1.0f : -1.0f;    // cab A, cab B
            handbrakes[index].localPosition = new Vector3(end * 0.95f, floor_top + 1.05f, end * (cab_back + 0.02f));
            handbrakes[index].localRotation = Quaternion.Euler(0.0f, end > 0.0f ? 0.0f : 180.0f, 0.0f);
        }
        Transform desk = node(root.transform, "DeskA", new Vector3(0.0f, floor_top, desk_z)).transform;
        // pedestal barely proud of the desk top; wheel rim 123 mm over its plate (reverser key + field lever under it)
        // right-hand group per MaSzyna's 303E (model/eu07_desk_parts.py); pedestal stands 9 cm proud of the desk front
        const float top_y = desk_top, ped_x = 0.765f, ped_z = 0.13f, ped_top = top_y + 0.012f, hub_y = ped_top + 0.123f, slope_z = 0.60f;

        // desk body, no colliders (they'd catch the mouse); cabinet with a well for the 110 V breakers (make_breaker_box)
        // cab narrows to the nose (eu07_hull.py half_width, 30 mm skin): each part runs to its own wall, never through
        const float cabinet_wall = 1.36f, stand_wall = 1.368f, ledge_wall = 1.39f, slope_wall = 1.33f;
        const float cab_x0 = -cabinet_wall, cab_x1 = 0.555f, cab_z0 = desk_front, cab_z1 = 0.66f;
        void cabinet(string name, float x0, float x1, float y0, float y1, float z0, float z1) => solid(desk, name, PrimitiveType.Cube, desk_green,
            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.identity, false);
        // CUPBOARDS UNDER THE DESK: carcass (back, floor, head, walls), three 45 cm hollows, doors swing out
        // driver's end = INSTRUMENT CUPBOARD: the only place DV gadgets may mount
        const float cup_y0 = 0.075f, cup_y1 = 0.615f, cup_back = 0.50f, rebate = 0.012f, body_top = top_y - well_deep;
        (string name, float x0, float x1, string port, string label)[] cupboards =
        {
            ("CupboardLeft", -1.24f, -0.72f, "cupboardLeft.EXT_IN", "eu07/cupboard"), ("CupboardMiddle", -0.66f, -0.14f, "cupboardMiddle.EXT_IN", "eu07/cupboard"),
            ("CupboardRight", -0.08f, 0.44f, "cupboardRight.EXT_IN", "eu07/gadget_cupboard"),
        };
        cabinet("CabinetBack" , cab_x0, cab_x1, 0.0f           , body_top        , cup_back, cab_z1);
        cabinet("CabinetFloor", cab_x0, cab_x1, 0.0f           , cup_y0 + rebate , cab_z0  , cup_back);
        cabinet("CabinetHead" , cab_x0, cab_x1, cup_y1 - rebate, body_top        , cab_z0  , cup_back);
        float wall_from = cab_x0;
        foreach ((string cup_name, float cup_x0, float cup_x1, string cup_port, string cup_label) in cupboards)
        {
            cabinet("CabinetWall" + cup_name, wall_from, cup_x0 + rebate, cup_y0 + rebate, cup_y1 - rebate, cab_z0, cup_back);
            wall_from = cup_x1 - rebate;
            make_cupboard_door(desk, cup_name, cup_x0, cup_x1, cup_y0, cup_y1, cab_z0, cup_port, cup_label);
        }
        cabinet("CabinetWallEnd", wall_from, cab_x1, cup_y0 + rebate, cup_y1 - rebate, cab_z0, cup_back);
        cabinet("CabinetLeft" , cab_x0     , well_x0    , top_y - well_deep, top_y            , cab_z0     , cab_z1);
        cabinet("CabinetRight", well_x1    , cab_x1     , top_y - well_deep, top_y            , cab_z0     , cab_z1);
        cabinet("CabinetNear" , well_x0    , well_x1    , top_y - well_deep, top_y            , cab_z0     , well_z0);
        cabinet("CabinetFar"  , well_x0    , well_x1    , top_y - well_deep, top_y            , well_z1    , cab_z1);
        // instrument cupboard: plywood back/floor/cheeks as REAL-SIZE meshes (DV builds mount colliders unscaled:
        // CustomizationPlacementMeshes), registered on the prefab root
        {
            float gx0 = cupboards[2].x0 + rebate, gx1 = cupboards[2].x1 - rebate, gy0 = cup_y0 + rebate, gy1 = cup_y1 - rebate, gz0 = cab_z0 + 0.020f, ply = 0.012f;
            MeshFilter[] gadget_boards =
            {
                shaped(desk, "GadgetBoardBack" , blocks("gadget_board_back" , (new Vector3(gx0, gy0, cup_back - ply), new Vector3(gx1, gy1, cup_back))), desk_wood, Vector3.zero).GetComponent<MeshFilter>(),
                shaped(desk, "GadgetBoardFloor", blocks("gadget_board_floor", (new Vector3(gx0, gy0, gz0), new Vector3(gx1, gy0 + ply, cup_back - ply))), desk_wood, Vector3.zero).GetComponent<MeshFilter>(),
                shaped(desk, "GadgetBoardLeft" , blocks("gadget_board_left" , (new Vector3(gx0, gy0 + ply, gz0), new Vector3(gx0 + ply, gy1, cup_back - ply))), desk_wood, Vector3.zero).GetComponent<MeshFilter>(),
                shaped(desk, "GadgetBoardRight", blocks("gadget_board_right", (new Vector3(gx1 - ply, gy0 + ply, gz0), new Vector3(gx1, gy1, cup_back - ply))), desk_wood, Vector3.zero).GetComponent<MeshFilter>(),
            };
            CustomizationPlacementMeshesProxy gadget_places = root.GetComponent<CustomizationPlacementMeshesProxy>();
            if (gadget_places == null)
                gadget_places = root.AddComponent<CustomizationPlacementMeshesProxy>();
            gadget_places.collisionMeshes = gadget_boards;
            gadget_places.drillDisableMeshes = new MeshFilter[0];
            gadget_places.generateFromTrainInteriorCols = false;
            EditorUtility.SetDirty(gadget_places);
        }
        // other two: a shelf each + crew kit
        foreach ((string cup_name, float cup_x0, float cup_x1, string cup_port, string cup_label) in cupboards.Take(2))
            solid(desk, cup_name + "Shelf", PrimitiveType.Cube, desk_wood, new Vector3((cup_x0 + cup_x1) * 0.5f, 0.345f, (cab_z0 + 0.03f + cup_back) * 0.5f),
                new Vector3(cup_x1 - cup_x0 - 2.0f * rebate, 0.012f, cup_back - cab_z0 - 0.03f), Quaternion.identity, false);
        {
            // left: rolled signal flags, hand lamp, books
            float kept_x = -0.98f, shelf_y = 0.351f, floor_y = cup_y0 + rebate;
            solid(desk, "FlagRed"   , PrimitiveType.Cylinder, red_cap, new Vector3(kept_x - 0.17f, shelf_y + 0.016f, 0.27f), new Vector3(0.032f, 0.19f, 0.032f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
            solid(desk, "FlagYellow", PrimitiveType.Cylinder, body_material("eu07_stripe_yellow"), new Vector3(kept_x - 0.12f, shelf_y + 0.016f, 0.27f), new Vector3(0.032f, 0.19f, 0.032f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
            solid(desk, "FlagSticks", PrimitiveType.Cube, desk_wood, new Vector3(kept_x - 0.145f, shelf_y + 0.012f, 0.13f), new Vector3(0.075f, 0.012f, 0.16f), Quaternion.identity, false);
            solid(desk, "HandLampBody", PrimitiveType.Cube, grey_panel, new Vector3(kept_x + 0.12f, shelf_y + 0.060f, 0.30f), new Vector3(0.10f, 0.12f, 0.07f), Quaternion.identity, false);
            solid(desk, "HandLampLens", PrimitiveType.Cylinder, ivory, new Vector3(kept_x + 0.12f, shelf_y + 0.070f, 0.262f), new Vector3(0.072f, 0.004f, 0.072f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
            solid(desk, "HandLampBail", PrimitiveType.Cube, chrome, new Vector3(kept_x + 0.12f, shelf_y + 0.130f, 0.30f), new Vector3(0.085f, 0.006f, 0.010f), Quaternion.identity, false);
            solid(desk, "BookLog"   , PrimitiveType.Cube, bakelite, new Vector3(kept_x - 0.05f, floor_y + 0.016f, 0.26f), new Vector3(0.22f, 0.032f, 0.30f), Quaternion.Euler(0.0f, 8.0f, 0.0f), false);
            solid(desk, "BookRules" , PrimitiveType.Cube, teal, new Vector3(kept_x - 0.03f, floor_y + 0.045f, 0.27f), new Vector3(0.16f, 0.026f, 0.23f), Quaternion.Euler(0.0f, -6.0f, 0.0f), false);
            solid(desk, "BookPages" , PrimitiveType.Cube, ivory, new Vector3(kept_x - 0.03f, floor_y + 0.045f, 0.268f), new Vector3(0.154f, 0.020f, 0.232f), Quaternion.Euler(0.0f, -6.0f, 0.0f), false);
        }
        {
            // middle: tool box, oil can, enamel mug and tin
            float kept_x = -0.40f, shelf_y = 0.351f, floor_y = cup_y0 + rebate;
            solid(desk, "ToolBox"      , PrimitiveType.Cube, valve_green, new Vector3(kept_x - 0.03f, floor_y + 0.075f, 0.28f), new Vector3(0.36f, 0.15f, 0.20f), Quaternion.identity, false);
            solid(desk, "ToolBoxLid"   , PrimitiveType.Cube, black, new Vector3(kept_x - 0.03f, floor_y + 0.151f, 0.28f), new Vector3(0.364f, 0.004f, 0.204f), Quaternion.identity, false);
            solid(desk, "ToolBoxHandle", PrimitiveType.Cube, chrome, new Vector3(kept_x - 0.03f, floor_y + 0.172f, 0.28f), new Vector3(0.12f, 0.008f, 0.012f), Quaternion.identity, false);
            solid(desk, "OilCan"       , PrimitiveType.Cylinder, silver, new Vector3(kept_x + 0.19f, floor_y + 0.060f, 0.33f), new Vector3(0.075f, 0.060f, 0.075f), Quaternion.identity, false);
            solid(desk, "OilCanSpout"  , PrimitiveType.Cylinder, silver, new Vector3(kept_x + 0.19f, floor_y + 0.150f, 0.30f), new Vector3(0.012f, 0.060f, 0.012f), Quaternion.Euler(-28.0f, 0.0f, 0.0f), false);
            solid(desk, "Mug"          , PrimitiveType.Cylinder, white, new Vector3(kept_x - 0.14f, shelf_y + 0.048f, 0.20f), new Vector3(0.080f, 0.042f, 0.080f), Quaternion.identity, false);
            solid(desk, "MugRim"       , PrimitiveType.Cylinder, teal, new Vector3(kept_x - 0.14f, shelf_y + 0.091f, 0.20f), new Vector3(0.084f, 0.002f, 0.084f), Quaternion.identity, false);
            solid(desk, "MugHandle"    , PrimitiveType.Cube, white, new Vector3(kept_x - 0.092f, shelf_y + 0.050f, 0.20f), new Vector3(0.022f, 0.050f, 0.010f), Quaternion.identity, false);
            solid(desk, "TeaTin"       , PrimitiveType.Cylinder, red_cap, new Vector3(kept_x + 0.02f, shelf_y + 0.066f, 0.26f), new Vector3(0.095f, 0.060f, 0.095f), Quaternion.identity, false);
            solid(desk, "TeaTinLid"    , PrimitiveType.Cylinder, silver, new Vector3(kept_x + 0.02f, shelf_y + 0.129f, 0.26f), new Vector3(0.098f, 0.004f, 0.098f), Quaternion.identity, false);
        }
        desk_part(desk, "Pedestal", placed("Pedestal", new Vector3(ped_x, 0.0f, ped_z)));
        solid(desk, "BrakeStand", PrimitiveType.Cube, desk_green, new Vector3((0.975f + stand_wall) * 0.5f, top_y * 0.5f, (desk_front + 0.48f) * 0.5f), new Vector3(stand_wall - 0.975f, top_y, 0.48f - desk_front), Quaternion.identity, false);
        // UNDER THE SLOPE: fill the slot behind pedestal (ends 0.465) and brake stand (0.48) to the slope foot (~0.515)
        // cabinet fills it on the other side, to 0.66
        const float pedestal_back = 0.465f, under_slope = 0.66f;
        solid(desk, "SlopeFootPedestal", PrimitiveType.Cube, desk_green, new Vector3((cab_x1 + 0.975f) * 0.5f, top_y * 0.5f, (pedestal_back + under_slope) * 0.5f),
            new Vector3(0.975f - cab_x1, top_y, under_slope - pedestal_back), Quaternion.identity, false);
        // 1.35, not the stand's 1.368: cab narrows toward the nose
        const float foot_wall = 1.35f;
        solid(desk, "SlopeFootStand", PrimitiveType.Cube, desk_green, new Vector3((0.975f + foot_wall) * 0.5f, top_y * 0.5f, (0.48f + under_slope) * 0.5f),
            new Vector3(foot_wall - 0.975f, top_y, under_slope - 0.48f), Quaternion.identity, false);
        // behind the slope, up to the windscreen sill; follows the bowed nose (eu07_hull.py front_y, 12 mm into the 30 mm skin)
        (float inner, float outer, float deep)[] sill_strips =
        {
            (0.00f, 0.30f, 1.042f), (0.30f, 0.50f, 1.016f), (0.50f, 0.70f, 0.988f), (0.70f, 0.80f, 0.961f), (0.80f, 0.90f, 0.935f), (0.90f, 0.98f, 0.914f),
            (0.98f, 1.05f, 0.892f), (1.05f, 1.13f, 0.860f), (1.13f, 1.20f, 0.833f), (1.20f, 1.25f, 0.813f), (1.25f, 1.30f, 0.794f), (1.30f, 1.33f, 0.772f),
        };
        // one piece: far edge as chords between strip corners (convex nose: chords stay inside the skin)
        {
            var far_edge = new List<Vector2>();
            foreach ((float strip_from, float strip_to, float strip_far) in sill_strips.Reverse())
                far_edge.Add(new Vector2(-strip_to, strip_far));
            foreach ((float strip_from, float strip_to, float strip_far) in sill_strips)
                far_edge.Add(new Vector2(strip_to, strip_far));
            float near_z = 0.68f, half_high = 0.06f, edge_x = sill_strips[sill_strips.Length - 1].outer;
            var loop = new List<Vector2> { new Vector2(-edge_x, near_z) };
            loop.AddRange(far_edge);
            loop.Add(new Vector2(edge_x, near_z));
            List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
            List<int> triangles = new List<int>();
            // top/bottom fanned from a point just inside the near edge (sees the whole outline)
            Vector2 hub = new Vector2(0.0f, near_z + 0.01f);
            foreach (float side in new[] { -1.0f, 1.0f })
                for (int index = 0; index + 1 < loop.Count; ++index)
                    flat_face(points, normals, triangles, new Vector3(0.0f, side, 0.0f), new[] {
                        new Vector3(hub.x, side * half_high, hub.y), new Vector3(loop[index].x, side * half_high, loop[index].y), new Vector3(loop[index + 1].x, side * half_high, loop[index + 1].y) });
            foreach (float side in new[] { -1.0f, 1.0f })    // hub's two slivers along the near edge
                flat_face(points, normals, triangles, new Vector3(0.0f, side, 0.0f), new[] {
                    new Vector3(hub.x, side * half_high, hub.y), new Vector3(edge_x, side * half_high, near_z), new Vector3(-edge_x, side * half_high, near_z) });
            for (int index = 0; index < loop.Count; ++index)
            {
                Vector2 from = loop[index], to = loop[(index + 1) % loop.Count];
                Vector2 out_of = new Vector2(to.y - from.y, from.x - to.x).normalized;
                if (Vector2.Dot(out_of, (from + to) * 0.5f - hub) < 0.0f)
                    out_of = -out_of;
                flat_face(points, normals, triangles, new Vector3(out_of.x, 0.0f, out_of.y), new[] {
                    new Vector3(from.x, -half_high, from.y), new Vector3(from.x, half_high, from.y), new Vector3(to.x, half_high, to.y), new Vector3(to.x, -half_high, to.y) });
            }
            shaped(desk, "Sill", keep_shape("desk_sill", points, normals, triangles), desk_green, new Vector3(0.0f, top_y + half_high, 0.0f));
        }
        Transform top      = panel(desk, "PanelTop", new Vector3(0.0f, top_y, 0.0f), 90.0f);
        Transform slope    = panel(desk, "PanelSlope", new Vector3(0.0f, top_y + 0.085f, slope_z), 45.0f);
        Transform pedestal = panel(desk, "PanelPedestal", new Vector3(ped_x, ped_top + 0.002f, ped_z), 90.0f);
        solid(slope, "Face", PrimitiveType.Cube, desk_green, new Vector3(0.0f, 0.0f, 0.06f), new Vector3(2.0f * slope_wall, 0.24f, 0.12f), Quaternion.identity, false);
        // two bare metal mounting plates; lamp plate as the reference: 786 mm, corner cut for the vigilance button
        solid(top, "PlateLamps", PrimitiveType.Cube, silver, new Vector3(0.162f, 0.376f, 0.0005f), new Vector3(0.786f, 0.264f, 0.003f), Quaternion.identity, false);
        solid(top, "PlateLampsNear", PrimitiveType.Cube, silver, new Vector3(0.1015f, 0.157f, 0.0005f), new Vector3(0.665f, 0.174f, 0.003f), Quaternion.identity, false);

        // of his controls only the brake cut-out and two fuse switches stay, moved
        // REVERSER = flat KEY under the rim; built right = neutral; 0 reverse, 1 forward (assumed), 25 deg each way
        string[] directions = child(old, "Reverser").GetComponentInChildren<NotchedControlCustomNamesProxy>().customNames;
        Vector3 key_at = placed("ReverserKey", new Vector3(ped_x + 0.0475f, ped_top + 0.038f, ped_z + 0.128f));
        desk_part(desk, "Boss", placed("Boss", new Vector3(key_at.x, ped_top, key_at.z)));
        GameObject reverser = make_lever(desk, "Reverser", key_at,
            new Vector3(0.095f, 0.0f, 0.0f), new Vector3(0.15f, 0.012f, 0.030f), new Vector3(0.15f, 0.0f, 0.0f),
            Vector3.down, -25.0f, 25.0f, 3, "reverser.CONTROL_EXT_IN", "ReverserIncremental", "ReverserAbsolute", directions, null, 0.0f, true);
        desk_part(reverser.transform, "ReverserKey", Vector3.zero);

        // DRIVER'S BRAKE VALVE, Oerlikon FV4a, right of pedestal; handle swings flat 27 mm over the rim (passes over the wheel)
        // running = ahead, 25 deg left; toward the driver brakes; pointing back = emergency
        Vector3 valve = placed("BrakeValve", new Vector3(ped_x + 0.331f, top_y, ped_z + 0.039f));
        desk_part(desk, "BrakeValve", valve);
        desk_part(desk, "BrakeValveCap", placed("BrakeValveCap", valve));
        GameObject brake = make_lever(desk, "TrainBrake", placed("BrakeHandle", valve + new Vector3(0.0f, 0.200f, 0.0f)),
            new Vector3(-0.11f, 0.010f, 0.0f), new Vector3(0.22f, 0.045f, 0.045f), new Vector3(-0.19f, 0.020f, 0.0f),
            Vector3.down, 0.0f, 158.0f, 11, "brake.EXT_IN", "BrakeIncremental", "BrakeAbsolute", null, null, 0.09f, true);
        desk_part(brake.transform, "BrakeHandle", Vector3.zero);
        brake.transform.parent.localRotation = Quaternion.Euler(0.0f, 65.0f, 0.0f);    // built pointing left; turned to ahead-left

        // INDEPENDENT BRAKE, FD1, on the side wall by the driver's right hand (model/eu07_desk_parts.py)
        // released = straight ahead; round left and back applies; red hub turns with the handle
        Vector3 direct = new Vector3(1.30f, top_y, -0.242f);
        Vector3 direct_pivot = direct + new Vector3(0.0f, -0.026f, 0.0f);
        foreach (string fd1_part in new[] { "FD1Bracket", "FD1Body", "FD1Pipe" })
            desk_part(desk, fd1_part, placed(fd1_part, direct_pivot));
        GameObject direct_brake = make_lever(desk, "IndependentBrake", placed("FD1Handle", direct_pivot),
            new Vector3(-0.07f, 0.0f, 0.0f), new Vector3(0.142f, 0.016f, 0.022f), new Vector3(-0.12f, 0.0f, 0.0f),
            Vector3.down, 0.0f, 170.0f, 11, "indBrake.EXT_IN", "IndependentBrakeIncremental", "IndependentBrakeAbsolute", null, null, 0.08f, true);
        desk_part(direct_brake.transform, "FD1Handle", Vector3.zero);
        direct_brake.transform.parent.localRotation = Quaternion.Euler(0.0f, 90.0f, 0.0f);    // built pointing left; turned to straight ahead

        // brake cut-out: COCK in the brake pipe (left of two pipes from the valve's back, 4.5 cm over the stand)
        // brass body, red handle; along the pipe = cut in
        Material pipe_steel = body_material("eu07_steel"), cock_brass = body_material("eu07_brass");
        const float pipe_up = 0.045f, cock_z = 0.335f;
        foreach ((string pipe, float pipe_x, float pipe_ends) in new[] { ("Brake", valve.x - 0.036f, 0.415f), ("Feed", valve.x + 0.039f, 0.445f) })
        {
            float from = valve.z + 0.07f;
            solid(desk, "Pipe" + pipe, PrimitiveType.Cylinder, pipe_steel, new Vector3(pipe_x, top_y + pipe_up, (from + pipe_ends) * 0.5f), new Vector3(0.022f, (pipe_ends - from) * 0.5f, 0.022f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
            solid(desk, "Pipe" + pipe + "Elbow", PrimitiveType.Sphere, pipe_steel, new Vector3(pipe_x, top_y + pipe_up, pipe_ends), Vector3.one * 0.024f, Quaternion.identity, false);
            solid(desk, "Pipe" + pipe + "Down", PrimitiveType.Cylinder, pipe_steel, new Vector3(pipe_x, top_y + pipe_up * 0.5f, pipe_ends), new Vector3(0.022f, pipe_up * 0.5f, 0.022f), Quaternion.identity, false);
            solid(desk, "Pipe" + pipe + "Flange", PrimitiveType.Cylinder, pipe_steel, new Vector3(pipe_x, top_y + 0.004f, pipe_ends), new Vector3(0.052f, 0.004f, 0.052f), Quaternion.identity, false);
            solid(desk, "Pipe" + pipe + "Union", PrimitiveType.Cylinder, cock_brass, new Vector3(pipe_x, top_y + pipe_up, from + 0.022f), new Vector3(0.032f, 0.012f, 0.032f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        }
        float cock_x = valve.x - 0.036f;
        solid(desk, "CutoutBody", PrimitiveType.Cylinder, cock_brass, new Vector3(cock_x, top_y + pipe_up, cock_z), new Vector3(0.042f, 0.030f, 0.042f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(desk, "CutoutSpindle", PrimitiveType.Cylinder, cock_brass, new Vector3(cock_x, top_y + pipe_up + 0.030f, cock_z), new Vector3(0.018f, 0.012f, 0.018f), Quaternion.identity, false);
        // own panel on the stand top, x along the pipe (make_line_cock's handle lies along x when open)
        Transform cutout_panel = node(top, "CutoutPanel", new Vector3(cock_x, cock_z, 0.0f)).transform;
        cutout_panel.localRotation = Quaternion.Euler(0.0f, 0.0f, 90.0f);
        make_line_cock(cutout_panel, "BrakeCutout", Vector2.zero, pipe_up + 0.040f, "brakeCutout.EXT_IN", null);
        make_label(top, new Vector3(cock_x + 0.155f, cock_z, -0.003f), "eu07/brake_cutout", control_caption);
        make_breaker_box(desk, top, old);
        // SMALL COMPRESSOR (eu07_low_voltage.cs, pantograph_air): tumbler on the LV cabinet's front post, cab end, 1.50 up
        // (reference: 1.52); gauge on the pneumatic frame board, passage end
        Transform low_board = node(root.transform, "SmallCompressorSwitch", new Vector3(0.3595f, floor_top + 1.50f, 3.6525f)).transform;
        low_board.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);    // faces the passage
        make_toggle(low_board, "SmallCompressor", Vector2.zero, "smallCompressor.EXT_IN", "eu07/small_compressor", null);
        // HV BY HAND: converter KNIFE SWITCH in the LV cabinet (pulled = converter can't start)
        // MOTOR ISOLATORS (cab A end, cage) locked as the real door (wire_simulation); INTERLOCK COCK 0/P on cab back wall
        Transform knife_wall = node(root.transform, "ConverterKnifeBoard", new Vector3(0.45f, floor_top + 1.35f, 3.646f)).transform;
        make_knife(knife_wall, "ConverterKnife", Vector2.zero, true, 0.100f, new[] { 0.07f, 0.0f, -0.07f }, 55.0f, false, false,
            "converterKnife.EXT_IN", "eu07/converter_knife", new Vector3(0.05f, -0.125f, -0.002f));
        // LV cabinet: model's two leaves; cab-side one opens (knife switch behind), 3.28-3.67 along, 0.88-1.78 up
        // hinged at cab end, to the post (compressor tumbler there); 92 deg swing clears the tumbler
        Vector2[] lv_paint = File.ReadAllLines(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_lv_leaf_uv.txt")))
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Split(' ')).Select(pair => new Vector2(float.Parse(pair[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        make_sheet_door(root.transform, "LvDoor", new Vector3(0.36f - 0.0005f, floor_top + 1.33f, 3.4575f), Quaternion.LookRotation(Vector3.right, Vector3.up),
            0.343f, 0.882f, "lvDoor.EXT_IN", "eu07/lv_door", false, 92.0f, lv_paint);
        // isolators in a box in the cage behind a door (eu07_blockout.py ISO_*: 40 x 36 cm, 21 deep)
        // switches on its back; pulled handle stands 18 cm off it
        const float passage = 0.36f, box_deep = 0.21f, box_wide = 0.40f, box_high = 0.36f, box_middle = 4.6825f;
        Transform cage_plate = node(root.transform, "MotorIsolators", new Vector3(-passage - box_deep + 0.0005f, floor_top + 1.30f, box_middle)).transform;
        cage_plate.localRotation = Quaternion.LookRotation(Vector3.left, Vector3.up);    // faces the passage; x toward the cab
        foreach (float base_x in new[] { -0.10f, 0.10f })
            solid(cage_plate, "IsolatorBase", PrimitiveType.Cube, bakelite, new Vector3(base_x, 0.01f, 0.0f), new Vector3(0.115f, 0.230f, 0.008f), Quaternion.identity, false);
        make_knife(cage_plate, "MotorIsolator12", new Vector2(-0.10f, -0.06f), false, 0.140f, new[] { -0.025f, 0.025f }, 60.0f, true, true,
            "motorIsolator12.EXT_IN", "eu07/isolator_12", new Vector3(0.0f, -0.050f, -0.002f));
        make_knife(cage_plate, "MotorIsolator34", new Vector2(0.10f, -0.06f), false, 0.140f, new[] { -0.025f, 0.025f }, 60.0f, true, true,
            "motorIsolator34.EXT_IN", "eu07/isolator_34", new Vector3(0.0f, -0.050f, -0.002f));
        make_isolator_door(root.transform, new Vector3(-passage + 0.001f, floor_top + 1.30f, box_middle), cage_plate.localRotation, box_wide, box_high);
        Transform back_wall = node(root.transform, "HvInterlockDial", new Vector3(0.62f, floor_top + 1.55f, cab_back + 0.0005f)).transform;
        back_wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);    // faces the cab
        make_cock(back_wall, "HvInterlock", Vector2.zero, "hvInterlock.EXT_IN", "eu07/hv_interlock", 0.075f);
        // VIGILANCE BRAKE CUT-OFF: cock before the device's valve, back wall 0.70 up
        // up = open (can brake); down = shut, lamp and buzzer only (eu07_driver.cs, watch_vigilance)
        Transform cock_wall = node(root.transform, "VigilanceCockWall", new Vector3(0.635f, floor_top + 0.70f, cab_back + 0.0005f)).transform;
        cock_wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);    // faces the cab
        make_cock(cock_wall, "VigilanceCock", Vector2.zero, "vigilanceCock.EXT_IN", "eu07/cock_vigilance");
        // TWO GUARDED SWITCHES, back wall behind the driver, 1.06 up (as the reference): lever between guards, yellow plate
        // red JAZDA CIAGNIONA: up = hauled (eu07_power_circuit.cs); teal CAB CUT OUT: up = MU follower (multiple_unit_step)
        void wall_switch(string name, float x, string port, string label, string notice, Material lever)
        {
            Transform wall = node(root.transform, name + "Switch", new Vector3(x, floor_top + 1.06f, cab_back + 0.0005f)).transform;
            wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);    // faces the cab
            const float box_out = 0.030f;
            solid(wall, "Box", PrimitiveType.Cube, bakelite, new Vector3(0.0f, -0.014f, -box_out * 0.5f), new Vector3(0.078f, 0.118f, box_out), Quaternion.identity, false);
            solid(wall, "Conduit", PrimitiveType.Cylinder, bakelite, new Vector3(0.0f, -0.555f, -0.011f), new Vector3(0.016f, 0.482f, 0.016f), Quaternion.identity, false);    // down the wall to the floor
            GameObject made = make_toggle(wall, name, Vector2.zero, port, label, null);
            made.transform.parent.localPosition += new Vector3(0.0f, 0.0f, -box_out);
            child(made.transform.parent, "Label").localPosition = new Vector3(0.0f, -0.050f, -0.002f);
            child(made.transform, "Tumbler").GetComponent<MeshRenderer>().sharedMaterial = lever;
            foreach (float guard_x in new[] { -0.019f, 0.019f })
                solid(wall, "Guard", PrimitiveType.Cube, chrome, new Vector3(guard_x, 0.0f, -box_out - 0.014f), new Vector3(0.004f, 0.058f, 0.028f), Quaternion.identity, false);
            decal(wall, notice, new Vector3(0.0f, 0.092f, -0.0012f));
        }
        wall_switch("HauledRun", 0.58f, "hauledRun.EXT_IN", "eu07/hauled_run", "hauled", red_cap);
        wall_switch("CabCutout", 0.69f, "cabCutout.EXT_IN", "eu07/cab_cutout", "cab_cutout", teal);
        Transform frame_board = node(root.transform, "PantographAirGauge", new Vector3(0.56f, floor_top + 1.245f, 1.55f + 0.016f)).transform;
        frame_board.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);    // faces cab A
        make_gauge(frame_board, "PantographAir", Vector2.zero, 0.0f, 10.0f, "port:eu07.PANTO_AIR", 1.0f, "eu07/panto_air", 135.0f, 0.3f, 0.066f, face: "bar10");
        // TWO GAUGES under the cocks, on the model's blank discs: left = panto/control air, right = main reservoir
        // control-air cock at x 1.12, main res at 0.74
        foreach ((string name, float x, float low, float high, string reader, string face, string label) in new[]
                 { ("ControlAirGauge", 1.03f, 0.0f, 10.0f, "port:eu07.PANTO_AIR", "bar10", "eu07/cock_control_air"), ("MainReservoirGauge", 0.84f, 1.0f, 17.0f, "reservoir", "bar16", "car/main_res") })
        {
            Transform hung = node(root.transform, name + "Board", new Vector3(x, floor_top + 0.47f, 1.55f + 0.051f)).transform;
            hung.localRotation = frame_board.localRotation;
            make_gauge(hung, name, Vector2.zero, low, high, reader, 1.0f, label, 135.0f, 0.3f, 0.092f, face: face);
        }
        // THREE-WAY COCK under the gauge: 8.5 cm lever, 28 deg each way about 0.905 (clear band 0.845-0.965)
        // 8 cm clear of the compressor delivery pipe; DOWN = main res (0, spawn), UP = small compressor
        make_cock(frame_board, "PantographCock", new Vector2(-0.04f, -0.340f), "pantoCock.EXT_IN", "eu07/panto_cock");
        // FRAME'S LABELLED COCKS: handles on the model's bodies; along pipe = open, across = shut
        // pipe axis 34 mm off the board, handle plane 40 mm further; the mod does the rest (pantograph_air)
        make_line_cock(frame_board, "PantographAirCock", new Vector2(-0.58f, -0.225f), 0.074f, "pantoAirCock.EXT_IN", "eu07/cock_panto_air");
        make_line_cock(frame_board, "MainReservoirCock", new Vector2(-0.18f, -0.545f), 0.074f, "mainResCock.EXT_IN", "eu07/cock_main_res");
        make_line_cock(frame_board, "DrainCock", new Vector2(-0.37f, -0.545f), 0.074f, "drainCock.EXT_IN", "eu07/cock_drain");
        make_line_cock(frame_board, "ControlAirCock", new Vector2(-0.56f, -0.545f), 0.074f, "controlAirCock.EXT_IN", "eu07/cock_control_air");
        // room lamp switches: black box, room side of each cab back wall by the door, 1.13 up; two-way
        foreach ((string cab, float end) in new[] { ("A", 1.0f), ("B", -1.0f) })
        {
            Transform wall = node(root.transform, "RoomLightSwitch" + cab, new Vector3(end * 0.50f, floor_top + 1.13f, end * (cab_back - 0.030f))).transform;
            wall.localRotation = Quaternion.LookRotation(end * Vector3.forward, Vector3.up);    // faces the room
            const float box_out = 0.024f;
            shaped(wall, "Box", turned("room_switch_box", 20, false, (0.034f, 0.0f), (0.034f, box_out - 0.004f), (0.030f, box_out), (0.0f, box_out)), bakelite, Vector3.zero);
            GameObject lamp_switch = make_toggle(wall, "RoomLight" + cab, Vector2.zero, $"roomLight{cab}.EXT_IN", "eu07/room_light", null);
            lamp_switch.transform.parent.localPosition += new Vector3(0.0f, 0.0f, -box_out);
            child(lamp_switch.transform.parent, "Label").localPosition = new Vector3(0.0f, -0.052f, box_out - 0.002f);
        }
        // SANDING: switch on the driver's plate, corner nearest the right hand, under the dimmers
        // (real: pedal + assistant's switch); railcar's sander does the rest
        make_toggle(top, "Sanding", new Vector2(0.375f, 0.151f), "sander.CONTROL_EXT_IN", "eu07/sanding", "SandToggle");
        // WIPER VALVES on the sill under each pane (reference: 0.725 off middle, 0.76 in, ~1 m up); off/slow/fast
        // DV wiper keys work the driver's
        Transform sill_top = panel(desk, "PanelSill", new Vector3(0.0f, top_y + 0.12f, 0.0f), 90.0f);
        foreach ((string side, float across) in new[] { ("Left", -0.725f), ("Right", 0.725f) })
        {
            Vector3 valve_at = new Vector3(across, top_y + 0.12f, 0.756f);
            solid(desk, "WiperValveBody", PrimitiveType.Cylinder, black, valve_at + new Vector3(0.0f, 0.028f, 0.0f), new Vector3(0.044f, 0.028f, 0.044f), Quaternion.identity, false);
            GameObject wiper_valve = make_lever(desk, "Wiper" + side, valve_at + new Vector3(0.0f, 0.072f, 0.0f),
                new Vector3(-0.042f, 0.0f, 0.0f), new Vector3(0.105f, 0.006f, 0.018f), new Vector3(-0.085f, 0.0f, 0.0f),
                Vector3.down, 0.0f, 70.0f, 3, $"wiper{side}A.EXT_IN", "WiperIncremental", "WiperAbsolute", null, chrome, 0.0f);
            if (side == "Left")
                no_keys(wiper_valve.transform);
            make_label(sill_top, new Vector3(across, 0.756f - 0.050f, -0.001f), side == "Left" ? "eu07/wiper_left" : "eu07/wiper_right", control_caption);
        }
        // HORN valve under the desk front left of the pedestal (ref: 0.370 right of middle, 76 mm down, 75 mm long)
        // left = low, right = high, springs back
        Transform front = panel(desk, "PanelFront", new Vector3(0.0f, 0.0f, desk_front), 0.0f);
        make_horn(front, new Vector2(0.370f, top_y - 0.076f));
        // rest of his desk goes: throttle, both panto switches, gauges, box
        UnityEngine.Object.DestroyImmediate(old.gameObject);

        // LAMP/BUTTON PLATE left of the controller, as the reference (reference/maszyna/kabina_a_parts.txt)
        // rows y: lamps 0.384, buttons 0.305, near lamps 0.230, near edge 0.181/0.151; ref spares left bare
        const float row_lamps = 0.384f, row_buttons = 0.305f, row_near = 0.230f, row_switches = 0.371f, row_switches_near = 0.296f;
        // all switches on the main plate, slope = instruments only
        // LIGHTS block, near left: 2 rows of 5 (lamps L/T/R + tails; dimmers under whites, cab light, instruments)
        float[] light_columns = { -0.200f, -0.145f, -0.090f, -0.035f, 0.020f };
        const float row_lights = 0.215f, row_dimming = 0.135f;
        (string name, string label)[] lights_far = { ("lightLeft", "eu07/light_left"), ("lightTop", "eu07/light_top"), ("lightRight", "eu07/light_right"), ("redLeft", "eu07/red_left"), ("redRight", "eu07/red_right") };
        (string name, string label)[] lights_near = { ("lightDimLeft", "eu07/dim_left"), ("lightDimTop", "eu07/dim_top"), ("lightDimRight", "eu07/dim_right") };
        for (int index = 0; index < lights_far.Length; ++index)
            make_toggle(top, char.ToUpper(lights_far[index].name[0]) + lights_far[index].name.Substring(1) + "A", new Vector2(light_columns[index], row_lights), lights_far[index].name + "A.EXT_IN", lights_far[index].label, null);
        for (int index = 0; index < lights_near.Length; ++index)
            make_toggle(top, char.ToUpper(lights_near[index].name[0]) + lights_near[index].name.Substring(1) + "A", new Vector2(light_columns[index], row_dimming), lights_near[index].name + "A.EXT_IN", lights_near[index].label, null);
        make_toggle(top, "CabLight", new Vector2(light_columns[3], row_dimming), "cabLight.EXT_IN", "eu07/cab_light", "CabLightToggle");
        // INSTRUMENT LAMPS: hooded lamp over each meter (pair); EU07InstrumentLights, mod switches them
        make_toggle(top, "InstrumentLight", new Vector2(light_columns[4], row_dimming), "instrumentLight.EXT_IN", "car/dash_light", null);
        GameObject meter_lamps = node(slope, "EU07InstrumentLights", Vector3.zero);
        foreach (float lamp_x in new[] { meter_x[0], (meter_x[1] + meter_x[2]) * 0.5f, (meter_x[3] + meter_x[4]) * 0.5f, meter_x[5], 0.75f, 0.92f, 1.09f })
        {
            bool big = lamp_x > 0.7f;    // brake gauges 130 mm, meters 80
            float above = big ? 0.105f : 0.085f;
            solid(slope, "InstrumentLampHood", PrimitiveType.Cube, black, new Vector3(lamp_x, above, -0.022f), new Vector3(big ? 0.070f : 0.090f, 0.012f, 0.040f), Quaternion.identity, false);
            GameObject lamp = node(meter_lamps.transform, "Lamp", new Vector3(lamp_x, above - 0.008f, -0.040f));
            lamp.transform.localRotation = Quaternion.LookRotation(new Vector3(0.0f, -above, 0.035f));
            Light bulb = lamp.AddComponent<Light>();
            bulb.type = LightType.Spot;
            bulb.spotAngle = 100.0f;
            bulb.color = new Color(1.0f, 0.86f, 0.62f);
            bulb.range = 0.30f;
            bulb.intensity = 1.4f;
            bulb.shadows = LightShadows.None;
            // festoon bulb 6 x 20 mm under the hood (in the lamps' object, lit with them) + lip on the hood's front edge
            solid(meter_lamps.transform, "InstrumentLampBulb", PrimitiveType.Capsule, instrument_glow, new Vector3(lamp_x, above - 0.0095f, -0.024f),
                new Vector3(0.006f, 0.010f, 0.006f), Quaternion.Euler(0.0f, 0.0f, 90.0f), false);
            solid(slope, "InstrumentLampLip", PrimitiveType.Cube, black, new Vector3(lamp_x, above - 0.0125f, -0.0415f),
                new Vector3(big ? 0.070f : 0.090f, 0.013f, 0.001f), Quaternion.identity, false);
        }
        meter_lamps.SetActive(false);
        // dimming: one switch per lamp, in the lights block under each lamp's switch
        Color red = new Color(0.25f, 0.03f, 0.03f), green = new Color(0.03f, 0.20f, 0.05f), blue = new Color(0.04f, 0.06f, 0.25f),
              orange = new Color(0.30f, 0.12f, 0.02f), clear = new Color(0.5f, 0.5f, 0.5f);
        // lamp row: two, gap, main breaker, gap, five
        make_lamp(top, "LampLSC" , new Vector2(0.031f, row_lamps), red   , "eu07.LAMP_LSC" , "eu07/lamp_lsc");
        make_lamp(top, "LampLS6" , new Vector2(0.090f, row_lamps), red   , "eu07.LAMP_LS6" , "eu07/lamp_ls6");
        make_lamp(top, "LampLSS" , new Vector2(0.177f, row_lamps), green , "eu07.LAMP_LSS" , "eu07/lamp_lss");
        make_lamp(top, "LampLS8" , new Vector2(0.265f, row_lamps), red   , "eu07.LAMP_LS8" , "eu07/lamp_ls8");
        make_lamp(top, "LampLS7" , new Vector2(0.325f, row_lamps), red   , "eu07.LAMP_LS7" , "eu07/lamp_ls7");
        make_lamp(top, "LampLSPO", new Vector2(0.383f, row_lamps), red   , "eu07.LAMP_LSPO", "eu07/lamp_lspo");
        make_lamp(top, "LampLS10", new Vector2(0.440f, row_lamps), blue  , "eu07.LAMP_LS10", "eu07/lamp_ls10");
        make_lamp(top, "LampLS9" , new Vector2(0.497f, row_lamps), clear , "eu07.LAMP_LS9" , "eu07/lamp_ls9");
        // buttons under/beside their lamps; compressor reset feeds the converter's control (one reset for all aux)
        // main breaker off and slip: mushroom buttons
        make_button(top, "CompressorReset"  , new Vector2(0.061f, row_buttons), green_cap, "auxRelayReset.EXT_IN"    , "eu07/comp_reset"         , null);
        make_button(top, "FastBreakerOff"   , new Vector2(0.145f, row_buttons), red_cap  , "fastBreakerOff.EXT_IN"   , "eu07/ws_off"             , "FuelCutoff", true);
        make_button(top, "FastBreakerOn"    , new Vector2(0.205f, row_buttons), green_cap, "fastBreakerOn.EXT_IN"    , "eu07/ws_on"              , "Starter");
        make_button(top, "RelayReset"       , new Vector2(0.289f, row_buttons), green_cap, "relayReset.EXT_IN"       , "eu07/relay_reset"        , "StarterFuse");
        make_button(top, "AuxRelayReset"    , new Vector2(0.376f, row_buttons), green_cap, "auxRelayReset.EXT_IN"    , "eu07/aux_reset"          , null);
        make_button(top, "LineContactorsOff", new Vector2(0.433f, row_buttons), red_cap  , "lineContactorsOff.EXT_IN", "eu07/line_contactors_off", null);
        make_button(top, "WheelSlip"        , new Vector2(0.492f, row_buttons), green_cap, "wheelSlip.EXT_IN"        , "eu07/slip_button"        , null, true);
        // near lamps; high start (rotary on the real desk) = toggle in the selector's place
        make_lamp(top, "LampLS4" , new Vector2(0.136f, row_near), red   , "eu07.LAMP_LS4" , "eu07/lamp_ls4");
        make_lamp(top, "LampLS5" , new Vector2(0.194f, row_near), orange, "eu07.LAMP_LS5" , "eu07/lamp_ls5");
        make_lamp(top, "LampLSGP", new Vector2(0.253f, row_near), blue  , "eu07.LAMP_LSGP", "eu07/lamp_lsgp");
        make_toggle(top, "HighStart", new Vector2(0.136f, 0.151f), "highStart.EXT_IN", "eu07/high_start", null);    // under its lamp, LS4
        // LAST-MILE BATTERY (not on the real desk): lamp at the near row's left end (lit on battery),
        // switch beside train heating's, charge meter on the slope
        make_lamp(top, "LampBattery", new Vector2(0.078f, row_near), new Color(0.26f, 0.21f, 0.02f), "eu07.LAMP_BATTERY", "eu07/lamp_battery");
        // left-end switches 50 mm apart; FRONT panto's in the window row, REAR in the driver row
        // pantograph2Switch drives [Pantograph2] (cab A end) = front from this desk
        make_toggle(top, "PantographFront", new Vector2(-0.124f, row_switches     ), "pantograph2Ask.EXT_IN"   , "eu07/panto_front", "Pantograph");
        make_toggle(top, "Compressors"    , new Vector2(-0.074f, row_switches     ), "compressorSwitch.EXT_IN" , "eu07/compressors", null);
        make_toggle(top, "Converter"      , new Vector2(-0.023f, row_switches     ), "converterSwitch.EXT_IN"  , "eu07/converter"  , null);
        make_toggle(top, "PantographRear" , new Vector2(-0.124f, row_switches_near), "pantographAsk.EXT_IN"    , "eu07/panto_rear" , null);
        make_toggle(top, "TrainHeating"   , new Vector2(-0.073f, row_switches_near), "heatingSwitch.EXT_IN"    , "eu07/heating"    , null);
        make_toggle(top, "Battery"        , new Vector2(-0.023f, row_switches_near), "batterySwitch.EXT_IN"    , "eu07/battery"    , null);

        // SPEED RECORDER on its own column at the window pillar, facing the driver (face: tools/dial_faces.py)
        // teal box on a stalk, two cover bolts + knob, cable; clock follows game time, 6 drums (red = 100 m)
        const float dial = 0.14f, dial_radius = dial * 0.5f;
        solid(desk, "RecorderDrive", PrimitiveType.Cylinder, teal, new Vector3(0.29f, top_y + 0.1675f, 0.80f), new Vector3(0.110f, 0.0475f, 0.110f), Quaternion.identity, false);
        solid(desk, "RecorderDriveFoot", PrimitiveType.Cylinder, black, new Vector3(0.29f, top_y + 0.127f, 0.80f), new Vector3(0.118f, 0.007f, 0.118f), Quaternion.identity, false);
        solid(desk, "RecorderGland", PrimitiveType.Cylinder, black, new Vector3(0.362f, top_y + 0.205f, 0.83f), new Vector3(0.022f, 0.012f, 0.022f), Quaternion.identity, false);
        solid(desk, "RecorderCable", PrimitiveType.Cylinder, black, new Vector3(0.362f, top_y + 0.160f, 0.83f), new Vector3(0.014f, 0.040f, 0.014f), Quaternion.identity, false);
        Transform column = panel(desk, "PanelColumn", new Vector3(0.29f, top_y + 0.40f, 0.74f), 0.0f);
        shaped(column, "RecorderBox", slab("recorder_box", (0.0f, 0.0f, 0.0f, -0.120f), (0.094f, 0.144f, 0.012f, -0.120f), (0.100f, 0.150f, 0.016f, -0.112f),
            (0.100f, 0.150f, 0.016f, -0.008f), (0.094f, 0.144f, 0.012f, 0.0f), (0.0f, 0.0f, 0.0f, 0.0f)), teal, new Vector3(0.0f, -0.035f, 0.0f));
        make_gauge(column, "Speedometer", Vector2.zero, 0.0f, 150.0f, "port:traction.WHEEL_SPEED_KMH_EXT_IN", 1.0f, null, 135.0f, 0.3f, dial, 7, "speed");
        make_label(column, new Vector3(0.0f, -0.150f, -0.004f), "car/speedometer");    // maker's plate position
        recorder_bolt(column, "RecorderBoltLeft", new Vector2(-0.070f, 0.093f), 30.0f);
        recorder_bolt(column, "RecorderBoltRight", new Vector2(0.070f, 0.093f), -30.0f);
        // dial backlight: face copy 0.2 mm over, glowing on white paper + faint front lamp
        // named as the meter lamps, so the mod switches them together
        GameObject recorder_lamps = node(column, "EU07InstrumentLights", Vector3.zero);
        GameObject backlit = node(recorder_lamps.transform, "RecorderBacklight", new Vector3(0.0f, 0.0f, -0.0062f));
        backlit.transform.localScale = new Vector3(dial, dial, 1.0f);
        backlit.AddComponent<MeshFilter>().sharedMesh = disc();
        backlit.AddComponent<MeshRenderer>().sharedMaterial = backlit_face("speed");
        Light dial_lamp = node(recorder_lamps.transform, "RecorderDialLamp", new Vector3(0.0f, 0.0f, -0.050f)).AddComponent<Light>();
        dial_lamp.type = LightType.Point;
        dial_lamp.color = new Color(1.0f, 0.86f, 0.62f);
        dial_lamp.range = 0.12f;
        dial_lamp.intensity = 0.5f;
        dial_lamp.shadows = LightShadows.None;
        recorder_lamps.SetActive(false);
        solid(column, "RecorderKnobFlange", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.097f, -0.002f), new Vector3(0.030f, 0.002f, 0.030f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderKnob", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.097f, -0.009f), new Vector3(0.016f, 0.007f, 0.016f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        foreach (float side in new[] { -0.0115f, 0.0115f })
            solid(column, "RecorderKnobScrew", PrimitiveType.Cylinder, chrome, new Vector3(side, 0.097f, -0.0045f), new Vector3(0.0045f, 0.0006f, 0.0045f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderLatch", PrimitiveType.Cube, black, new Vector3(0.0f, -0.100f, -0.004f), new Vector3(0.105f, 0.018f, 0.008f), Quaternion.identity, false);
        solid(column, "RecorderLatchPivot", PrimitiveType.Cylinder, black, new Vector3(-0.040f, -0.100f, -0.009f), new Vector3(0.012f, 0.002f, 0.012f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderKeyhole", PrimitiveType.Cylinder, chrome, new Vector3(0.036f, -0.100f, -0.0085f), new Vector3(0.009f, 0.0006f, 0.009f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        // clock hands: over the face, under the pointer
        Vector2 clock = new Vector2(0.0f, recorder_clock_up * dial_radius);
        recorder_hand(column, "RecorderClockHours", clock, recorder_clock_radius * dial_radius * 0.52f, 0.0016f, 12.0f, "eu07.CLOCK_HOURS", -0.0070f);
        recorder_hand(column, "RecorderClockMinutes", clock, recorder_clock_radius * dial_radius * 0.84f, 0.0011f, 60.0f, "eu07.CLOCK_MINUTES", -0.0078f);
        solid(column, "RecorderClockBoss", PrimitiveType.Cylinder, black, new Vector3(clock.x, clock.y, -0.0084f), new Vector3(0.0028f, 0.0003f, 0.0028f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        // counter: six drums 1.5 mm through the face, black frame
        float window_wide = recorder_counter_wide * dial_radius, window_high = recorder_counter_high * dial_radius;
        float drum_wide = window_wide / 6.0f, drum_radius = window_high * 10.0f / (2.0f * Mathf.PI), counter_y = -recorder_counter_down * dial_radius;
        for (int drum = 0; drum < 6; ++drum)    // drum 0 lowest, at the right
            recorder_drum(column, "RecorderDrum" + drum, new Vector3((2.5f - drum) * drum_wide, counter_y, -0.0075f + drum_radius), drum_wide - 0.0003f, drum_radius,
                "eu07.ODOMETER_" + drum, drum == 0 ? "odometer_red" : "odometer");
        foreach (float side in new[] { -1.0f, 1.0f })
        {
            solid(column, "RecorderCounterFrame", PrimitiveType.Cube, black, new Vector3(0.0f, counter_y + side * (window_high * 0.5f + 0.0011f), -0.0073f),
                new Vector3(window_wide + 0.004f, 0.0022f, 0.003f), Quaternion.identity, false);
            solid(column, "RecorderCounterFrame", PrimitiveType.Cube, black, new Vector3(side * (window_wide * 0.5f + 0.001f), counter_y, -0.0073f),
                new Vector3(0.002f, window_high + 0.0044f, 0.003f), Quaternion.identity, false);
        }
        // HV/LV METERS: round dials for the real edgewise ones, on a black bank (meter_x: 80 mm dials, 99 mm apart)
        // incl. 110 V voltmeter and last-mile battery charge
        solid(slope, "MeterBank", PrimitiveType.Cube, black, new Vector3(0.3985f, 0.0f, -0.0005f), new Vector3(0.512f, 0.16f, 0.003f), Quaternion.identity, false);
        make_gauge(slope, "VoltmeterNN", new Vector2(meter_x[2], 0.0f), 0.0f, 150.0f, "port:eu07.LOW_VOLTS", 1.0f, "eu07/low_voltmeter", 135.0f, 0.3f, meter_dial, face: "volts150");
        make_gauge(slope, "AmmeterWN1", new Vector2(meter_x[3], 0.0f), 0.0f, 1200.0f, "port:eu07.AMMETER_12", ammeter_scale, "eu07/ammeter_1", 135.0f, 0.3f, meter_dial, face: "amps");
        make_gauge(slope, "AmmeterWN2", new Vector2(meter_x[4], 0.0f), 0.0f, 1200.0f, "port:eu07.AMMETER_34", ammeter_scale, "eu07/ammeter_2", 135.0f, 0.3f, meter_dial, face: "amps");
        make_gauge(slope, "VoltmeterWN", new Vector2(meter_x[5], 0.0f), 0.0f, 2000.0f, "port:busBar.SUPPLY_VOLTAGE", voltmeter_scale, "eu07/voltmeter", 135.0f, 0.3f, meter_dial, face: "kilovolts");
        // last-mile battery meter, own surround
        make_gauge(slope, "BatteryCharge", new Vector2(meter_x[1], 0.0f), 0.0f, 100.0f, "port:eu07.BATTERY_CHARGE", 1.0f, "eu07/battery_charge", 135.0f, 0.3f, meter_dial, face: "charge");
        // DV'S OWN TWO: traction motor temp (tmHeat, red from 120 where DV trips) + one red fault lamp
        // lamp mirrors DV's two motor lamps (eu07_driver.cs): blink = breaker out / 105 C, steady = dead / 90 C; DE6 buzzes
        make_gauge(slope, "MotorTemperature", new Vector2(meter_x[0], 0.0f), 0.0f, 150.0f, "port:tmHeat.TEMPERATURE", 1.0f, "car/tm_temp", 135.0f, 0.3f, meter_dial, face: "celsius150");
        make_lamp(top, "LampFault", new Vector2(0.320f, row_near), red, "eu07.LAMP_FAULT", "eu07/lamp_fault");
        sounds(child(top, "LampFault").GetComponent<LampControlProxy>(), ("warningAudio", "DashWarning_02_Buzz"), ("blinkStateBuzzingLoopAudio", "DashWarning_07_BuzzContinuous"));
        // FAULT ACK: blink -> steady, buzz stops until a NEW fault (eu07_driver.cs)
        make_button(top, "FaultAcknowledge", new Vector2(0.385f, row_near), red_cap, "faultAck.EXT_IN", "eu07/fault_ack", null);
        // VIGILANCE BUTTON in the lamp plate's cut corner: answers a vigilance gadget in the cupboard (eu07_driver.cs)
        // lamp blinks + buzz while asked; no device = dead button
        make_button(top, "Vigilance", new Vector2(0.495f, 0.150f), yellow_cap, "vigilance.EXT_IN", "eu07/vigilance", null);
        // vigilance and train-stop lamps: in their own box on the sill (make_shp_box)
        make_shp_box(desk, new Vector3(0.497f, top_y + 0.12f, 0.772f), blue, red);
        // six meters in a row on one black plate
        solid(slope, "PlateMeters", PrimitiveType.Cube, bakelite, new Vector3(0.351f, 0.0f, -0.0005f), new Vector3(0.612f, 0.205f, 0.003f), Quaternion.identity, false);
        // DV pressures are absolute (1.0 = empty): 1..11 reads 0..10 bar
        make_gauge(slope, "BrakeCylinder", new Vector2(0.75f, 0.0f), 1.0f, 11.0f, "cylinder", 1.0f, "car/brake_cyl", 135.0f, 0.3f, 0.13f, face: "bar10");
        make_gauge(slope, "BrakePipe"    , new Vector2(0.92f, 0.0f), 1.0f, 11.0f, "pipe", 1.0f, "car/brake_pipe", 135.0f, 0.3f, 0.13f, face: "bar10");
        make_gauge(slope, "MainReservoir", new Vector2(1.09f, 0.0f), 1.0f, 17.0f, "reservoir", 1.0f, "car/main_res", 135.0f, 0.3f, 0.13f, face: "bar16");

        foreach (float gauge_x in new[] { 0.75f, 0.92f, 1.09f })    // brake gauges in round green flanges
            solid(slope, "GaugeFlange", PrimitiveType.Cylinder, valve_green, new Vector3(gauge_x, 0.0f, -0.0015f), new Vector3(0.158f, 0.001f, 0.158f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);

        // controller: 44-position wheel; ring pointer shows the contactors (may lag the wheel)
        make_controller(desk, placed("Wheel", new Vector3(ped_x, hub_y, ped_z)), "throttle.EXT_IN");
        make_gauge(pedestal, "Position", Vector2.zero, 0.0f, 43.0f, "port:eu07.CONTROLLER_POSITION", 1.0f, null,
            controller_sweep, 0.1f, 0.136f, eu07_positions, "positions", false);
        // FIELD LEVER: flat bar left of the hub over a sector plate; 0 = ahead 29 deg left; 15 deg per step toward driver
        Vector3 field_at = placed("FieldLever", new Vector3(ped_x - 0.061f, ped_top + 0.040f, ped_z + 0.128f));
        desk_part(desk, "FieldSector", placed("FieldSector", new Vector3(field_at.x, ped_top, field_at.z)));
        desk_part(desk, "Boss", new Vector3(field_at.x, ped_top, field_at.z));
        GameObject field = make_lever(desk, "FieldLever", field_at,
            new Vector3(-0.085f, 0.012f, 0.0f), new Vector3(0.13f, 0.035f, 0.026f), new Vector3(-0.14f, 0.025f, 0.0f),
            field_lever_axis, 0.0f, 90.0f, 7, "fieldLever.EXT_IN", "DynamicBrakeIncremental", "DynamicBrakeAbsolute", null, null, 0.0f, true);
        desk_part(field.transform, "FieldLever", Vector3.zero);
        field.transform.parent.localRotation = Quaternion.Euler(0.0f, 61.0f, 0.0f);    // built pointing left; turned to ahead-left
        // captions along the near edge: field lever, controller, reverser
        make_label(pedestal, new Vector3(-0.11f, -0.150f, -0.003f), "eu07/field_lever");
        make_label(pedestal, new Vector3(0.0f, -0.150f, -0.003f), "eu07/controller");
        make_label(pedestal, new Vector3(0.11f, -0.150f, -0.003f), "car/reverser");

        // HIGHLIGHT: without a HighlightTag DV outlines only the first renderer (GrabberHoverHighlight): list all
        foreach (ControlSpecProxy control in root.GetComponentsInChildren<ControlSpecProxy>(true))
        {
            Renderer[] pieces = control.GetComponentsInChildren<MeshRenderer>(true);
            if (pieces.Length > 1 && control.GetComponentInChildren<HighlightTagProxy>(true) == null)
                control.gameObject.AddComponent<HighlightTagProxy>().renderers = pieces.ToList();
        }
        // DESK SURFACES PAINTED (see skin and paint_skins)
        Color green_paint = desk_green.color, grey_paint = new Color(0.66f, 0.67f, 0.65f);
        skins.Clear();
        skin(top, "SkinTop", new Rect(cab_x0, cab_z0, cab_x1 - cab_x0, cab_z1 - cab_z0), 0.0004f, green_paint, 0.32f, false,
            new Rect(cab_x0, cab_z0, well_x0 - cab_x0, cab_z1 - cab_z0), new Rect(well_x1, cab_z0, cab_x1 - well_x1, cab_z1 - cab_z0),
            new Rect(well_x0, cab_z0, well_x1 - well_x0, well_z0 - cab_z0), new Rect(well_x0, well_z1, well_x1 - well_x0, cab_z1 - well_z1));
        skin(top, "SkinPlate", new Rect(-0.231f, 0.070f, 0.786f, 0.438f), 0.0014f, grey_paint, 0.30f, true,
            new Rect(-0.231f, 0.244f, 0.786f, 0.264f), new Rect(-0.231f, 0.070f, 0.665f, 0.174f));
        skin(slope, "SkinSlope", new Rect(-slope_wall, -0.12f, 2.0f * slope_wall, 0.24f), 0.0004f, green_paint, 0.32f, false);
        // front paint: carcass face only (rail below doors, rail above, stiles)
        List<Rect> front_pieces = new List<Rect>
        {
            new Rect(cab_x0, 0.0f, cab_x1 - cab_x0, cup_y0 + rebate), new Rect(cab_x0, cup_y1 - rebate, cab_x1 - cab_x0, top_y - (cup_y1 - rebate)),
        };
        float stile_from = cab_x0;
        foreach ((string cup_name, float cup_x0, float cup_x1, string cup_port, string cup_label) in cupboards)
        {
            front_pieces.Add(new Rect(stile_from, cup_y0 + rebate, cup_x0 + rebate - stile_from, cup_y1 - cup_y0 - 2.0f * rebate));
            stile_from = cup_x1 - rebate;
        }
        front_pieces.Add(new Rect(stile_from, cup_y0 + rebate, cab_x1 - stile_from, cup_y1 - cup_y0 - 2.0f * rebate));
        skin(front, "SkinFront", new Rect(cab_x0, 0.0f, cab_x1 - cab_x0, top_y), 0.0004f, green_paint, 0.32f, false, front_pieces.ToArray());
        skin(top, "SkinStandTop", new Rect(0.975f, desk_front, stand_wall - 0.975f, 0.48f - desk_front), 0.0004f, green_paint, 0.32f, false);
        skin(front, "SkinStandFront", new Rect(0.975f, 0.0f, stand_wall - 0.975f, top_y), 0.0004f, green_paint, 0.32f, false);
        paint_skins(root.transform);
        lay_finish(root.transform);

        // CAB B: cab A's desk turned about the vehicle centre; HV interlock, safety cut-outs, hauled run: cab A only
        // own driving controls (cab_driving), lamps, wipers, cupboards; breakers, battery, cab light, heating shared
        Transform desk_a = child(root.transform, "DeskA");
        foreach (InteractablePortFeederProxy feeder in desk_a.GetComponentsInChildren<InteractablePortFeederProxy>(true))
            foreach (var (control, canonical, _) in cab_driving)
                if (feeder.portId == canonical)
                    feeder.portId = control + "A.EXT_IN";
        foreach (string fitting in new[] { "DeskA", "CabCutoutSwitch" })
        {
            Transform in_a = child(root.transform, fitting);
            Transform in_b = UnityEngine.Object.Instantiate(in_a.gameObject, root.transform).transform;
            in_b.name = fitting == "DeskA" ? "DeskB" : fitting + "_B";
            in_b.localPosition = new Vector3(-in_a.localPosition.x, in_a.localPosition.y, -in_a.localPosition.z);
            in_b.localRotation = Quaternion.Euler(0.0f, 180.0f, 0.0f) * in_a.localRotation;
            in_b.localScale = in_a.localScale;
            foreach (InteractablePortFeederProxy feeder in in_b.GetComponentsInChildren<InteractablePortFeederProxy>(true))
            {
                string at_a = feeder.portId.EndsWith("A.EXT_IN") ? feeder.portId.Substring(0, feeder.portId.Length - "A.EXT_IN".Length) : null;
                if (at_a != null && (end_lamp_ports.Contains(at_a) || cab_driving.Any(one => one.control == at_a)))
                    feeder.portId = at_a + "B.EXT_IN";
                else if (cab_b_own.Any(own => feeder.portId == own + ".EXT_IN"))
                    feeder.portId = feeder.portId.Replace(".EXT_IN", "B.EXT_IN");
                else
                    continue;
                if (feeder.gameObject.name.EndsWith("A"))
                    feeder.gameObject.name = feeder.gameObject.name.Substring(0, feeder.gameObject.name.Length - 1) + "B";
            }
        }
        // all indoors, except the doors' outside paint and the glass
        int indoor_slots = 0;
        foreach (Renderer part in root.GetComponentsInChildren<Renderer>(true))
            foreach (Material worn in part.sharedMaterials)
            {
                occlude_indoors(worn);
                ++indoor_slots;
            }
        foreach (var (_, _, repainted) in inside_cans)    // plus the fresh can paint
            occlude_indoors(repainted);
        Debug.Log($"EU07 BUILD: the indoor materials occluded as the game's cabs are ({indoor_slots} renderer slots looked at)");
        // check: no desk feeds the other cab's controls or the loco's own driving controls
        foreach ((string desk_name, string own, string other) in new[] { ("DeskA", "A", "B"), ("DeskB", "B", "A") })
        {
            string[] fed = child(root.transform, desk_name).GetComponentsInChildren<InteractablePortFeederProxy>(true).Select(feeder => feeder.portId).ToArray();
            string[] wrong = fed.Where(port => cab_driving.Any(one => port == one.canonical || port == one.control + other + ".EXT_IN")
                || end_lamp_ports.Any(end => port == end + other + ".EXT_IN") || (own == "B" && cab_b_own.Any(mine => port == mine + ".EXT_IN"))).ToArray();
            int driving = fed.Count(port => cab_driving.Any(one => port == one.control + own + ".EXT_IN"));
            if (wrong.Length > 0 || driving < cab_driving.Length)
                throw new Exception($"EU07: {desk_name} feeds {string.Join(" ", wrong)}; {driving} of its {cab_driving.Length} driving controls are its own");
        }
        if (!child(root.transform, "CabCutoutSwitch_B").GetComponentsInChildren<InteractablePortFeederProxy>(true).Any(feeder => feeder.portId == "cabCutoutB.EXT_IN"))
            throw new Exception("EU07: cab B's cut-out switch is not its own");
        // item rest surfaces: each desk piece as it stands (desk_item_colliders)
        desk_item_boxes.Clear();
        foreach (string desk_name in new[] { "DeskA", "DeskB" })
        {
            Transform desk_root = child(root.transform, desk_name);
            foreach (MeshFilter piece in desk_root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (piece.sharedMesh == null || piece.GetComponent<MeshRenderer>() == null)
                    continue;
                bool moves = false;
                for (Transform up = piece.transform; up != null && up != desk_root; up = up.parent)
                    moves |= up.name.StartsWith("C_");
                if (moves)
                    continue;
                Bounds own = piece.sharedMesh.bounds;
                Matrix4x4 place = root.transform.worldToLocalMatrix * piece.transform.localToWorldMatrix;
                Vector3 size = Vector3.Scale(own.size, piece.transform.lossyScale);
                // only pieces >= 5 cm both ways (box count costs frames), not the 0.4 mm paint skins
                float[] sides = { Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z) };
                System.Array.Sort(sides);
                if (sides[1] < 0.05f || piece.name.StartsWith("Skin"))
                    continue;
                desk_item_boxes.Add((place.MultiplyPoint3x4(own.center), new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z)), place.rotation));
            }
        }
    }

    // DESK ITEM COLLIDERS: [items] box per fixed piece of both desks (names C_ = moving, skipped)
    // read off the desk prefab (shape_desk_v2) into the vehicle's
    private static readonly List<(Vector3 centre, Vector3 size, Quaternion turn)> desk_item_boxes = new List<(Vector3, Vector3, Quaternion)>();

    private static void desk_item_colliders(GameObject root)
    {
        Transform items = child(child(root.transform, "[colliders]"), "[items]");
        Transform slab = child(items, "Cube");
        foreach (Transform old in items.Cast<Transform>().Where(one => one.name.StartsWith("DeskPiece")).ToArray())
            UnityEngine.Object.DestroyImmediate(old.gameObject);
        int made = 0;
        foreach ((Vector3 centre, Vector3 size, Quaternion turn) in desk_item_boxes)
        {
            Transform box = UnityEngine.Object.Instantiate(slab.gameObject, items).transform;
            box.name = "DeskPiece" + made++;
            box.position = root.transform.TransformPoint(centre);
            box.rotation = root.transform.rotation * turn;
            box.localScale = size;
        }
        Debug.Log($"EU07 BUILD: the desks take items: {made} [items] boxes");

        // GADGETS anywhere in the cab, as on the stock locos: the game copies every [items] collider into its gadget
        // placing layer (CustomizationPlacementMeshes.TryGenerateInteriorCols); the desk cupboard's boards stay as well
        CustomizationPlacementMeshesProxy cab_gadgets = root.GetComponent<CustomizationPlacementMeshesProxy>();
        if (cab_gadgets == null)
            cab_gadgets = root.AddComponent<CustomizationPlacementMeshesProxy>();
        cab_gadgets.collisionMeshes = new MeshFilter[0];
        cab_gadgets.drillDisableMeshes = new MeshFilter[0];
        cab_gadgets.generateFromTrainInteriorCols = true;
        EditorUtility.SetDirty(cab_gadgets);
    }

    // DESK SKINS: flat piece 0.4 mm over a desk surface; paint_skins paints round what stands on it
    // uneven tone, chipped edges, grime ring round each fitting, corner screws on plates; follows the layout
    private class skin_plan
    {
        public Transform panel;
        public string name;
        public Rect whole;
        public Rect[] pieces;
        public float out_of;
        public Color paint;
        public float smooth;
        public bool screws;
        public List<MeshRenderer> shown = new List<MeshRenderer>();
    }
    private static readonly List<skin_plan> skins = new List<skin_plan>();

    // smoothness as DV cabs: worn 0.3-0.37, fresh 0.66; worn green 0.32, grey 0.30, fresh plate 0.60
    private static void skin(Transform panel, string name, Rect whole, float out_of, Color paint, float smooth, bool screws, params Rect[] pieces)
    {
        skin_plan plan = new skin_plan { panel = panel, name = name, whole = whole, out_of = out_of, paint = paint, smooth = smooth, screws = screws, pieces = pieces.Length > 0 ? pieces : new[] { whole } };
        for (int index = 0; index < plan.pieces.Length; ++index)
        {
            Rect piece = plan.pieces[index];
            Mesh mesh = new Mesh { name = $"{name}_{index}" };
            Vector3[] corners = { new Vector3(piece.xMin, piece.yMin, -out_of), new Vector3(piece.xMax, piece.yMin, -out_of), new Vector3(piece.xMax, piece.yMax, -out_of), new Vector3(piece.xMin, piece.yMax, -out_of) };
            mesh.vertices = corners;
            mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
            mesh.uv = corners.Select(corner => new Vector2((corner.x - whole.xMin) / whole.width, (corner.y - whole.yMin) / whole.height)).ToArray();
            // one winding, facing out, with tangents: two-way winding on a slope cancels tangents and lights black
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.tangents = Enumerable.Repeat(new Vector4(1.0f, 0.0f, 0.0f, -1.0f), 4).ToArray();
            mesh.RecalculateBounds();
            GameObject made = node(panel, $"{name}_{index}", Vector3.zero);
            made.AddComponent<MeshFilter>().sharedMesh = mesh;
            plan.shown.Add(made.AddComponent<MeshRenderer>());
        }
        skins.Add(plan);
    }

    // 0 under `from`, 1 over `to`, eased between (not Mathf.SmoothStep, which eases a -> b by t)
    private static float ramp(float from, float to, float value)
    {
        return Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(from, to, value));
    }

    private static float skin_noise(float x, float y, float per_metre, float seed)
    {
        return (Mathf.PerlinNoise(x * per_metre + seed, y * per_metre + seed * 0.37f) + 0.5f * Mathf.PerlinNoise(x * per_metre * 2.03f + seed * 1.7f, y * per_metre * 2.03f + 3.1f)) / 1.5f;
    }

    private static Texture2D skin_picture(string file, Color32[] pixels, int wide, int high, bool colour)
    {
        Texture2D drawn = new Texture2D(wide, high, TextureFormat.RGBA32, false);
        drawn.SetPixels32(pixels);
        File.WriteAllBytes(Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_skin_" + file + ".png"), drawn.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(drawn);
        string picture = $"{target}/EU07_skin_{file}.png";
        AssetDatabase.ImportAsset(picture, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = (TextureImporter) AssetImporter.GetAtPath(picture) ?? throw new IOException("no picture at " + picture);
        importer.sRGBTexture = colour;
        importer.mipmapEnabled = true;
        importer.anisoLevel = 8;
        importer.maxTextureSize = 2048;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.alphaIsTransparency = false;
        importer.textureCompression = colour ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(picture) ?? throw new IOException("no picture at " + picture);
    }

    private static void paint_skins(Transform root)
    {
        MeshRenderer[] every = root.GetComponentsInChildren<MeshRenderer>(true);
        HashSet<MeshRenderer> own = new HashSet<MeshRenderer>(skins.SelectMany(plan => plan.shown));
        foreach (skin_plan plan in skins)
        {
            float z0 = -plan.out_of, seed = (Mathf.Abs(plan.name.GetHashCode()) % 977) * 0.731f;
            int wide = Mathf.Clamp(Mathf.NextPowerOfTwo((int) (plan.whole.width * 1400.0f)), 256, 2048), high = Mathf.Clamp(Mathf.NextPowerOfTwo((int) (plan.whole.height * 1400.0f)), 256, 2048);
            float per_x = wide / plan.whole.width, per_y = high / plan.whole.height;
            // what stands on it: (middle, half size, corner radius) in the panel's plane
            List<(Vector2 at, Vector2 half, float round)> standing = new List<(Vector2 at, Vector2 half, float round)>();
            foreach (MeshRenderer part in every)
            {
                MeshFilter filter = part.GetComponent<MeshFilter>();
                if (own.Contains(part) || filter == null || filter.sharedMesh == null)
                    continue;
                Matrix4x4 place = plan.panel.worldToLocalMatrix * part.transform.localToWorldMatrix;
                Bounds box = filter.sharedMesh.bounds;
                Vector3 low = Vector3.one * float.MaxValue, top = Vector3.one * float.MinValue;
                for (int corner = 0; corner < 8; ++corner)
                {
                    Vector3 at = place.MultiplyPoint3x4(box.center + Vector3.Scale(box.extents, new Vector3((corner & 1) == 0 ? -1.0f : 1.0f, (corner & 2) == 0 ? -1.0f : 1.0f, (corner & 4) == 0 ? -1.0f : 1.0f)));
                    low = Vector3.Min(low, at);
                    top = Vector3.Max(top, at);
                }
                // stands OUT of the skin (-z), within 15 mm, not a big solid over it
                float area = (top.x - low.x) * (top.y - low.y);
                // and not sunk 4 cm into it (cabinet blocks cross the slope's plane)
                if (low.z > z0 - 0.0004f || top.z < z0 - 0.015f || top.z > z0 + 0.04f || (z0 - low.z > 0.10f && area > 0.05f) || area > 0.6f * plan.whole.width * plan.whole.height)
                    continue;
                Rect on = Rect.MinMaxRect(low.x, low.y, top.x, top.y);
                if (!on.Overlaps(plan.whole))
                    continue;
                Vector2 half = on.size * 0.5f;
                bool round = Mathf.Abs(half.x - half.y) < 0.15f * Mathf.Max(half.x, half.y);
                standing.Add((on.center, half, round ? Mathf.Min(half.x, half.y) : 0.002f));
            }
            // distance to the nearest thing on it (within 3 cm; < 0 under it)
            const float reach = 0.030f;
            float[] nearest = Enumerable.Repeat(reach, wide * high).ToArray();
            foreach ((Vector2 at, Vector2 half, float round) in standing)
            {
                int x0 = Mathf.Max(0, (int) ((at.x - half.x - reach - plan.whole.xMin) * per_x)), x1 = Mathf.Min(wide - 1, (int) ((at.x + half.x + reach - plan.whole.xMin) * per_x));
                int y0 = Mathf.Max(0, (int) ((at.y - half.y - reach - plan.whole.yMin) * per_y)), y1 = Mathf.Min(high - 1, (int) ((at.y + half.y + reach - plan.whole.yMin) * per_y));
                for (int y = y0; y <= y1; ++y)
                {
                    float py = plan.whole.yMin + (y + 0.5f) / per_y;
                    for (int x = x0; x <= x1; ++x)
                    {
                        float px = plan.whole.xMin + (x + 0.5f) / per_x;
                        float dx = Mathf.Abs(px - at.x) - (half.x - round), dy = Mathf.Abs(py - at.y) - (half.y - round);
                        float far = new Vector2(Mathf.Max(dx, 0.0f), Mathf.Max(dy, 0.0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0.0f) - round;
                        if (far < nearest[y * wide + x])
                            nearest[y * wide + x] = far;
                    }
                }
            }
            // screws: in from each corner of each plate piece
            List<(Vector2 at, float slot)> screws = new List<(Vector2 at, float slot)>();
            if (plan.screws)
            {
                foreach (Rect piece in plan.pieces)
                    foreach ((float sx, float sy) in new[] { (piece.xMin + 0.014f, piece.yMin + 0.014f), (piece.xMax - 0.014f, piece.yMin + 0.014f), (piece.xMax - 0.014f, piece.yMax - 0.014f), (piece.xMin + 0.014f, piece.yMax - 0.014f) })
                        screws.Add((new Vector2(sx, sy), Mathf.PerlinNoise(sx * 37.0f + seed, sy * 53.0f) * Mathf.PI));
            }
            bool is_light = plan.paint.grayscale > 0.5f;
            Color primer = is_light ? new Color(0.27f, 0.27f, 0.28f) : new Color(0.085f, 0.078f, 0.070f), grime = new Color(0.055f, 0.050f, 0.045f);
            Color32[] colours = new Color32[wide * high], masks = new Color32[wide * high];
            for (int y = 0; y < high; ++y)
            {
                float py = plan.whole.yMin + (y + 0.5f) / per_y;
                for (int x = 0; x < wide; ++x)
                {
                    float px = plan.whole.xMin + (x + 0.5f) / per_x;
                    // distance in from the edge of this piece (on none = never seen)
                    float edge = -1.0f;
                    foreach (Rect piece in plan.pieces)
                        if (piece.Contains(new Vector2(px, py)))
                            edge = Mathf.Max(edge, Mathf.Min(Mathf.Min(px - piece.xMin, piece.xMax - px), Mathf.Min(py - piece.yMin, piece.yMax - py)));
                    // tone: slow drift + fine dust (dirt goes round things and along edges, below)
                    Color colour = plan.paint * (0.90f + 0.14f * skin_noise(px, py, 1.1f, seed) + 0.05f * skin_noise(px, py, 70.0f, seed + 2.0f));
                    float metal = 0.0f, smooth = plan.smooth * (0.88f + 0.24f * skin_noise(px, py, 2.0f, seed + 31.0f));
                    if (edge >= 0.0f)
                    {
                        // chips: along edges and here and there; show what's under the paint
                        float chip_at = Mathf.Lerp(0.585f, 0.80f, Mathf.SmoothStep(0.0f, 1.0f, edge / 0.022f));
                        float chipped = ramp(chip_at, chip_at + 0.025f, skin_noise(px, py, 85.0f, seed + 5.0f) * 0.75f + skin_noise(px, py, 260.0f, seed + 9.0f) * 0.25f);
                        // grime ring + edge shade round what stands on it; fades within 7 mm (false hits = outline, not slab)
                        float far = nearest[y * wide + x];
                        if (far < 0.0f)
                            far *= -3.0f;
                        float ring = Mathf.SmoothStep(1.0f, 0.0f, far / 0.020f) * (0.55f + 0.45f * skin_noise(px, py, 40.0f, seed + 13.0f));
                        float shade = Mathf.SmoothStep(1.0f, 0.0f, far / 0.005f);
                        chipped = Mathf.Max(chipped, ramp(0.70f, 0.74f, skin_noise(px, py, 150.0f, seed + 17.0f)) * Mathf.SmoothStep(1.0f, 0.0f, far / 0.012f) * 0.9f);
                        colour = Color.Lerp(colour, primer, chipped);
                        metal = chipped * (is_light ? 0.75f : 0.35f);
                        smooth = Mathf.Lerp(smooth, is_light ? 0.55f : 0.25f, chipped);
                        colour = Color.Lerp(colour, grime, Mathf.Clamp01(0.50f * ring + 0.30f * shade));
                        smooth *= 1.0f - 0.55f * ring;
                        metal *= 1.0f - ring;
                    }
                    foreach ((Vector2 at, float slot) in screws)
                    {
                        float off = (new Vector2(px, py) - at).magnitude;
                        if (off > 0.0050f)
                            continue;
                        float across = Mathf.Abs((px - at.x) * Mathf.Sin(slot) - (py - at.y) * Mathf.Cos(slot));
                        bool dark = off > 0.0041f || across < 0.0006f;
                        colour = dark ? new Color(0.05f, 0.05f, 0.05f) : new Color(0.50f, 0.50f, 0.51f);
                        metal = dark ? 0.0f : 0.85f;
                        smooth = dark ? 0.15f : 0.60f;
                    }
                    colours[y * wide + x] = new Color(colour.r, colour.g, colour.b, 1.0f);
                    masks[y * wide + x] = new Color(metal, indoor_occlusion, 0.0f, smooth);
                }
            }
            Material made = finish(new Material(Shader.Find("Standard")) { color = Color.white }, "hammer", 18.0f, is_light ? 0.20f : 0.35f, 1.0f, 1.0f);
            made.mainTexture = skin_picture(plan.name + "_albedo", colours, wide, high, true);
            Texture2D mask = skin_picture(plan.name + "_mask", masks, wide, high, false);
            made.SetTexture("_MetallicGlossMap", mask);
            made.SetTexture("_OcclusionMap", mask);    // green channel: as DV's _s maps serve both
            made.SetFloat("_OcclusionStrength", 1.0f);
            made.EnableKeyword("_METALLICGLOSSMAP");
            made.SetFloat("_GlossMapScale", 1.0f);
            AssetDatabase.CreateAsset(made, $"{target}/EU07_skin_{plan.name}.mat");
            foreach (MeshRenderer shown in plan.shown)
                shown.sharedMaterial = made;
            Debug.Log($"EU07 BUILD: skin {plan.name}: {wide} x {high}, {standing.Count} things standing on it, {screws.Count} screws");
        }
        // cab fittings follow the inside paint; swaps offered here (inside_cans), applied by the mod (eu07_driver.cs, watch_doors)
        Material plate_fresh = finish(material("desk_plate_paint", new Color(0.66f, 0.67f, 0.65f)), "hammer", 18.0f, 0.20f, 0.60f, 0.0f);
        foreach (skin_plan plan in skins)
        {
            Material worn = plan.shown[0].sharedMaterial;
            inside_cans.Add(("worn", worn, worn));
            inside_cans.Add(("new", worn, plan.paint.grayscale > 0.5f ? plate_fresh : desk_green));
        }
    }

    // 0 to 43: just under one turn on the real disc; 300 deg here, 6.98 per position
    private const float controller_sweep = 150.0f;
    private const int eu07_positions = 44;
    // label vs its item in panel frame: nearer the driver; 3 mm out (plate face at 1 mm)
    private static readonly Vector3 label_offset = new Vector3(0.0f, -0.035f, -0.003f);
    // DV caption plate 65 mm; ref lamps/buttons 57-60 mm apart, switches 50: captions at 3/4
    private const float control_caption = 0.75f;

    private static void make_materials()
    {
        black      = material("black", new Color(0.03f, 0.03f, 0.03f));
        black.SetFloat("_Glossiness", 0.30f);    // matte as the paint round it
        white      = material("white", new Color(0.85f, 0.85f, 0.82f));
        // control plates: brushed aluminium; cupboard doors: varnished plywood
        grey_panel = finish(material("desk_grey", new Color(0.42f, 0.44f, 0.43f)), "brushed", 7.0f, 0.5f, 0.48f, 0.35f);
        desk_wood  = finish(material("desk_wood", new Color(0.56f, 0.39f, 0.21f)), "wood", 3.2f, 0.6f, 0.55f, 0.0f);
        // desk green: muted hammer-finish enamel
        desk_green = finish(material("desk_green", new Color(0.19f, 0.39f, 0.25f)), "hammer", 18.0f, 0.35f, 0.32f, 0.0f);
        valve_green = finish(material("valve_green", new Color(0.10f, 0.26f, 0.15f)), "hammer", 18.0f, 0.35f, 0.30f, 0.0f);
        teal       = material("teal", new Color(0.10f, 0.38f, 0.40f));
        silver     = finish(material("pedestal", new Color(0.66f, 0.67f, 0.68f)), "brushed", 7.0f, 0.5f, 0.52f, 0.55f);
        red_cap    = material("button_red", new Color(0.52f, 0.08f, 0.06f));
        green_cap  = material("button_green", new Color(0.09f, 0.34f, 0.14f));
        yellow_cap = material("button_yellow", new Color(0.66f, 0.50f, 0.05f));
        // caps: dulled moulded plastic (0.35); lenses stay glossy
        foreach (Material cap in new[] { red_cap, green_cap, yellow_cap })
            cap.SetFloat("_Glossiness", 0.35f);
        instrument_glow = material("lamp_instrument_glow", new Color(1.0f, 0.86f, 0.62f));    // meter lamps, lit under their hoods
        instrument_glow.EnableKeyword("_EMISSION");
        instrument_glow.SetColor("_EmissionColor", new Color(1.0f, 0.86f, 0.62f) * 1.6f);
        instrument_glow.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        bakelite   = material("bakelite", new Color(0.035f, 0.035f, 0.040f));    // lamp housings, button collars: black, a little shine
        bakelite.SetFloat("_Glossiness", 0.30f);    // higher reads wet beside the matte paint
        ivory      = material("ivory", new Color(0.80f, 0.77f, 0.66f));    // tumbler levers: old cream plastic
        ivory.SetFloat("_Glossiness", 0.45f);
        chrome     = material("chrome", new Color(0.78f, 0.79f, 0.80f));
        door_metal = material("door_metal", new Color(0.50f, 0.51f, 0.52f));    // door handles: worn bright metal
        door_metal.SetFloat("_Glossiness", 0.62f);
        chrome.SetFloat("_Metallic", 0.9f);
        chrome.SetFloat("_Glossiness", 0.75f);
        shapes.Clear();
    }

    // FINISHES (tools/finishes.py -> model/finishes/): tiling normal + mottle, by metre UVs (lay_finish)
    // hammer: dimpled enamel, 18/m; brushed: aluminium; wood: varnished plywood, grain up the door
    private static readonly List<Material> finished = new List<Material>();

    private static Material finish(Material made, string kind, float to_the_metre, float leans, float smooth, float metallic)
    {
        string folder = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/finishes"));
        made.SetTexture("_BumpMap", stand_in("flat", new Color32(128, 128, 255, 255), true));
        made.EnableKeyword("_NORMALMAP");
        made.SetTexture("_DetailAlbedoMap", paint_picture(kind + "_albedo", true, 512, false, folder));
        made.SetTexture("_DetailNormalMap", paint_picture(kind + "_normal", false, 512, true, folder));
        made.SetFloat("_DetailNormalMapScale", leans);
        made.EnableKeyword("_DETAIL_MULX2");
        made.SetFloat("_UVSec", 1.0f);
        made.SetTextureScale("_DetailAlbedoMap", new Vector2(to_the_metre, to_the_metre));
        made.SetFloat("_Glossiness", smooth);
        made.SetFloat("_Metallic", metallic);
        EditorUtility.SetDirty(made);
        if (!finished.Contains(made))
            finished.Add(made);
        return made;
    }

    // metre UVs (UV2) for meshes wearing a finish: box-projected in the root's frame, same size whatever the stretch
    // meshes are copies: DV's cube serves many parts
    private static void lay_finish(Transform root)
    {
        Mesh holder = null;
        int laid = 0;
        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
            Mesh source = filter.sharedMesh;
            if (renderer == null || source == null || !renderer.sharedMaterials.Any(worn => finished.Contains(worn)))
                continue;
            Vector3[] points = source.vertices, normals = source.normals;
            if (normals.Length != points.Length)
                continue;
            Matrix4x4 place = root.worldToLocalMatrix * filter.transform.localToWorldMatrix, turn = place.inverse.transpose;
            Vector2[] metres = new Vector2[points.Length];
            for (int index = 0; index < points.Length; ++index)
            {
                Vector3 at = place.MultiplyPoint3x4(points[index]), faces = turn.MultiplyVector(normals[index]);
                float x = Mathf.Abs(faces.x), y = Mathf.Abs(faces.y), z = Mathf.Abs(faces.z);
                metres[index] = x >= y && x >= z ? new Vector2(at.z, at.y) : y >= z ? new Vector2(at.x, at.z) : new Vector2(at.x, at.y);
            }
            Mesh copy = UnityEngine.Object.Instantiate(source);
            copy.name = source.name + "_laid";
            copy.uv2 = metres;
            // build-made shapes may lack UV1 and tangents; a sloping surface needs both
            if (copy.uv.Length != points.Length)
                copy.uv = metres;
            if (copy.tangents.Length != points.Length)
                copy.RecalculateTangents();
            if (holder == null)
            {
                holder = copy;
                AssetDatabase.CreateAsset(copy, $"{target}/EU07_finish_{root.name}.asset");
            }
            else
                AssetDatabase.AddObjectToAsset(copy, holder);
            filter.sharedMesh = copy;
            ++laid;
        }
        Debug.Log($"EU07 BUILD: finishes: {laid} meshes under {root.name} given UVs in metres");
    }

    // INDOOR REFLECTIONS: DV cabs dim probe sky + ambient with an AO map (DE6 0.40, DH4 0.29, DM3 0.38, DE2 ~0.5)
    // so indoor materials get indoor_occlusion (skins via their mask's green, others a flat stand-in); outside parts keep reflections
    private const float indoor_occlusion = 0.45f;
    // machine room colours (body_colours): indoors too
    private static readonly string[] machine_room = { "eu07_room_floor", "eu07_cabinet", "eu07_trim", "eu07_machine", "eu07_compressor", "eu07_guard_red",
        "eu07_board_blue", "eu07_brass", "eu07_contact", "eu07_bus_copper", "eu07_porcelain_brown", "eu07_lining", "eu07_cab_floor", "eu07_cage", "eu07_cage_louvre" };
    private static readonly string[] outdoors = { "eu07_ext", "glass", "lens", "lamp", "visor", "Buffer", "BrakeHandles", "AirHose", "MUCable", "InfoPlate" };

    private static void occlude_indoors(Material made)
    {
        if (made == null || made.shader == null || made.shader.name != "Standard")
            return;
        made.SetFloat("_GlossyReflections", 1.0f);
        made.DisableKeyword("_GLOSSYREFLECTIONS_OFF");
        // desk lamp lenses count as indoors despite their names
        bool inside = made.name.StartsWith("EU07_lens_") || !outdoors.Any(outside => made.name.IndexOf(outside, StringComparison.OrdinalIgnoreCase) >= 0);
        if (inside && made.GetTexture("_OcclusionMap") == null)
        {
            byte shade = (byte) Mathf.RoundToInt(indoor_occlusion * 255.0f);
            made.SetTexture("_OcclusionMap", stand_in("occlusion", new Color32(shade, shade, shade, 255), false));
            made.SetFloat("_OcclusionStrength", 1.0f);
        }
        EditorUtility.SetDirty(made);
    }

    // SKINNABLE PARTS: Skin Manager swaps textures by name, so a colour-only material can't be skinned.
    // each gets its own 4x4 picture of its colour (EU07_part_<material>), colour white: looks the same
    // not: glass, lenses, lit lamps (emissive), see-through, contacts (heat tint is absolute), paint atlases
    private static readonly string[] not_skinned = { "glass", "lens", "visor", "glow", "bulb", "_lit", "contact" };

    private static void parts_skinnable()
    {
        int made = 0;
        foreach (string id in AssetDatabase.FindAssets("t:Material", new[] { target }))
        {
            string path = AssetDatabase.GUIDToAssetPath(id);
            Material part = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (part == null || part.shader == null || part.shader.name != "Standard" || part.mainTexture != null
                || part.IsKeywordEnabled("_EMISSION") || part.renderQueue >= 2450
                || not_skinned.Any(word => part.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0))
                continue;
            string name = "EU07_part_" + (part.name.StartsWith("EU07_") ? part.name.Substring(5) : part.name);
            string picture = $"{target}/{name}.png";
            Color32 colour = part.color;
            colour.a = 255;
            Texture2D solid = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            solid.SetPixels32(Enumerable.Repeat(colour, 16).ToArray());
            File.WriteAllBytes(Path.Combine(Application.dataPath, "_CCL_CARS/EU07/" + name + ".png"), solid.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(solid);
            AssetDatabase.ImportAsset(picture, ImportAssetOptions.ForceSynchronousImport);
            TextureImporter importer = (TextureImporter) AssetImporter.GetAtPath(picture) ?? throw new IOException("no picture at " + picture);
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
            part.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(picture) ?? throw new IOException("no picture at " + picture);
            part.color = new Color(1.0f, 1.0f, 1.0f, part.color.a);
            EditorUtility.SetDirty(part);
            ++made;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"EU07 BUILD: skinnable parts: {made} colour materials given their own picture (EU07_part_*)");
    }

    private static Material material(string name, Color colour)
    {
        Shader standard = Shader.Find("Standard") ?? throw new MissingReferenceException("no Standard shader");
        Material made = new Material(standard) { color = colour };
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.mat");
        return made;
    }

    // LAMP LENS: DV sets _EmissionColor via property block (IndicatorEmission), so emission must be on
    // own emission a hair above black: the inspector turns the keyword off on black
    private static Material lens(string name, Color dark)
    {
        Shader standard = Shader.Find("Standard") ?? throw new MissingReferenceException("no Standard shader");
        Material made = new Material(standard) { color = dark };
        if (!made.HasProperty("_EmissionColor"))
            throw new MissingFieldException("the Standard shader has no _EmissionColor");
        made.EnableKeyword("_EMISSION");
        made.SetColor("_EmissionColor", new Color(0.002f, 0.002f, 0.002f));
        made.SetFloat("_Glossiness", 0.60f);    // handled, dusty glass
        made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        AssetDatabase.CreateAsset(made, $"{target}/EU07_lens_{name}.mat");
        return made;
    }

    // dial face: tools/dial_faces.py -> model/dials/, as a material
    private static readonly Dictionary<string, Material> dial_faces = new Dictionary<string, Material>();
    private static readonly Dictionary<int, Mesh> bezels = new Dictionary<int, Mesh>();
    private static Mesh dial_disc;

    // self-lit drawn face: glows warm where white (backlit_face)
    private static Material backlit_face(string name)
    {
        Material face = dial_face(name);
        string path = $"{target}/EU07_dial_{name}_backlit.mat";
        AssetDatabase.DeleteAsset(path);
        Material made = new Material(face);
        made.EnableKeyword("_EMISSION");
        made.SetTexture("_EmissionMap", face.mainTexture);
        made.SetColor("_EmissionColor", new Color(1.0f, 0.86f, 0.62f) * 0.55f);
        made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        AssetDatabase.CreateAsset(made, path);
        return made;
    }

    private static Material dial_face(string name)
    {
        if (dial_faces.TryGetValue(name, out Material known))
            return known;
        string picture = $"{target}/EU07_dial_{name}.png";
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/dials/" + name + ".png")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_dial_" + name + ".png"), true);
        AssetDatabase.ImportAsset(picture, ImportAssetOptions.ForceSynchronousImport);
        TextureImporter importer = (TextureImporter) AssetImporter.GetAtPath(picture) ?? throw new IOException("no picture at " + picture);
        importer.mipmapEnabled = true;
        importer.anisoLevel = 8;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();
        Shader standard = Shader.Find("Standard") ?? throw new MissingReferenceException("no Standard shader");
        Material made = new Material(standard) { color = Color.white };
        made.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(picture) ?? throw new IOException("no picture at " + picture);
        made.SetFloat("_Glossiness", 0.15f);
        AssetDatabase.CreateAsset(made, $"{target}/EU07_dial_{name}.mat");
        dial_faces[name] = made;
        return made;
    }

    // flat unit disc in panel plane facing -z, picture right = +x, top = +y; both windings
    private static Mesh disc()
    {
        if (dial_disc != null)
            return dial_disc;
        const int around = 48;
        List<Vector3> points = new List<Vector3> { Vector3.zero }, normals = new List<Vector3> { Vector3.back };
        List<Vector2> pictures = new List<Vector2> { new Vector2(0.5f, 0.5f) };
        List<int> triangles = new List<int>();
        for (int index = 0; index <= around; ++index)
        {
            float angle = 2.0f * Mathf.PI * index / around;
            points.Add(new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0.0f) * 0.5f);
            normals.Add(Vector3.back);
            pictures.Add(new Vector2(0.5f + 0.5f * Mathf.Cos(angle), 0.5f + 0.5f * Mathf.Sin(angle)));
        }
        for (int index = 1; index <= around; ++index)
            triangles.AddRange(new[] { 0, index + 1, index, 0, index, index + 1 });
        dial_disc = new Mesh { name = "dial_disc" };
        dial_disc.SetVertices(points);
        dial_disc.SetNormals(normals);
        dial_disc.SetUVs(0, pictures);
        dial_disc.SetTriangles(triangles, 0);
        dial_disc.RecalculateBounds();
        AssetDatabase.CreateAsset(dial_disc, $"{target}/EU07_dial_disc.asset");
        return dial_disc;
    }

    // black dial rim, one mesh per size; slope meter positions and sizes
    // rim 12 mm wider than dial: 80 mm dials 99 mm apart = 7 mm clear (14 mm between 3rd and 4th)
    private const float meter_dial = 0.080f;
    private static readonly float[] meter_x = { 0.100f, 0.199f, 0.298f, 0.404f, 0.503f, 0.602f };

    private static Mesh bezel(float radius)
    {
        int size = Mathf.RoundToInt(radius * 1000.0f);
        if (!bezels.TryGetValue(size, out Mesh made))
            bezels[size] = made = torus("bezel_" + size, radius + 0.002f, 0.004f, 40, 8);
        return made;
    }

    // recorder clock and counter positions, as shares of dial radius (same as tools/dial_faces.py)
    private const float recorder_clock_up = 0.27f, recorder_clock_radius = 0.19f;
    private const float recorder_counter_down = 0.53f, recorder_counter_wide = 0.60f, recorder_counter_high = 0.14f;

    // recorder clock hand: one turn per `round` (12 h, 60 min), clockwise from up; no lag (no swing back at the hour)
    private static void recorder_hand(Transform panel, string name, Vector2 at, float length, float width, float round, string port, float stands)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        Transform pivot = node(root, "Pivot", Vector3.zero).transform;
        solid(pivot, "Hand", PrimitiveType.Cube, black, new Vector3(0.0f, length * 0.40f, stands), new Vector3(width, length, 0.0004f), Quaternion.identity, false);
        IndicatorGaugeProxy gauge = root.gameObject.AddComponent<IndicatorGaugeProxy>();
        gauge.minValue = 0.0f;
        gauge.maxValue = round;
        gauge.needle = pivot;
        gauge.minAngle = 0.0f;
        gauge.maxAngle = 360.0f;
        gauge.rotationAxis = Vector3.back;
        gauge.gizmoRadius = length;
        serialised(gauge, "minValue", "maxValue", "needle", "minAngle", "maxAngle", "rotationAxis");
        IndicatorPortReaderProxy source = root.gameObject.AddComponent<IndicatorPortReaderProxy>();
        source.portId = port;
        source.useAbsoluteValue = true;
        source.valueMultiplier = 1.0f;
        serialised(source, "portId", "useAbsoluteValue", "valueMultiplier");
    }

    // counter drum: ten figures (tools/dial_faces.py), turns about panel x; 3.5 = half-way from 3 to 4
    private static void recorder_drum(Transform panel, string name, Vector3 at, float wide, float radius, string port, string figures)
    {
        Transform root = node(panel, name, at).transform;
        GameObject drum = node(root, "Drum", Vector3.zero);
        drum.AddComponent<MeshFilter>().sharedMesh = counter_drum(wide, radius);
        drum.AddComponent<MeshRenderer>().sharedMaterial = dial_face(figures);
        IndicatorGaugeProxy gauge = root.gameObject.AddComponent<IndicatorGaugeProxy>();
        gauge.minValue = 0.0f;
        gauge.maxValue = 10.0f;
        gauge.needle = drum.transform;
        gauge.minAngle = 0.0f;
        gauge.maxAngle = -360.0f;
        gauge.rotationAxis = Vector3.right;
        gauge.gizmoRadius = radius;
        serialised(gauge, "minValue", "maxValue", "needle", "minAngle", "maxAngle", "rotationAxis");
        IndicatorPortReaderProxy source = root.gameObject.AddComponent<IndicatorPortReaderProxy>();
        source.portId = port;
        source.useAbsoluteValue = true;
        source.valueMultiplier = 1.0f;
        serialised(source, "portId", "useAbsoluteValue", "valueMultiplier");
    }

    // drum skin: strip round panel x; unturned, figure 0 faces out; +1/10 turn = next figure
    private static Mesh counter_drum(float wide, float radius)
    {
        if (shapes.TryGetValue("recorder_drum", out Mesh known))
            return known;
        const int steps = 40;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<Vector2> pictures = new List<Vector2>();
        List<int> triangles = new List<int>();
        for (int step = 0; step <= steps; ++step)
        {
            float angle = (-18.0f + 360.0f * step / steps) * Mathf.Deg2Rad;
            Vector3 away = new Vector3(0.0f, Mathf.Sin(angle), -Mathf.Cos(angle));
            foreach (float side in new[] { -0.5f, 0.5f })
            {
                points.Add(away * radius + new Vector3(side * wide, 0.0f, 0.0f));
                normals.Add(away);
                pictures.Add(new Vector2(side + 0.5f, (float) step / steps));
            }
        }
        for (int step = 0; step < steps; ++step)
        {
            int a = 2 * step, b = a + 1, c = a + 2, d = a + 3;
            bool turned_over = Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[a]), normals[a]) < 0.0f;
            triangles.AddRange(turned_over ? new[] { a, c, b, b, c, d } : new[] { a, b, c, b, d, c });
        }
        Mesh made = new Mesh { name = "recorder_drum" };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetUVs(0, pictures);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_recorder_drum.asset");
        shapes["recorder_drum"] = made;
        return made;
    }

    // recorder swing bolt: stud from a boss, wire loop leaning `lean` deg toward the side
    private static void recorder_bolt(Transform panel, string name, Vector2 at, float lean)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        solid(root, "Boss", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, 0.0f, -0.003f), new Vector3(0.016f, 0.003f, 0.016f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(root, "Stud", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, 0.0f, -0.011f), new Vector3(0.006f, 0.008f, 0.006f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        if (!shapes.TryGetValue("recorder_loop", out Mesh loop))
            shapes["recorder_loop"] = loop = torus("recorder_loop", 0.0095f, 0.0014f, 20, 6);
        // ring lies about its own y; turned about the panel normal, its plane holds normal + leaning line
        Vector2 along = new Vector2(-Mathf.Sin(lean * Mathf.Deg2Rad), Mathf.Cos(lean * Mathf.Deg2Rad));
        GameObject ring = shaped(root, "Loop", loop, chrome, new Vector3(along.x * 0.0085f, along.y * 0.0085f, -0.016f));
        ring.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, 90.0f + lean);
    }

    // throws if the component doesn't serialise the field (else setting it silently does nothing)
    private static void serialised(UnityEngine.Object component, params string[] fields)
    {
        SerializedObject serialized = new SerializedObject(component);
        foreach (string field in fields)
        {
            if (serialized.FindProperty(field) == null)
                throw new MissingFieldException($"{component.GetType().Name} on {component.name} serialises no {field}: has CCL changed?");
        }
    }

    private static GameObject node(Transform parent, string name, Vector3 at)
    {
        GameObject made = new GameObject(name);
        made.transform.SetParent(parent, false);
        made.transform.localPosition = at;
        return made;
    }

    private static Transform panel(Transform desk, string name, Vector3 at, float tilt)
    {
        Transform made = node(desk, name, at).transform;
        made.localRotation = Quaternion.Euler(tilt, 0.0f, 0.0f);
        return made;
    }

    private static Transform carry(Transform item, Transform parent, Vector3 at)
    {
        item.SetParent(parent, false);
        item.localPosition = at;
        item.localRotation = Quaternion.identity;
        return item;
    }

    // primitive, with or without a collider; cylinder gets a box collider (as his button)
    private static GameObject solid(Transform parent, string name, PrimitiveType shape, Material material,
        Vector3 at, Vector3 size, Quaternion turn, bool collider)
    {
        GameObject made = GameObject.CreatePrimitive(shape);
        made.name = name;
        made.transform.SetParent(parent, false);
        made.transform.localPosition = at;
        made.transform.localRotation = turn;
        made.transform.localScale = size;
        made.GetComponent<MeshRenderer>().sharedMaterial = material;
        UnityEngine.Object.DestroyImmediate(made.GetComponent<Collider>());
        if (collider)
            made.AddComponent<BoxCollider>();
        return made;
    }

    // inactive SIBLING click area, not a child (CCL: static area under a moving control defeats it); triggers only
    private static StaticInteractionAreaProxy sphere_area(Transform parent, string name, Vector3 centre, float radius)
    {
        GameObject made = node(parent, "IA_" + name, Vector3.zero);
        SphereCollider sphere = made.AddComponent<SphereCollider>();
        sphere.isTrigger = true;
        sphere.center = centre;
        sphere.radius = radius;
        StaticInteractionAreaProxy proxy = made.AddComponent<StaticInteractionAreaProxy>();
        made.SetActive(false);
        return proxy;
    }

    private static StaticInteractionAreaProxy box_area(Transform parent, string name, Vector3 centre, Vector3 size)
    {
        GameObject made = node(parent, "IA_" + name, Vector3.zero);
        BoxCollider box = made.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = centre;
        box.size = size;
        StaticInteractionAreaProxy proxy = made.AddComponent<StaticInteractionAreaProxy>();
        made.SetActive(false);
        return proxy;
    }

    // feeder must be on the control: InteractablePortFeeder.Init does GetComponent<ControlImplBase>()
    private static void feed(GameObject control, string port)
    {
        control.AddComponent<InteractablePortFeederProxy>().portId = port;
    }

    private static void make_label(Transform parent, Vector3 at, string key, float scale = 1.0f)
    {
        GameObject made = node(parent, "Label", at);
        made.transform.localScale = Vector3.one * scale;
        LabelLocalizer label = made.AddComponent<LabelLocalizer>();
        label.key = key;
        label.ModelType = LabelModelType.Flush;
        serialised(label, "key", "ModelType");
    }

    // hold-mode push button: 1 pressed, 0 released (ButtonBase isToggle + isTogglingBack); mod uses the port event
    // panel -Z is out, so pushed in = +Z
    private static GameObject make_button(Transform panel, string name, Vector2 at, Material cap,
        string port, string label, string key, bool mushroom = false)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        // BUTTONS as measured on the ref cab: 12-sided black collar 49 mm, 11 mm out, 29.6 mm bore; flat cap 1.5 mm under
        // MUSHROOM (main breaker off, slip): 30 mm stem, 47.8 mm head 23.6 mm off; grab = hidden disc at the cap
        shaped(root, "Collar", turned("button_collar", 12, true, (0.0254f, 0.0f), (0.0254f, 0.0100f), (0.0246f, 0.0110f), (0.0148f, 0.0110f), (0.0148f, 0.0050f)), bakelite, Vector3.zero);
        GameObject head;
        if (mushroom)
        {
            shaped(control.transform, "Cap", turned("button_mushroom", 24, false, (0.0150f, 0.0070f), (0.0150f, 0.0180f), (0.0234f, 0.0180f), (0.0239f, 0.0186f),
                (0.0239f, 0.0212f), (0.0224f, 0.0224f), (0.0120f, 0.0234f), (0.0f, 0.0236f)), cap, Vector3.zero);
            head = solid(control.transform, "Head", PrimitiveType.Cylinder, cap, new Vector3(0.0f, 0.0f, -0.0208f), new Vector3(0.0470f, 0.0028f, 0.0470f), Quaternion.Euler(90.0f, 0.0f, 0.0f), true);
        }
        else
        {
            shaped(control.transform, "Cap", turned("button_cap", 24, false, (0.0143f, 0.0050f), (0.0143f, 0.0089f), (0.0136f, 0.0095f), (0.0f, 0.0095f)), cap, Vector3.zero);
            head = solid(control.transform, "Head", PrimitiveType.Cylinder, cap, new Vector3(0.0f, 0.0f, -0.0075f), new Vector3(0.0286f, 0.0020f, 0.0286f), Quaternion.Euler(90.0f, 0.0f, 0.0f), true);
        }
        UnityEngine.Object.DestroyImmediate(head.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(head.GetComponent<MeshFilter>());

        ButtonProxy button = control.AddComponent<ButtonProxy>();
        button.colliderGameObjects = new[] { head };
        button.nonVrStaticInteractionArea = sphere_area(root, name, new Vector3(0.0f, 0.0f, -0.006f), 0.026f);
        button.createRigidbody = true;
        button.useJoints = false;
        button.pushLocalOffset = new Vector3(0.0f, 0.0f, 0.004f);
        button.isToggle = true;
        button.isTogglingBack = true;
        serialised(button, "useJoints", "pushLocalOffset", "isToggle", "isTogglingBack", "colliderGameObjects", "nonVrStaticInteractionArea");
        feed(control, port);
        if (key != null)
        {
            ButtonUseKeyboardInputProxy input = control.AddComponent<ButtonUseKeyboardInputProxy>();
            input.useAction = new AKeyboardInputProxy.ActionReference(key);
            input.OnValidate();
            serialised(input, "useAction", "_json");
        }
        sounds(button, ("press", "ButtonClick_02_Push"));
        button.OnValidate();
        make_label(root, label_offset, label, control_caption);
        return control;
    }

    // HORN LEVER: LeverProxy without steps, springs back on release (LeverNonVR resetTargetPositionOnUngrab)
    // key: scrollWheelHoverScroll = whole throw, back on release (LeverBase.Scroll); 0.5 rest, 0 left, 1 right
    private const float horn_throw = 25.0f;
    private static void make_horn(Transform panel, Vector2 at)
    {
        const float proud = 0.022f, reach = 0.075f;
        solid(panel, "HornValve", PrimitiveType.Cube, black, new Vector3(at.x, at.y, -proud * 0.5f), new Vector3(0.046f, 0.036f, proud), Quaternion.identity, false);
        solid(panel, "HornValveNut", PrimitiveType.Cylinder, chrome, new Vector3(at.x, at.y + 0.020f, -proud * 0.5f), new Vector3(0.014f, 0.003f, 0.014f), Quaternion.identity, false);
        Transform root = node(panel, "Horn", new Vector3(at.x, at.y, -proud)).transform;
        GameObject control = node(root, "C_Horn", Vector3.zero);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white,
            new Vector3(0.0f, 0.0f, -reach * 0.5f), new Vector3(0.026f, 0.026f, reach + 0.012f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        solid(control.transform, "Stem", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, 0.0f, -(reach - 0.012f) * 0.5f),
            new Vector3(0.008f, (reach - 0.012f) * 0.5f, 0.008f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(control.transform, "Knob", PrimitiveType.Sphere, black, new Vector3(0.0f, 0.0f, -(reach - 0.012f)), Vector3.one * 0.024f, Quaternion.identity, false);
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, 0.0f, -(reach - 0.012f)));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);     // as his

        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        lever.nonVrStaticInteractionArea = sphere_area(root, "Horn", new Vector3(0.0f, 0.0f, -reach * 0.5f), 0.05f);
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 30.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = horn_throw;
        lever.useSteppedJoint = false;
        lever.steppedValueUpdate = false;
        lever.jointAxis = Vector3.down;    // rising value: end to the right
        lever.useSpring = true;
        lever.jointSpring = 400.0f;
        lever.jointDamper = 20.0f;
        lever.useLimits = true;
        lever.jointLimitMin = -horn_throw;
        lever.jointLimitMax =  horn_throw;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "scrollWheelHoverScroll", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        feed(control, "hornControl.EXT_IN");
        MouseScrollKeyboardInputProxy scroll = control.AddComponent<MouseScrollKeyboardInputProxy>();
        scroll.scrollAction = new AKeyboardInputProxy.ActionReference("HornIncremental");
        scroll.OnValidate();
        serialised(scroll, "scrollAction", "_json");
        lever.OnValidate();
        make_label(panel, new Vector3(at.x, at.y - 0.034f, -0.002f), "eu07/horn", control_caption);
    }

    // BREAKER BOX in the assistant's desk top, as the ref: box 452 x 235 x 129 mm, flush LID 506 x 226 hinged at far edge
    // "Bateria" = DV main-electrics fuse (master); pair = one 2-pole breaker; far row 11th pole spare; functions: eu07_low_voltage.cs
    private const float well_x0 = -0.7705f, well_x1 = -0.2615f, well_z0 = 0.2575f, well_z1 = 0.4905f, well_deep = 0.158f;
    private const float box_x0 = -0.736f, box_x1 = -0.284f, box_z0 = 0.266f, box_z1 = 0.487f, box_rim = 0.025f, box_divide = -0.5285f;
    private const float battery_plate = 0.070f, breaker_plate = 0.092f;    // depth of each plate under the desk top

    private static void make_breaker_box(Transform desk, Transform top, Transform old)
    {
        // `top` in the desk top: x = desk x, y = desk z, z DOWN into the desk
        void lump(string name, Material material, float x0, float x1, float y0, float y1, float z0, float z1) => solid(top, name, PrimitiveType.Cube, material,
            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.identity, false);
        // box walls, rim down: between well mouth and box inside
        lump("BoxWallLeft" , grey_panel, well_x0, box_x0 , well_z0, well_z1, box_rim, well_deep);
        lump("BoxWallRight", grey_panel, box_x1 , well_x1, well_z0, well_z1, box_rim, well_deep);
        lump("BoxWallNear" , grey_panel, box_x0 , box_x1 , well_z0, box_z0 , box_rim, well_deep);
        lump("BoxWallFar"  , grey_panel, box_x0 , box_x1 , box_z1 , well_z1, box_rim, well_deep);
        lump("BoxBatteryPlate", silver, box_x0, box_divide - 0.0015f, box_z0, box_z1, battery_plate, well_deep);
        lump("BoxDivider"     , grey_panel, box_divide - 0.0015f, box_divide + 0.0015f, box_z0, box_z1, box_rim + 0.010f, well_deep);
        lump("BoxBreakerPlate", silver, box_divide + 0.0015f, box_x1, box_z0, box_z1, breaker_plate, well_deep);

        // "Bateria": his main-electrics fuse switch, on the battery plate
        Transform battery = carry(child(old, "MainFuse"), top, new Vector3(-0.595f, 0.388f, battery_plate));
        Transform caption = child(battery, "Label");
        caption.localPosition = new Vector3(0.0f, -0.064f, -0.003f);    // below its housing
        // housing: black bakelite 56 x 96 x 16 mm, 60 mm slot (bar turns 45 deg each way), four screws; longer bar + knob
        const float house_x = 0.028f, house_y = 0.048f, house_h = 0.016f, slot_x = 0.010f, slot_y = 0.030f;
        void housing(string name, Material material, float x0, float x1, float y0, float y1, float z0, float z1) => solid(battery, name, PrimitiveType.Cube, material,
            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.identity, false);
        housing("HousingLeft" , bakelite, -house_x, -slot_x, -house_y, house_y, -house_h, 0.0f);
        housing("HousingRight", bakelite,  slot_x,  house_x, -house_y, house_y, -house_h, 0.0f);
        housing("HousingNear" , bakelite, -slot_x,  slot_x , -house_y, -slot_y, -house_h, 0.0f);
        housing("HousingFar"  , bakelite, -slot_x,  slot_x ,  slot_y,  house_y, -house_h, 0.0f);
        housing("HousingSlot" , black   , -slot_x,  slot_x , -slot_y,  slot_y , -0.003f, 0.0f);
        foreach (float sx in new[] { -0.019f, 0.019f })
            foreach (float sy in new[] { -0.039f, 0.039f })
            {
                solid(battery, "Screw", PrimitiveType.Cylinder, chrome, new Vector3(sx, sy, -house_h - 0.0003f), new Vector3(0.0052f, 0.0006f, 0.0052f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
                solid(battery, "ScrewSlot", PrimitiveType.Cube, black, new Vector3(sx, sy, -house_h - 0.0009f), new Vector3(0.0040f, 0.0007f, 0.0003f),
                    Quaternion.Euler(0.0f, 0.0f, sx * sy > 0.0f ? 25.0f : -60.0f), false);
            }
        Transform bar = child(child(battery, "C_MainFuse"), "Cube");
        // knob top 3 mm under the shut lid
        bar.localScale = new Vector3(0.012f, 0.012f, 0.044f);
        bar.localPosition = new Vector3(0.0f, 0.0f, -0.022f);
        solid(bar.parent, "Knob", PrimitiveType.Sphere, bakelite, new Vector3(0.0f, 0.0f, -0.046f), new Vector3(0.022f, 0.022f, 0.022f), Quaternion.identity, false);
        LabelLocalizer words = caption.GetComponent<LabelLocalizer>() ?? throw new MissingComponentException("his fuse switch's label is not a LabelLocalizer");
        words.key = "eu07/battery_110";
        serialised(words, "key");
        UnityEngine.Object.DestroyImmediate(child(old, "MotorFuse").gameObject);    // the fuse is the "control" breaker now

        GameObject heating = make_toggle(top, "CabHeating", new Vector2(-0.690f, 0.440f), "cabHeatingSwitch.EXT_IN", "eu07/cab_heating", null);
        heating.transform.parent.localPosition += new Vector3(0.0f, 0.0f, battery_plate);
        child(heating.transform, "Tumbler").GetComponent<MeshRenderer>().sharedMaterial = red_cap;

        // breakers: two rows of five pairs, 40 mm per pair, spare pole at far row end
        const float first = -0.4875f, pitch = 0.040f, far_row = 0.436f, near_row = 0.339f;
        for (int index = 0; index < 5; ++index)
        {
            make_breaker(top, "BreakerFar" + index , new Vector2(first + pitch * index, far_row ), breakers_far[index].fuse , breakers_far[index].label , null);
            make_breaker(top, "BreakerNear" + index, new Vector2(first + pitch * index, near_row), breakers_near[index].fuse, breakers_near[index].label,
                breakers_near[index].fuse == "fusebox.TM_POWER" ? "TractionMotorFuse" : null);
        }
        Transform spare = node(top, "BreakerSpare", new Vector3(first + pitch * 4.0f + 0.031f, far_row, breaker_plate)).transform;
        breaker_pole(spare, 0.0f, false);

        // LID frame: hinge line at far edge in the desk top plane; leaf toward the driver (-z); +x turn lifts it
        float wide = well_x1 - well_x0 - 0.003f, deep = well_z1 - well_z0 - 0.003f;
        const float thick = 0.006f, opens = 82.0f;
        // 1.5 mm proud of the desk top, to read as a lid
        Transform root = node(desk, "BreakerFlap", new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0015f, well_z1 - 0.0015f)).transform;
        GameObject control = node(root, "C_BreakerFlap", Vector3.zero);
        Vector3 middle = new Vector3(0.0f, -thick * 0.5f, -deep * 0.5f);
        solid(control.transform, "Leaf", PrimitiveType.Cube, desk_green, middle, new Vector3(wide, thick, deep), Quaternion.identity, false);
        // folded edge underneath: open, a tray not a sheet
        solid(control.transform, "LeafEdgeNear" , PrimitiveType.Cube, desk_green, new Vector3(0.0f, -0.012f, -deep + 0.002f), new Vector3(wide, 0.024f, 0.004f), Quaternion.identity, false);
        solid(control.transform, "LeafEdgeLeft" , PrimitiveType.Cube, desk_green, new Vector3(-wide * 0.5f + 0.002f, -0.012f, -deep * 0.5f), new Vector3(0.004f, 0.024f, deep), Quaternion.identity, false);
        solid(control.transform, "LeafEdgeRight", PrimitiveType.Cube, desk_green, new Vector3( wide * 0.5f - 0.002f, -0.012f, -deep * 0.5f), new Vector3(0.004f, 0.024f, deep), Quaternion.identity, false);
        shaped(control.transform, "Knob", turned("flap_knob", 8, true, (0.0060f, 0.0f), (0.0060f, 0.0080f), (0.0125f, 0.0120f), (0.0125f, 0.0190f), (0.0080f, 0.0240f), (0.0f, 0.0240f)), black,
            new Vector3(0.0f, 0.0f, -deep + 0.026f)).transform.localRotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
        foreach (float side in new[] { -0.17f, 0.17f })
            solid(root, "Hinge", PrimitiveType.Cylinder, desk_green, new Vector3(side, 0.002f, 0.0f), new Vector3(0.010f, 0.030f, 0.010f), Quaternion.Euler(0.0f, 0.0f, 90.0f), false);
        // hasp plate before the lid, with slot
        solid(desk, "FlapHasp", PrimitiveType.Cube, desk_green, new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0015f, well_z0 - 0.018f), new Vector3(0.080f, 0.003f, 0.026f), Quaternion.identity, false);
        solid(desk, "FlapHaspSlot", PrimitiveType.Cube, black, new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0031f, well_z0 - 0.018f), new Vector3(0.016f, 0.0004f, 0.010f), Quaternion.identity, false);

        Vector3 grip_size = new Vector3(wide, 0.026f, deep);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, new Vector3(0.0f, -0.011f, -deep * 0.5f), grip_size, Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, 0.010f, -deep + 0.026f));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);

        // notched lever, as the doors (see make_door): stop every ~10 deg
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        // no static area: it would stay where the lid lay shut and catch clicks meant for the breakers; grab the leaf
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 3.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 9;
        lever.jointAxis = Vector3.right;
        lever.useSpring = true;
        lever.jointSpring = 40.0f;
        lever.jointDamper = 3.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = opens;
        serialised(lever, "interactionPoint", "useSteppedJoint", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        sounds(lever, ("limitHit", "ItemCollision_MetalSheet_03"));
        lever.OnValidate();
    }

    // one breaker pole through the cover plate: light grey nose 17.4 x 44 mm, dark window, optional lever
    private static void breaker_pole(Transform parent, float x, bool lever)
    {
        shaped(parent, "Nose", slab("breaker_nose", (0.0087f, 0.0220f, 0.0010f, 0.0f), (0.0087f, 0.0220f, 0.0010f, 0.0030f), (0.0080f, 0.0213f, 0.0008f, 0.0038f),
            (0.0f, 0.0f, 0.0f, 0.0038f)), ivory, new Vector3(x, 0.0f, 0.0f));
        solid(parent, "Window", PrimitiveType.Cube, red_cap, new Vector3(x, -0.0150f, -0.0039f), new Vector3(0.0080f, 0.0030f, 0.0003f), Quaternion.identity, false);
        solid(parent, "Slot", PrimitiveType.Cube, grey_panel, new Vector3(x, 0.0040f, -0.0039f), new Vector3(0.0066f, 0.0130f, 0.0003f), Quaternion.identity, false);
        if (lever)
            return;
        solid(parent, "LeverDown", PrimitiveType.Cube, black, new Vector3(x, -0.0010f, -0.0070f), new Vector3(0.0060f, 0.0080f, 0.0070f), Quaternion.Euler(28.0f, 0.0f, 0.0f), false);
    }

    // 2-pole 110 V breaker: toggle feeding a DV fuse, and following it
    private static void make_breaker(Transform panel, string name, Vector2 at, string fuse, string label, string key)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, breaker_plate)).transform;
        const float pole = 0.009f, hub_out = 0.0045f;
        breaker_pole(root, -pole, true);
        breaker_pole(root,  pole, true);
        GameObject control = node(root, "C_" + name, new Vector3(0.0f, 0.004f, -hub_out));
        GameObject grip = solid(control.transform, "Lever", PrimitiveType.Cube, white, new Vector3(0.0f, 0.0f, -0.0060f), new Vector3(0.030f, 0.010f, 0.014f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        foreach (float x in new[] { -pole, pole })
            solid(control.transform, "Toggle", PrimitiveType.Cube, black, new Vector3(x, 0.0f, -0.0050f), new Vector3(0.0060f, 0.0070f, 0.0100f), Quaternion.identity, false);
        solid(control.transform, "Tie", PrimitiveType.Cube, black, new Vector3(0.0f, 0.0f, -0.0085f), new Vector3(0.0240f, 0.0040f, 0.0030f), Quaternion.identity, false);

        ToggleSwitchProxy toggle = control.AddComponent<ToggleSwitchProxy>();
        toggle.colliderGameObjects = new[] { grip };
        toggle.nonVrStaticInteractionArea = sphere_area(root, name, new Vector3(0.0f, 0.004f, -0.008f), 0.016f);
        toggle.jointAxis = new Vector3(1.0f, 0.0f, 0.0f);
        toggle.jointLimitMin = -28.0f;
        toggle.jointLimitMax =  28.0f;
        toggle.angleOffset = -90.0f;
        serialised(toggle, "jointAxis", "jointLimitMin", "jointLimitMax");
        InteractableFuseFeederProxy feeder = control.AddComponent<InteractableFuseFeederProxy>();
        feeder.fuseId = fuse;
        serialised(feeder, "fuseId");
        if (key != null)
        {
            ToggleValueKeyboardInputProxy input = control.AddComponent<ToggleValueKeyboardInputProxy>();
            input.useAction = new AKeyboardInputProxy.ActionReference(key);
            input.OnValidate();
            serialised(input, "useAction", "_json");
        }
        control.AddComponent<ToggleSwitchZeroOneNamesProxy>();
        sounds(toggle, ("toggle", "Switch_05_Solid"));
        toggle.OnValidate();
        make_label(root, new Vector3(0.0f, -0.0495f, -0.0012f), label, 0.55f);
    }

    // ISOLATOR DOOR: sheet leaf, hinged away from the cab, swings into the passage; notched lever, no static area
    // DV lock holds it shut while the interlock cock is at P (wire_simulation)
    private static void make_isolator_door(Transform parent, Vector3 at, Quaternion facing, float box_wide, float box_high)
        => make_sheet_door(parent, "IsolatorDoor", at, facing, box_wide, box_high, "isolatorDoor.EXT_IN", "eu07/isolator_door", true);

    // CZUWAK / SHP BOX on the sill right of the speedometer: ref 175 x 102 x 110 mm, middle 0.50 m right
    // ref photos (Commons, free licences): "LampkaCzuwakSHP.jpg" by Pinoteres, "Czuwak aktywny.jpg" by Januszppk
    // hooded face leaning back; left blue = vigilance (eu07.LAMP_VIGILANCE), right red = SHP (unwritten); `foot` = face bottom middle
    private static void make_shp_box(Transform desk, Vector3 foot, Color czuwak, Color shp)
    {
        const float wide = 0.175f, high = 0.100f, deep = 0.105f, leans = 14.0f, hood = 0.016f, sheet = 0.002f;
        Transform face = panel(desk, "ShpBox", foot, leans);    // -z out toward the driver; y up the face
        shaped(face, "Case", blocks("shp_case",
            (new Vector3(-wide * 0.5f, 0.0f, 0.0f), new Vector3(wide * 0.5f, high, deep)),
            (new Vector3(-wide * 0.5f - sheet, high, -hood), new Vector3(wide * 0.5f + sheet, high + sheet, deep)),
            (new Vector3(-wide * 0.5f - sheet, 0.0f, -hood), new Vector3(-wide * 0.5f, high, deep)),
            (new Vector3(wide * 0.5f, 0.0f, -hood), new Vector3(wide * 0.5f + sheet, high, deep)),
            (new Vector3(-wide * 0.5f - sheet, -sheet, -hood * 0.5f), new Vector3(wide * 0.5f + sheet, 0.0f, deep))), desk_green, Vector3.zero);
        solid(face, "Strip", PrimitiveType.Cube, silver, new Vector3(0.0f, 0.025f, -0.0006f), new Vector3(0.150f, 0.014f, 0.0012f), Quaternion.identity, false);
        // same lamps as the desk plate (make_lamp)
        make_lamp(face, "LampVigilance", new Vector2(-0.036f, 0.060f), czuwak, "eu07.LAMP_VIGILANCE", "eu07/lamp_vigilance");
        make_lamp(face, "LampShp", new Vector2(0.036f, 0.060f), shp, "eu07.LAMP_SHP", "eu07/lamp_shp");
        // no lamp sound: buzzer is the cab's own, after 3 s of blinking (eu07_driver.cs, watch_vigilance)
    }

    // PLYWOOD CUPBOARD DOOR: hinged left edge, swings out (-z); 12 mm leaf 2 mm proud, black knob, 2 chrome hinges, seam
    // whole leaf is the handle
    private static void make_cupboard_door(Transform desk, string name, float x0, float x1, float y0, float y1, float face_z, string port, string label)
    {
        const float thick = 0.012f, proud = 0.002f, swings = 100.0f;
        float wide = x1 - x0, high = y1 - y0;
        Transform hinge = node(desk, name, new Vector3(x0, (y0 + y1) * 0.5f, face_z)).transform;
        GameObject control = node(hinge, "C_" + name, Vector3.zero);
        Vector3 middle = new Vector3(wide * 0.5f, 0.0f, -proud - thick * 0.5f);
        solid(control.transform, "Leaf", PrimitiveType.Cube, desk_wood, middle, new Vector3(wide, high, thick), Quaternion.identity, false);
        solid(control.transform, "Knob", PrimitiveType.Sphere, black, new Vector3(wide - 0.045f, 0.015f, -proud - thick - 0.010f), Vector3.one * 0.026f, Quaternion.identity, false);
        foreach (float edge in new[] { -1.0f, 1.0f })
        {
            solid(hinge, "Seam", PrimitiveType.Cube, black, new Vector3(wide * 0.5f, edge * (high * 0.5f + 0.004f), -0.0008f), new Vector3(wide + 0.016f, 0.008f, 0.0016f), Quaternion.identity, false);
            solid(hinge, "Seam", PrimitiveType.Cube, black, new Vector3(wide * 0.5f + edge * (wide * 0.5f + 0.004f), 0.0f, -0.0008f), new Vector3(0.008f, high, 0.0016f), Quaternion.identity, false);
            solid(hinge, "Hinge", PrimitiveType.Cylinder, chrome, new Vector3(-0.004f, edge * 0.185f, -proud - thick * 0.5f), new Vector3(0.010f, 0.028f, 0.010f), Quaternion.identity, false);
        }
        make_label(control.transform, new Vector3(wide * 0.5f, high * 0.5f - 0.035f, -proud - thick - 0.0005f), label, control_caption);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, middle, new Vector3(wide, high, thick + 0.004f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        GameObject point = node(control.transform, "Interaction Point", new Vector3(wide - 0.045f, 0.015f, -0.040f));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 3.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 8;
        lever.jointAxis = Vector3.up;    // about +y, along-x swings to -z
        lever.useSpring = true;
        lever.jointSpring = 40.0f;
        lever.jointDamper = 3.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = swings;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        feed(control, port);
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        sounds(lever, ("limitHit", "ItemCollision_MetalSheet_03"));
        lever.OnValidate();
    }

    // any cabinet: name, port, caption, HV sign or not
    private static void make_sheet_door(Transform parent, string name, Vector3 at, Quaternion facing, float box_wide, float box_high, string port, string label, bool warning,
        float swings = 105.0f, Vector2[] painted = null)
    {
        const float lap = 0.005f, leaf_thick = 0.006f;
        float leaf_wide = box_wide + 2.0f * lap, leaf_high = box_high + 2.0f * lap;
        Material sheet = body_material("eu07_cabinet");
        // at: opening middle on the panel face; door x toward the cab
        Transform hinge = node(parent, name, at + facing * new Vector3(-leaf_wide * 0.5f, 0.0f, 0.0f)).transform;
        hinge.localRotation = facing;
        GameObject control = node(hinge, "C_" + name, Vector3.zero);
        Vector3 middle = new Vector3(leaf_wide * 0.5f, 0.0f, -0.001f - leaf_thick * 0.5f);
        GameObject leaf = solid(control.transform, "Leaf", PrimitiveType.Cube, sheet, middle, new Vector3(leaf_wide, leaf_high, leaf_thick), Quaternion.identity, false);
        if (painted != null)
        {
            // painted as the LV cabinet's other leaf: its picture place laid over (model/eu07_lv_leaf_uv.txt), louvres, no seal
            Mesh laid = UnityEngine.Object.Instantiate(leaf.GetComponent<MeshFilter>().sharedMesh);
            laid.name = name + "_leaf";
            laid.uv = laid.vertices.Select(corner =>
            {
                float along = corner.x + 0.5f, up = corner.y + 0.5f;
                return (1.0f - along) * (1.0f - up) * painted[0] + along * (1.0f - up) * painted[1] + along * up * painted[2] + (1.0f - along) * up * painted[3];
            }).ToArray();
            AssetDatabase.CreateAsset(laid, $"{target}/EU07_{name}_leaf.asset");
            leaf.GetComponent<MeshFilter>().sharedMesh = laid;
            leaf.GetComponent<MeshRenderer>().sharedMaterial = body_material("eu07_int_d");
            foreach (float up in new[] { -leaf_high * 0.5f + 0.094f, leaf_high * 0.5f - 0.094f })
                solid(control.transform, "Louvre", PrimitiveType.Cube, body_material("eu07_grille"), new Vector3(leaf_wide * 0.5f, up, -0.001f - leaf_thick - 0.001f),
                    new Vector3(leaf_wide - 0.100f, 0.060f, 0.002f), Quaternion.identity, false);
        }
        // dark seal round the opening, a little past the leaf: else shut door and panel read as one
        foreach (float edge in painted == null ? new[] { -1.0f, 1.0f } : new float[0])
        {
            solid(hinge, "Seal", PrimitiveType.Cube, black, new Vector3(leaf_wide * 0.5f, edge * (box_high * 0.5f + 0.0055f), -0.0005f), new Vector3(leaf_wide + 0.012f, 0.011f, 0.001f), Quaternion.identity, false);
            solid(hinge, "Seal", PrimitiveType.Cube, black, new Vector3(leaf_wide * 0.5f + edge * (box_wide * 0.5f + 0.0055f), 0.0f, -0.0005f), new Vector3(0.011f, box_high, 0.001f), Quaternion.identity, false);
        }
        // folded edge inside the opening: open, a door not a sheet
        foreach (float edge in new[] { -1.0f, 1.0f })
        {
            solid(control.transform, "LeafEdge", PrimitiveType.Cube, sheet, new Vector3(leaf_wide * 0.5f, edge * (box_high * 0.5f - 0.006f), 0.005f), new Vector3(box_wide - 0.012f, 0.004f, 0.012f), Quaternion.identity, false);
            solid(control.transform, "LeafEdge", PrimitiveType.Cube, sheet, new Vector3(leaf_wide * 0.5f + edge * (box_wide * 0.5f - 0.008f), 0.0f, 0.005f), new Vector3(0.004f, box_high - 0.012f, 0.012f), Quaternion.identity, false);
        }
        shaped(control.transform, "Knob", turned("door_knob", 8, true, (0.0060f, 0.0f), (0.0060f, 0.0080f), (0.0125f, 0.0120f), (0.0125f, 0.0190f), (0.0080f, 0.0240f), (0.0f, 0.0240f)), black,
            new Vector3(leaf_wide - 0.035f, 0.0f, -0.001f - leaf_thick));
        if (warning)
            solid(control.transform, "Sign", PrimitiveType.Cube, body_material("eu07_stripe_yellow"), new Vector3(leaf_wide * 0.5f, -0.045f, -0.001f - leaf_thick), new Vector3(0.12f, 0.12f, 0.0012f), Quaternion.identity, false);
        if (warning)
            decal(control.transform, "danger_small", new Vector3(leaf_wide * 0.5f, -0.045f, -0.0022f - leaf_thick));
        make_label(control.transform, new Vector3(leaf_wide * 0.5f, 0.085f, -0.0015f - leaf_thick), label, control_caption);
        foreach (float up in new[] { -Mathf.Max(0.11f, box_high * 0.36f), Mathf.Max(0.11f, box_high * 0.36f) })
            solid(hinge, "Hinge", PrimitiveType.Cylinder, sheet, new Vector3(-0.004f, up, -0.005f), new Vector3(0.010f, 0.030f, 0.010f), Quaternion.identity, false);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, middle, new Vector3(leaf_wide, leaf_high, 0.014f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        GameObject point = node(control.transform, "Interaction Point", new Vector3(leaf_wide - 0.035f, 0.0f, -0.030f));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 3.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 8;
        lever.jointAxis = Vector3.up;    // about +y, along-x swings out of the panel
        lever.useSpring = true;
        lever.jointSpring = 40.0f;
        lever.jointDamper = 3.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = swings;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        feed(control, port);
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        sounds(lever, ("limitHit", "ItemCollision_MetalSheet_03"));
        lever.OnValidate();
    }

    // PIPE COCK HANDLE: hub, flat red lever, black ball; quarter turn about panel normal
    // along x (built) = open, 1; down = shut, 0; body is the model's
    private static void make_line_cock(Transform panel, string name, Vector2 at, float off, string port, string label)
    {
        const float length = 0.085f;
        Transform root = node(panel, name, new Vector3(at.x, at.y, -off)).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        GameObject grip = solid(control.transform, "Lever", PrimitiveType.Cube, white, new Vector3(length * 0.5f, 0.0f, -0.010f), new Vector3(length + 0.03f, 0.036f, 0.030f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        shaped(control.transform, "Hub", turned("cock_hub", 12, false, (0.016f, 0.0f), (0.016f, 0.012f), (0.012f, 0.016f), (0.0f, 0.016f)), red_cap, Vector3.zero);
        solid(control.transform, "Bar", PrimitiveType.Cube, red_cap, new Vector3(length * 0.5f, 0.0f, -0.007f), new Vector3(length, 0.020f, 0.008f), Quaternion.identity, false);
        solid(control.transform, "Grip", PrimitiveType.Sphere, black, new Vector3(length, 0.0f, -0.007f), Vector3.one * 0.024f, Quaternion.identity, false);
        LeverProxy lever = swing(control, grip, new Vector3(length, 0.0f, -0.012f), new Vector3(0.0f, 0.0f, 1.0f), -90.0f, 0.0f,
            sphere_area(root, name, new Vector3(length * 0.5f, 0.0f, -0.010f), 0.045f));
        feed(control, port);
        lever.OnValidate();
        if (label != null)
            make_label(panel, new Vector3(at.x, at.y - 0.075f, -0.002f), label, control_caption);
    }

    // SHOVED HANDLE: DV lever with two stops, dragged against a spring, stays where put
    private static LeverProxy swing(GameObject control, GameObject grip, Vector3 held_at, Vector3 axis, float from, float to, StaticInteractionAreaProxy area,
        string notch_sound = "LeverNotch_01_Deep", string limit_sound = "LeverNotch_07_Deep")
    {
        GameObject point = node(control.transform, "Interaction Point", held_at);
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { grip };
        lever.nonVrStaticInteractionArea = area;
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 30.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 2;
        lever.jointAxis = axis;
        lever.useSpring = true;
        lever.jointSpring = 400.0f;
        lever.jointDamper = 20.0f;
        lever.useLimits = true;
        lever.jointLimitMin = from;
        lever.jointLimitMax = to;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        sounds(lever, ("notch", notch_sound), ("limitHit", limit_sound));
        return lever;
    }

    // KNIFE SWITCH: blades from jaws into clips, tied by a handle bar; `across`: lie along x, swing sideways
    // else stand along y, pulled out and down; `poles`: blade positions; 0 shut, 1 = `opens` deg out
    private static void make_knife(Transform panel, string name, Vector2 at, bool across, float length, float[] poles, float opens, bool jaws, bool red,
        string port, string label, Vector3 label_at)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        const float proud = 0.022f;
        Vector3 along = across ? Vector3.right : Vector3.up, beside = across ? Vector3.up : Vector3.right;
        Material brass = body_material("eu07_brass"), copper = body_material("eu07_bus_copper");
        if (jaws)
        {
            foreach (float pole in poles)
            {
                foreach (float end in new[] { 0.0f, length })
                {
                    solid(root, "Insulator", PrimitiveType.Cylinder, ivory, along * end + beside * pole + new Vector3(0.0f, 0.0f, -proud * 0.4f),
                        new Vector3(0.026f, proud * 0.4f, 0.026f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
                    solid(root, "Jaw", PrimitiveType.Cube, brass, along * end + beside * pole + new Vector3(0.0f, 0.0f, -proud),
                        Vector3.one * 0.022f, Quaternion.identity, false);
                }
            }
        }
        GameObject control = node(root, "C_" + name, new Vector3(0.0f, 0.0f, -proud));
        float span = poles.Max() - poles.Min(), middle = (poles.Max() + poles.Min()) * 0.5f;
        foreach (float pole in poles)
            solid(control.transform, "Blade", PrimitiveType.Cube, copper, along * (length * 0.5f) + beside * pole + new Vector3(0.0f, 0.0f, -0.004f),
                along * (length + 0.016f) + beside * 0.007f + new Vector3(0.0f, 0.0f, 0.018f), Quaternion.identity, false);
        Vector3 tie_at = along * (length - 0.02f) + beside * middle + new Vector3(0.0f, 0.0f, -0.018f);
        solid(control.transform, "Tie", PrimitiveType.Cube, bakelite, tie_at, along * 0.018f + beside * (span + 0.030f) + new Vector3(0.0f, 0.0f, 0.014f), Quaternion.identity, false);
        solid(control.transform, "Stem", PrimitiveType.Cylinder, bakelite, tie_at + new Vector3(0.0f, 0.0f, -0.030f), new Vector3(0.016f, 0.026f, 0.016f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(control.transform, "Grip", PrimitiveType.Sphere, red ? red_cap : black, tie_at + new Vector3(0.0f, 0.0f, -0.062f), Vector3.one * 0.034f, Quaternion.identity, false);
        GameObject grip = solid(control.transform, "Lever", PrimitiveType.Cube, white, along * (length * 0.5f) + beside * middle + new Vector3(0.0f, 0.0f, -0.030f),
            along * (length + 0.03f) + beside * (span + 0.04f) + new Vector3(0.0f, 0.0f, 0.085f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());

        // about +y a blade lying along x swings out of the panel; about -x one standing along y does
        // sound: DV's big breaker (C_BreakerElectrics, Switch_08_Electric) on travel; small breaker click at the stop
        LeverProxy lever = swing(control, grip, tie_at + new Vector3(0.0f, 0.0f, -0.062f), across ? Vector3.up : Vector3.left, 0.0f, opens,
            sphere_area(root, name, along * (length * 0.6f) + beside * middle + new Vector3(0.0f, 0.0f, -proud - 0.035f), 0.055f),
            "Switch_08_Electric", "Switch_10_Solid");
        feed(control, port);
        lever.OnValidate();
        make_label(root, label_at, label, control_caption);
    }

    // COCK WITH LEVER: two positions, 28 deg either side of panel x, about the panel normal; boss, flat lever, round grip
    private static void make_cock(Transform panel, string name, Vector2 at, string port, string label, float dial = 0.0f)
    {
        // optional DIAL: 4 mm plate with drawn face (tools/dial_faces.py "interlock"): P by lever up, 0 by lever down
        // caption under the plate
        Transform root = node(panel, name, new Vector3(at.x, at.y, dial > 0.0f ? -0.004f : 0.0f)).transform;
        const float boss_out = 0.030f, length = 0.085f, turns = 28.0f;
        if (dial > 0.0f)
        {
            shaped(root, "Dial", turned("cock_dial_" + name, 32, false, (dial, 0.0f), (dial, 0.003f), (dial - 0.003f, 0.004f), (0.0f, 0.004f)), silver, new Vector3(0.0f, 0.0f, 0.004f));
            GameObject drawn = node(root, "Face", new Vector3(0.0f, 0.0f, -0.0015f));
            drawn.transform.localScale = new Vector3(2.0f * (dial - 0.004f), 2.0f * (dial - 0.004f), 1.0f);
            drawn.AddComponent<MeshFilter>().sharedMesh = disc();
            drawn.AddComponent<MeshRenderer>().sharedMaterial = dial_face("interlock");
        }
        shaped(root, "Boss", turned("cock_boss", 16, false, (0.024f, 0.0f), (0.024f, boss_out - 0.006f), (0.020f, boss_out), (0.0f, boss_out)), black, Vector3.zero);
        shaped(root, "Flange", slab("cock_flange", (0.034f, 0.034f, 0.004f, 0.0f), (0.034f, 0.034f, 0.004f, 0.004f), (0.0f, 0.0f, 0.0f, 0.004f)), black, Vector3.zero);
        GameObject control = node(root, "C_" + name, new Vector3(0.0f, 0.0f, -boss_out));
        GameObject grip = solid(control.transform, "Lever", PrimitiveType.Cube, white, new Vector3(length * 0.5f, 0.0f, -0.010f), new Vector3(length + 0.03f, 0.036f, 0.030f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        shaped(control.transform, "Hub", turned("cock_hub", 12, false, (0.016f, 0.0f), (0.016f, 0.012f), (0.012f, 0.016f), (0.0f, 0.016f)), red_cap, Vector3.zero);
        solid(control.transform, "Bar", PrimitiveType.Cube, red_cap, new Vector3(length * 0.5f, 0.0f, -0.007f), new Vector3(length, 0.020f, 0.008f), Quaternion.identity, false);
        solid(control.transform, "Grip", PrimitiveType.Sphere, black, new Vector3(length, 0.0f, -0.007f), Vector3.one * 0.024f, Quaternion.identity, false);

        LeverProxy lever = swing(control, grip, new Vector3(length, 0.0f, -0.012f), new Vector3(0.0f, 0.0f, 1.0f), -turns, turns,
            sphere_area(root, name, new Vector3(length * 0.5f, 0.0f, -boss_out - 0.010f), 0.045f));
        feed(control, port);
        lever.OnValidate();
        make_label(root, dial > 0.0f ? new Vector3(0.0f, -dial - 0.018f, 0.002f) : new Vector3(-0.078f, 0.0f, -0.002f), label, control_caption);
    }

    // toggle switch (his Pantograph rebuilt): lever out of the panel, hinged about panel X
    private static GameObject make_toggle(Transform panel, string name, Vector2 at, string port, string label, string key)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // TUMBLER as measured on the ref cab: flat ivory lever, ribbed end, 30 deg each way; black frame, two screws
        // sizes in tumbler(); grab = hidden box
        GameObject control = node(root, "C_" + name, new Vector3(0.0f, 0.0f, -tumbler_hub_out));
        GameObject lever = solid(control.transform, "Lever", PrimitiveType.Cube, white,
            new Vector3(0.0f, 0.0f, -0.0110f), new Vector3(0.010f, 0.009f, 0.024f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(lever.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(lever.GetComponent<MeshFilter>());
        shaped(control.transform, "Tumbler", tumbler(), ivory, Vector3.zero);
        shaped(root, "Frame", slab("tumbler_frame", (0.0070f, 0.0130f, 0.0018f, 0.0f), (0.0070f, 0.0130f, 0.0018f, 0.0016f), (0.0064f, 0.0124f, 0.0015f, 0.0021f),
            (0.0f, 0.0f, 0.0f, 0.0021f)), bakelite, Vector3.zero);
        foreach (float screw in new[] { -0.0165f, 0.0165f })
        {
            solid(root, "Screw", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, screw, -0.0006f), new Vector3(0.0052f, 0.0006f, 0.0052f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
            solid(root, "ScrewSlot", PrimitiveType.Cube, black, new Vector3(0.0f, screw, -0.0012f), new Vector3(0.0040f, 0.0007f, 0.0003f),
                Quaternion.Euler(0.0f, 0.0f, screw > 0.0f ? 25.0f : -60.0f), false);
        }

        ToggleSwitchProxy toggle = control.AddComponent<ToggleSwitchProxy>();
        toggle.colliderGameObjects = new[] { lever };
        toggle.nonVrStaticInteractionArea = sphere_area(root, name, new Vector3(0.0f, 0.0f, -0.010f), 0.024f);    // ref switches 50 mm apart
        toggle.jointAxis = new Vector3(1.0f, 0.0f, 0.0f);       // as his
        toggle.jointLimitMin = -30.0f;    // as the ref lever leans
        toggle.jointLimitMax =  30.0f;
        toggle.angleOffset = -90.0f;                             // editor gizmo only, as his
        serialised(toggle, "jointAxis", "jointLimitMin", "jointLimitMax");
        feed(control, port);
        if (key != null)
        {
            ToggleValueKeyboardInputProxy input = control.AddComponent<ToggleValueKeyboardInputProxy>();
            input.useAction = new AKeyboardInputProxy.ActionReference(key);
            input.OnValidate();
            serialised(input, "useAction", "_json");
        }
        control.AddComponent<ToggleSwitchZeroOneNamesProxy>();
        sounds(toggle, ("toggle", "Switch_05_Solid"));
        toggle.OnValidate();
        make_label(root, label_offset, label, control_caption);
        return control;
    }

    // SIGNAL LAMP: LampControlProxy + LampPortReaderProxy on the holder (LampPortReader.Init: GetComponent<LampControl>())
    // lens a child; mod writes 0/1; no LampLogic (no sim port to leave unconnected)
    private static void make_lamp(Transform panel, string name, Vector2 at, Color dark, string port, string label)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // lamp as measured on the ref cab: square black housing 48 mm, 12.3 mm out; curved glass cover 31.6 mm, 4.4 mm crown
        // one mesh, so all lights together
        shaped(root, "Housing", block("lamp_housing", (0.0240f, 0.0030f, 0.0f), (0.0240f, 0.0030f, 0.0110f), (0.0229f, 0.0026f, 0.0123f), (0.0f, 0.0f, 0.0123f)), bakelite, Vector3.zero);
        GameObject glass = shaped(root, "emitter", lamp_lens(), lens(name, dark), new Vector3(0.0f, 0.0f, -0.0121f));

        IndicatorEmissionProxy emission = glass.AddComponent<IndicatorEmissionProxy>();
        emission.binary = true;
        emission.valueThreshold = 0.5f;
        emission.lag = 0.05f;
        emission.emissionColor = new Color(Mathf.Min(1.0f, dark.r * 3.0f), Mathf.Min(1.0f, dark.g * 3.0f), Mathf.Min(1.0f, dark.b * 3.0f)) * 3.0f;
        emission.renderers = new Renderer[] { glass.GetComponent<MeshRenderer>() };
        serialised(emission, "binary", "valueThreshold", "emissionColor", "renderers");

        LampControlProxy lamp = root.gameObject.AddComponent<LampControlProxy>();
        lamp.lampInd = emission;
        lamp.lampState = LampControlProxy.LampState.Off;
        serialised(lamp, "lampInd", "lampState");
        LampPortReaderProxy reader = root.gameObject.AddComponent<LampPortReaderProxy>();
        reader.lampStatePortId = port;
        serialised(reader, "lampStatePortId");
        make_label(root, label_offset, label, control_caption);
    }

    // NEEDLE DIAL; reader: "port:<id>" or "cylinder" / "pipe" / "reservoir"
    // reader on the indicator's object (IndicatorPortReader.Init: GetComponent<Indicator>())
    private static void make_gauge(Transform panel, string name, Vector2 at, float low, float high,
        string reader, float scale, string label,
        float sweep = 135.0f, float smooth = 0.3f, float diameter = 0.090f, int ticks = 5, string face = null, bool rim = true)
    {
        float radius = diameter * 0.5f;
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // panel -z is OUT; layers mm apart against z-fighting; white disc only where no face is drawn
        if (face == null)
            solid(root, "Dial", PrimitiveType.Cylinder, white, new Vector3(0.0f, 0.0f, -0.004f),
                new Vector3(diameter, 0.001f, diameter), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        if (face != null)
        {
            // drawn face (tools/dial_faces.py) 1 mm over the white disc, black rim round it
            GameObject drawn = node(root, "Face", new Vector3(0.0f, 0.0f, -0.006f));
            drawn.transform.localScale = new Vector3(diameter, diameter, 1.0f);
            drawn.AddComponent<MeshFilter>().sharedMesh = disc();
            drawn.AddComponent<MeshRenderer>().sharedMaterial = dial_face(face);
            if (rim)
            {
                GameObject ring = node(root, "Rim", new Vector3(0.0f, 0.0f, -0.005f));
                ring.transform.localRotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
                ring.AddComponent<MeshFilter>().sharedMesh = bezel(radius);
                ring.AddComponent<MeshRenderer>().sharedMaterial = black;
            }
        }
        for (int index = 0; index < ticks && face == null; ++index)
        {
            Quaternion turn = Quaternion.AngleAxis(Mathf.Lerp(-sweep, sweep, index / (float) (ticks - 1)), Vector3.back);
            solid(root, "Tick" + index, PrimitiveType.Cube, black, turn * new Vector3(0.0f, radius * 0.89f, -0.0065f),
                new Vector3(0.003f, radius * 0.18f, 0.002f), turn, false);
        }
        Transform pivot = node(root, "Pivot", Vector3.zero).transform;
        // slim pointer to the graduations, short tail, small boss (thick bars hide figures)
        float needle = Mathf.Max(0.0012f, radius * 0.032f);
        solid(pivot, "Needle", PrimitiveType.Cube, black, new Vector3(0.0f, radius * 0.335f, -0.010f),
            new Vector3(needle, radius * 0.97f, 0.0015f), Quaternion.identity, false);
        solid(pivot, "NeedleBoss", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.0f, -0.010f),
            new Vector3(radius * 0.16f, 0.0025f, radius * 0.16f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);    // face 1.7 mm proud of the pointer's

        bool port = reader.StartsWith("port:", StringComparison.Ordinal);
        IndicatorGaugeProxy gauge = port ? root.gameObject.AddComponent<IndicatorGaugeLaggingProxy>()
                                         : root.gameObject.AddComponent<IndicatorGaugeProxy>();
        gauge.minValue = low;
        gauge.maxValue = high;
        gauge.needle = pivot;
        gauge.minAngle = -sweep;
        gauge.maxAngle =  sweep;
        gauge.rotationAxis = Vector3.back;
        gauge.gizmoRadius = radius;
        IndicatorGaugeLaggingProxy lagging = gauge as IndicatorGaugeLaggingProxy;
        if (lagging != null)
            lagging.smoothTime = smooth;
        serialised(gauge, "minValue", "maxValue", "needle", "minAngle", "maxAngle", "rotationAxis");

        if (port)
        {
            IndicatorPortReaderProxy source = root.gameObject.AddComponent<IndicatorPortReaderProxy>();
            source.portId = reader.Substring(5);
            source.useAbsoluteValue = true;
            source.valueMultiplier = scale;
            serialised(source, "portId", "useAbsoluteValue", "valueMultiplier");
        }
        else if (reader == "cylinder")  root.gameObject.AddComponent<IndicatorBrakeCylinderReaderProxy>();
        else if (reader == "pipe")      root.gameObject.AddComponent<IndicatorBrakePipeReaderProxy>();
        else if (reader == "reservoir") root.gameObject.AddComponent<IndicatorBrakeReservoirReaderProxy>();
        else throw new ArgumentException("no such reader: " + reader);
        if (label != null)
            make_label(root, new Vector3(0.0f, -(radius + 0.022f), -0.004f), label);
    }

    // OPENING SIDE PANE: DV "puller" (free along its y between stops, 0 shut, held per tenth of travel)
    // own track 25 mm inside the wall (30 mm wall, 12 mm pane) so it passes the fixed pane
    // travel: driver 0.40 m, other side 0.18 m; walls draw in 49 mm/m toward the front; grab = latch, not the glass
    // OpenableControlProxy: a way out for sound, as a door
    private const float window_track = 0.025f;

    private static void make_cab_window(Transform parent, GameObject model, string part)
    {
        if (!door_frames.TryGetValue(part, out Matrix4x4 frame))
            throw new KeyNotFoundException("the body brought no " + part);
        Transform source = child(model.transform, part);
        Mesh mesh = source.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] points = mesh.vertices.Select(point => frame.MultiplyPoint3x4(point)).ToArray();
        Vector3 centre = points.Aggregate(Vector3.zero, (sum, point) => sum + point) / points.Length;
        float toward_end = Mathf.Sign(centre.z);
        Vector3 fore = Vector3.zero, aft = Vector3.zero;
        foreach (Vector3 point in points)
        {
            if ((point.z - centre.z) * toward_end > 0.0f) fore += point;
            else aft += point;
        }
        Vector3 open = fore - aft;
        open.y = 0.0f;
        open.Normalize();
        Vector3 inward = new Vector3(open.z, 0.0f, -open.x);
        if (inward.x * centre.x > 0.0f)
            inward = -inward;
        float wide = points.Max(point => Vector3.Dot(point, open)) - points.Min(point => Vector3.Dot(point, open));
        float high = points.Max(point => point.y) - points.Min(point => point.y);
        float thick = points.Max(point => Vector3.Dot(point, inward)) - points.Min(point => Vector3.Dot(point, inward));
        if (wide < 0.40f || wide > 0.65f || high < 0.55f || high > 0.75f || thick > 0.02f || Mathf.Abs(open.x) > 0.08f || Mathf.Abs(centre.x) < 1.35f)
            throw new InvalidDataException($"window pane {part} came in wrong: centre {centre:F3} along {open:F3}, {wide:F3} wide, {high:F3} high, {thick:F3} thick");
        float travel = wide > 0.55f ? 0.40f : 0.18f;
        // left/right as seen in that cab: A looks +z, B -z
        string name = "Window" + (centre.x * toward_end < 0.0f ? "Left" : "Right") + (toward_end > 0.0f ? "A" : "B");
        Debug.Log($"EU07 BUILD: {name} from {part}: centre {centre:F3} slides {travel:F2} m along {open:F3}, in is {inward:F3}; {wide:F3} wide {high:F3} high {thick:F3} thick");

        // own frame: y against opening (puller goes to -y), z into the cab, x up/down
        Quaternion turn = Quaternion.LookRotation(inward, -open);
        Transform root = node(parent, name, centre + inward * window_track).transform;
        root.localRotation = turn;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        Matrix4x4 own = Matrix4x4.TRS(centre, turn, Vector3.one).inverse * frame;
        GameObject pane = node(control.transform, "Pane", own.GetColumn(3));
        pane.transform.localRotation = own.rotation;
        pane.transform.localScale = own.lossyScale;
        pane.AddComponent<MeshFilter>().sharedMesh = mesh;
        pane.AddComponent<MeshRenderer>().sharedMaterials = source.GetComponent<MeshRenderer>().sharedMaterials.Select(material => body_material(material.name)).ToArray();
        // latch: black bar on the inside face, 35 mm from the rear edge
        Vector3 latch = new Vector3(0.0f, wide * 0.5f - 0.035f, thick * 0.5f);
        solid(control.transform, "Latch", PrimitiveType.Cube, black, latch + new Vector3(0.0f, 0.0f, 0.009f), new Vector3(0.090f, 0.016f, 0.018f), Quaternion.identity, false);
        solid(control.transform, "LatchFoot", PrimitiveType.Cube, black, latch + new Vector3(0.0f, 0.0f, 0.0015f), new Vector3(0.120f, 0.026f, 0.003f), Quaternion.identity, false);
        GameObject grip = solid(control.transform, "Handle", PrimitiveType.Cube, white, latch + new Vector3(0.0f, 0.0f, 0.020f), new Vector3(0.180f, 0.070f, 0.060f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        // middle of travel: stops half the travel either side
        GameObject anchor = node(root, "Anchor", new Vector3(0.0f, -travel * 0.5f, 0.0f));

        PullerProxy slide = control.AddComponent<PullerProxy>();
        slide.colliderGameObjects = new[] { grip };
        slide.rigidbodyMass = 3.0f;
        // NO DRAG: drag acts on ground speed, and the body is free along the running direction
        slide.rigidbodyDrag = 0.0f;
        slide.useSteppedPuller = true;
        slide.notches = 10;
        slide.scrollWheelHoverScroll = 0.1f;    // a tenth of travel per wheel tick
        slide.useCustomConnectionAnchor = true;
        slide.connectionAnchor = anchor.transform;
        slide.linearLimit = travel * 0.5f;
        serialised(slide, "rigidbodyDrag", "useSteppedPuller", "notches", "scrollWheelHoverScroll", "useCustomConnectionAnchor", "connectionAnchor", "linearLimit");
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        sounds(slide, ("drag", "LeverDrag_02_MetalLightDirt"), ("limitHit", "LeverLimit_01_PlasticLight"));
        slide.OnValidate();
    }

    // CONTACTOR ARMS + ARC CHUTES at the model's hinges (model/eu07_contactors.txt; vehicle = (-x, z, -y) of model)
    // hinge frame: x along row, y up, z to passage; Arm +24 deg about x = open (34 mm gap); mod turns it by NAME
    // Chute: own renderer, mod soots it (eu07_driver.cs, watch_contactors); 48 as eu07_contactors.cs, named middle -> cab
    // cab A upper: SL1 SL3 IS IM IR1 IR2 SR1-SR8, lower SR9-SR20; cab B upper SR21-SR30 SP1 SP2, lower SP3-SP12
    private static readonly string[] contactor_names =
        new[] { "SL1", "SL3", "IS", "IM", "IR1", "IR2" }.Concat(Enumerable.Range(1, 30).Select(number => "SR" + number)).Concat(Enumerable.Range(1, 12).Select(number => "SP" + number)).ToArray();


    private static void make_contactors(Transform parent)
    {
        string list = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_contactors.txt"));
        List<(int shelf, Vector3 hinge)> places = new List<(int shelf, Vector3 hinge)>();
        foreach (string line in File.ReadAllLines(list))
        {
            string[] words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 6 || line.StartsWith("#"))
                continue;
            float[] figures = words.Skip(2).Select(word => float.Parse(word, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            places.Add((int.Parse(words[1]), new Vector3(-figures[0], figures[2], -figures[1])));
        }
        // desk end (+z) first, upper row before lower, middle outward
        places = places.OrderBy(place => place.hinge.z > 0.0f ? 0 : 1).ThenBy(place => -place.shelf).ThenBy(place => Mathf.Abs(place.hinge.z)).ToList();
        if (places.Count != contactor_names.Length)
            throw new InvalidDataException($"{places.Count} contactors in model/eu07_contactors.txt, and the circuit has {contactor_names.Length}");
        // arm: 32 x 16 mm bar, 14 mm under hinge to 92 over; contact 18.5 mm toward the cage
        // two materials, bar first: mod colours bar by heat, contact by soot
        Mesh arm = new Mesh { name = "contactor_arm" };
        arm.CombineMeshes(new[]
        {
            new CombineInstance { mesh = blocks("contactor_arm_bar", (new Vector3(-0.016f, -0.014f, -0.008f), new Vector3(0.016f, 0.092f, 0.008f))), transform = Matrix4x4.identity },
            new CombineInstance { mesh = blocks("contactor_arm_contact", (new Vector3(-0.018f, 0.062f, -0.0265f), new Vector3(0.018f, 0.092f, -0.008f))), transform = Matrix4x4.identity },
        }, false, false);
        arm.RecalculateBounds();
        AssetDatabase.CreateAsset(arm, $"{target}/EU07_contactor_arm.asset");
        // chute: 108 mm wide, 92-240 mm over hinge, sloped shoulders; 22-192 mm behind the hinge
        Mesh chute = slab("contactor_chute", 0.054f, new Vector2(-0.192f, 0.092f), new Vector2(-0.022f, 0.092f), new Vector2(-0.022f, 0.200f),
            new Vector2(-0.045f, 0.240f), new Vector2(-0.169f, 0.240f), new Vector2(-0.192f, 0.200f));
        Material copper = body_material("eu07_contact");
        // SOOT painted in six stages: copper film from the arc, grey-black, brownish where thin
        // round the passage-side mouth (fan upward), the top, side front edges; ContactorSoot object holds them for the mod
        Material[] stages = Enumerable.Range(0, soot_stages).Select(stage => sooted_chute(stage)).ToArray();
        GameObject keeper = shaped(parent, "ContactorSoot", chute, stages[0], Vector3.zero);
        keeper.GetComponent<MeshRenderer>().sharedMaterials = stages;
        keeper.GetComponent<MeshRenderer>().enabled = false;
        for (int index = 0; index < places.Count; ++index)
        {
            Vector3 hinge = places[index].hinge;
            Transform root = node(parent, "Contactor_" + contactor_names[index], hinge).transform;
            root.localRotation = Quaternion.LookRotation(new Vector3(-Mathf.Sign(hinge.x), 0.0f, 0.0f), Vector3.up);
            shaped(root, "Chute", chute, stages[0], Vector3.zero);
            shaped(root, "Arm", arm, copper, Vector3.zero).GetComponent<MeshRenderer>().sharedMaterials = new[] { copper, copper };
        }
        Debug.Log($"EU07 BUILD: {places.Count} contactors: {contactor_names[0]} at {places[0].hinge:F3}, {contactor_names[places.Count - 1]} at {places[places.Count - 1].hinge:F3}");
    }

    // chute material per stage, 256 x 128: LEFT half = two sides (back to front, up), RIGHT = edge faces (across, up); see slab()
    private const int soot_stages = 6;

    private static Material sooted_chute(int stage)
    {
        const int size = 128;
        // ramp(), not Mathf.SmoothStep (see ramp())
        float much = stage / (soot_stages - 1.0f);
        Color clean = new Color(0.78f, 0.76f, 0.70f), filmed = new Color(0.40f, 0.35f, 0.30f), burnt = new Color(0.050f, 0.048f, 0.046f);
        Texture2D picture = new Texture2D(size * 2, size, TextureFormat.RGB24, true) { name = $"EU07_chute_soot_{stage}", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
        Color[] dots = new Color[size * 2 * size];
        for (int row = 0; row < size; ++row)
        {
            float up = row / (size - 1.0f);
            for (int column = 0; column < size * 2; ++column)
            {
                bool side = column < size;
                float across = (side ? column : column - size) / (size - 1.0f);
                // cement: slightly uneven
                float grain = Mathf.PerlinNoise(column * 0.19f + 3.1f, row * 0.19f + 7.7f);
                // unevenness: streaks up the face + blotches
                float uneven = 0.55f + 0.55f * Mathf.PerlinNoise(column * 0.085f + 11.3f, row * 0.016f + 2.9f) + 0.40f * Mathf.PerlinNoise(column * 0.035f + 5.5f, row * 0.045f + 9.1f);
                float top = ramp(0.80f, 1.0f, up), lies;
                if (side)
                {
                    // side: by front edge, most at the foot (contacts), and under the top
                    float by_front = ramp(0.70f, 1.0f, across) * (1.0f - 0.55f * up);
                    float over_contacts = Mathf.Exp(-((1.0f - across) * (1.0f - across) + up * up) / 0.07f);
                    lies = 0.75f * by_front + 0.9f * over_contacts + 0.65f * top;
                }
                else
                {
                    // passage face: fan from the gap at the foot, widening and thinning up; plus the top
                    float wide = 0.13f + 0.42f * up, off = (across - 0.5f) / wide;
                    float fan = Mathf.Exp(-off * off) * (1.0f - 0.45f * ramp(0.0f, 0.75f, up));
                    lies = 1.15f * fan + 1.0f * top;
                }
                float soot = Mathf.Clamp01(much * (0.55f + 0.95f * much) * lies * uneven * 1.45f);
                Color paint = soot < 0.45f ? Color.Lerp(clean, filmed, soot / 0.45f) : Color.Lerp(filmed, burnt, (soot - 0.45f) / 0.55f);
                dots[row * size * 2 + column] = paint * (0.93f + 0.14f * grain);
            }
        }
        picture.SetPixels(dots);
        picture.Apply(true);
        // debug copy beside the other generated pictures; not read
        File.WriteAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, $"../../model/paint/chute_soot_{stage}.png")), picture.EncodeToPNG());
        AssetDatabase.CreateAsset(picture, $"{target}/EU07_chute_soot_{stage}.asset");
        Material made = new Material(Shader.Find("Standard")) { color = Color.white, mainTexture = picture };
        made.SetFloat("_Metallic", 0.0f);
        made.SetFloat("_Glossiness", 0.12f);
        AssetDatabase.CreateAsset(made, $"{target}/EU07_chute_soot_{stage}.mat");
        return made;
    }

    // boxes as one mesh, flat-lit, each least -> greatest corner
    private static Mesh blocks(string name, params (Vector3 least, Vector3 most)[] boxes)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        foreach ((Vector3 least, Vector3 most) in boxes)
        {
            Vector3 middle = (least + most) * 0.5f, half = (most - least) * 0.5f;
            foreach (Vector3 way in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                // the two directions across this face
                Vector3 one = Mathf.Abs(way.x) > 0.5f ? Vector3.up : Vector3.right, other = Vector3.Cross(way, one);
                Vector3 centre = middle + Vector3.Scale(way, half);
                Vector3[] corners = { centre - Vector3.Scale(one, half) - Vector3.Scale(other, half), centre + Vector3.Scale(one, half) - Vector3.Scale(other, half),
                                      centre + Vector3.Scale(one, half) + Vector3.Scale(other, half), centre - Vector3.Scale(one, half) + Vector3.Scale(other, half) };
                flat_face(points, normals, triangles, way, corners);
            }
        }
        return keep_shape(name, points, normals, triangles);
    }

    // SLAB from an outline (convex) in its own (z, y), `half` either way along x; flat-lit
    private static Mesh slab(string name, float half, params Vector2[] outline)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        foreach (float side in new[] { -1.0f, 1.0f })
            flat_face(points, normals, triangles, new Vector3(side, 0.0f, 0.0f), outline.Select(at => new Vector3(side * half, at.y, at.x)).ToArray());
        Vector2 middle = outline.Aggregate(Vector2.zero, (sum, at) => sum + at) / outline.Length;
        for (int index = 0; index < outline.Length; ++index)
        {
            Vector2 from = outline[index], to = outline[(index + 1) % outline.Length];
            Vector2 out_of = new Vector2(to.y - from.y, from.x - to.x).normalized;
            if (Vector2.Dot(out_of, (from + to) * 0.5f - middle) < 0.0f)
                out_of = -out_of;
            flat_face(points, normals, triangles, new Vector3(0.0f, out_of.y, out_of.x),
                new[] { new Vector3(-half, from.y, from.x), new Vector3(half, from.y, from.x), new Vector3(half, to.y, to.x), new Vector3(-half, to.y, to.x) });
        }
        // UVs: up = up the slab; LEFT half = cut faces (along outline), RIGHT = edge faces (across); inset at the join
        float low = outline.Min(at => at.y), high = outline.Max(at => at.y), back = outline.Min(at => at.x), front = outline.Max(at => at.x);
        List<Vector2> laid = points.Select((point, index) => new Vector2(
            Mathf.Abs(normals[index].x) > 0.5f ? Mathf.Lerp(0.004f, 0.496f, Mathf.InverseLerp(back, front, point.z)) : Mathf.Lerp(0.504f, 0.996f, Mathf.InverseLerp(-half, half, point.x)),
            Mathf.InverseLerp(low, high, point.y))).ToList();
        return keep_shape(name, points, normals, triangles, laid);
    }

    // one flat face: corners in order, fanned from the first, facing `lit`
    private static void flat_face(List<Vector3> points, List<Vector3> normals, List<int> triangles, Vector3 lit, Vector3[] corners)
    {
        for (int index = 1; index + 1 < corners.Length; ++index)
        {
            Vector3 a = corners[0], b = corners[index], c = corners[index + 1];
            int first = points.Count;
            bool turned_over = Vector3.Dot(Vector3.Cross(b - a, c - a), lit) < 0.0f;
            points.AddRange(turned_over ? new[] { a, c, b } : new[] { a, b, c });
            normals.AddRange(new[] { lit, lit, lit });
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
    }

    private static Mesh keep_shape(string name, List<Vector3> points, List<Vector3> normals, List<int> triangles, List<Vector2> laid = null)
    {
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        if (laid != null)
            made.SetUVs(0, laid);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // SUN VISOR: smoked sheet on two clips on the model's rail (eu07_blockout.py; vehicle = (-x, z, -y) of model)
    // rail = hinge; 0 = up, 17 mm off the face; 1 = 166 deg over, hanging along the glass; six stops, notched lever
    private static readonly (Vector3 from, Vector3 to)[] visor_rails =
    {
        (new Vector3( 0.4700f,  6.9677f, 3.3160f), new Vector3( 1.1300f,  6.7869f, 3.3160f)),
        (new Vector3(-0.4700f,  6.9677f, 3.3160f), new Vector3(-1.1300f,  6.7869f, 3.3160f)),
        (new Vector3(-0.4700f, -6.9677f, 3.3160f), new Vector3(-1.1300f, -6.7869f, 3.3160f)),
        (new Vector3( 0.4700f, -6.9677f, 3.3160f), new Vector3( 1.1300f, -6.7869f, 3.3160f)),
    };

    private static void make_visor(Transform parent, Vector3 from, Vector3 to)
    {
        const float wide = 0.62f, tall = 0.16f, foot = 0.010f, thick = 0.005f, comes_over = 166.0f;
        // at rest leans one stop into the cab (straight up clips the curved ceiling)
        const float leans = comes_over / 6.0f;
        Vector3 one = new Vector3(-from.x, from.z, -from.y), other = new Vector3(-to.x, to.z, -to.y);
        Vector3 middle = (one + other) * 0.5f, along = (other - one).normalized;
        float toward_end = Mathf.Sign(middle.z);
        Vector3 to_glass = Vector3.Cross(along, Vector3.up).normalized;
        if (to_glass.z * toward_end < 0.0f)
            to_glass = -to_glass;
        string name = "Visor" + (middle.x * toward_end < 0.0f ? "Left" : "Right") + (toward_end > 0.0f ? "A" : "B");
        // own frame: x along rail, y up, z toward the glass
        Transform root = node(parent, name, middle).transform;
        root.localRotation = Quaternion.LookRotation(to_glass, Vector3.up);
        GameObject control = node(root, "C_" + name, Vector3.zero);
        control.transform.localRotation = Quaternion.AngleAxis(leans, Vector3.left);
        solid(control.transform, "Sheet", PrimitiveType.Cube, body_material("eu07_visor"), new Vector3(0.0f, foot + tall * 0.5f, 0.0f), new Vector3(wide, tall, thick), Quaternion.identity, false);
        foreach (float clip in new[] { -0.20f, 0.20f })
            solid(control.transform, "Clip", PrimitiveType.Cube, black, new Vector3(clip, 0.006f, 0.0f), new Vector3(0.036f, 0.032f, 0.022f), Quaternion.identity, false);
        GameObject grip = solid(control.transform, "Handle", PrimitiveType.Cube, white, new Vector3(0.0f, foot + tall * 0.5f, 0.0f), new Vector3(wide, tall + 0.02f, 0.030f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, foot + tall, -0.02f));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { grip };
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 2.0f;
        lever.rigidbodyDrag = 15.0f;
        // wheel down brings it down
        lever.scrollWheelHoverScroll = -1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 6;
        lever.jointAxis = Vector3.left;    // about -x, up-along-y comes over toward -z: into the cab
        lever.useSpring = true;
        lever.jointSpring = 40.0f;
        lever.jointDamper = 3.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = comes_over - leans;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        sounds(lever, ("notch", "LeverNotch_04_Tick"), ("limitHit", "LeverLimit_01_PlasticLight"));
        lever.OnValidate();
    }

    // DOOR: model leaf on a notched lever, shut (0) to just over 90 deg INTO the cab; OpenableControlProxy lets sound in
    // grab = leaf-sized box
    private static readonly Dictionary<string, Matrix4x4> door_frames = new Dictionary<string, Matrix4x4>();

    private static void make_door(Transform parent, GameObject model, string part, string name, Vector3 hinge, bool across = false, float opens = 92.0f)
    {
        if (!door_frames.TryGetValue(part, out Matrix4x4 frame))
            throw new KeyNotFoundException("the body brought no " + part);
        Transform source = child(model.transform, part);
        Mesh mesh = UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
        mesh.name = name;
        AssetDatabase.CreateAsset(mesh, $"{target}/EU07_{name}.asset");
        // leaf hung where it stood in the body (`frame`: mesh place incl. model file and template turns)
        Transform root = node(parent, name, hinge).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        GameObject leaf = node(control.transform, "Leaf", (Vector3) frame.GetColumn(3) - hinge);
        leaf.transform.localRotation = frame.rotation;
        leaf.transform.localScale = frame.lossyScale;
        // one object per leaf material: DV outlines only a mesh's FIRST material
        Material[] materials = source.GetComponent<MeshRenderer>().sharedMaterials;
        for (int index = 0; index < mesh.subMeshCount; ++index)
        {
            // only this material's points (else the piece has the whole leaf's bounds, confusing desk_clearance.py)
            int[] corners = mesh.GetTriangles(index);
            int[] used = corners.Distinct().ToArray();
            Dictionary<int, int> place = Enumerable.Range(0, used.Length).ToDictionary(at => used[at], at => at);
            Vector3[] points = mesh.vertices, normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] atlas = mesh.uv, world = mesh.uv2;
            Mesh piece = new Mesh { name = $"{name}_{index}" };
            piece.SetVertices(used.Select(at => points[at]).ToList());
            piece.SetNormals(used.Select(at => normals[at]).ToList());
            // and the paint UVs (else one grey)
            if (atlas.Length == points.Length)
                piece.SetUVs(0, used.Select(at => atlas[at]).ToList());
            if (world.Length == points.Length)
                piece.SetUVs(1, used.Select(at => world[at]).ToList());
            if (tangents.Length == points.Length)
                piece.SetTangents(used.Select(at => tangents[at]).ToList());
            piece.SetTriangles(corners.Select(at => place[at]).ToArray(), 0);
            piece.RecalculateBounds();
            AssetDatabase.AddObjectToAsset(piece, mesh);
            GameObject shown = node(leaf.transform, materials[index].name, Vector3.zero);
            shown.AddComponent<MeshFilter>().sharedMesh = piece;
            shown.AddComponent<MeshRenderer>().sharedMaterial = body_material(materials[index].name);
        }
        // HANDLES at model/eu07_door_handles.txt, shaped as DV's cab doors and the ref: door_lever() (22 x 14 x 128 mm)
        // on a 40 x 190 plate; inside cab outside doors a lock case 111 x 88 on 118 x 228 plate, as the ref
        // turning part = DoorHandle (mod dips it, eu07_driver.cs); lever toward the hinge, "Lever" built along +x
        int handle_number = 0;
        foreach ((Vector3 spindle, Vector3 out_of, Vector3 along) in door_handle_places(part))
        {
            Transform seat = node(control.transform, "HandleSeat" + handle_number, spindle - hinge).transform;
            seat.localRotation = Quaternion.LookRotation(-out_of, Vector3.up);    // -z out of the leaf, as on a panel
            float side = Mathf.Sign((Quaternion.Inverse(seat.localRotation) * along).x);
            bool lock_case = !across && handle_number == 1;
            float off_leaf = 0.0f;
            if (lock_case)
            {
                const float case_out = 0.054f;
                Vector3 case_at = new Vector3(side * 0.018f, -0.017f, 0.0f);
                shaped(seat, "LockPlate", slab("door_case_plate", (0.059f, 0.114f, 0.004f, 0.0f), (0.059f, 0.114f, 0.004f, 0.0030f), (0.0f, 0.0f, 0.0f, 0.0030f)),
                    door_metal, new Vector3(side * 0.018f, -0.073f, 0.0f));
                shaped(seat, "LockCase", slab("door_lock_case", (0.0555f, 0.044f, 0.005f, 0.0f), (0.0555f, 0.044f, 0.005f, case_out - 0.003f), (0.0530f, 0.0415f, 0.004f, case_out),
                    (0.0f, 0.0f, 0.0f, case_out)), black, case_at);
                shaped(seat, "LockCaseLower", slab("door_lock_case_lower", (0.051f, 0.036f, 0.005f, 0.0f), (0.051f, 0.036f, 0.005f, 0.034f), (0.0485f, 0.0335f, 0.004f, 0.037f),
                    (0.0f, 0.0f, 0.0f, 0.037f)), black, new Vector3(side * 0.018f, -0.132f, 0.0f));
                solid(seat, "CaseScrew", PrimitiveType.Cylinder, door_metal, case_at + new Vector3(-side * 0.040f, -0.030f, -case_out), new Vector3(0.007f, 0.0006f, 0.007f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
                solid(seat, "CaseScrew", PrimitiveType.Cylinder, door_metal, case_at + new Vector3(side * 0.040f, 0.030f, -case_out), new Vector3(0.007f, 0.0006f, 0.007f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
                off_leaf = case_out;
            }
            else
            {
                shaped(seat, "Backplate", slab("door_backplate", (0.020f, 0.095f, 0.005f, 0.0f), (0.020f, 0.095f, 0.005f, 0.0025f), (0.0185f, 0.0935f, 0.004f, 0.0040f),
                    (0.0f, 0.0f, 0.0f, 0.0040f)), door_metal, new Vector3(0.0f, -0.050f, 0.0f));
                shaped(seat, "LockRose", turned("door_lock_rose", 20, false, (0.0125f, 0.0040f), (0.0125f, 0.0075f), (0.0110f, 0.0090f), (0.0f, 0.0090f)), door_metal, new Vector3(0.0f, -0.100f, 0.0f));
                solid(seat, "Keyhole", PrimitiveType.Cube, black, new Vector3(0.0f, -0.100f, -0.0091f), new Vector3(0.0030f, 0.0100f, 0.0006f), Quaternion.identity, false);
            }
            Transform turning = node(seat, "DoorHandle", new Vector3(0.0f, 0.0f, -off_leaf)).transform;
            shaped(turning, "Lever", door_lever(lock_case ? 0.022f : 0.040f), door_metal, Vector3.zero).transform.localScale = new Vector3(side, 1.0f, 1.0f);
            ++handle_number;
        }
        if (handle_number != 2)
            throw new InvalidDataException($"door {name}: {handle_number} handles in model/eu07_door_handles.txt for {part}, expected 2");
        // leaf box in hinge frame, whatever the mesh orientation
        Bounds box = new Bounds(leaf.transform.localPosition + leaf.transform.localRotation * Vector3.Scale(mesh.bounds.center, leaf.transform.localScale), Vector3.zero);
        foreach (float x in new[] { -1.0f, 1.0f })
            foreach (float y in new[] { -1.0f, 1.0f })
                foreach (float z in new[] { -1.0f, 1.0f })
                    box.Encapsulate(leaf.transform.localPosition + leaf.transform.localRotation
                        * Vector3.Scale(mesh.bounds.center + Vector3.Scale(mesh.bounds.extents, new Vector3(x, y, z)), leaf.transform.localScale));
        Debug.Log($"EU07 BUILD: {name} hinge {hinge:F3} leaf centre {box.center:F3} size {box.size:F3}");
        // leaf must start AT the hinge foot, reach toward its cab's front, wall thickness to the cab side
        float toward_front = Mathf.Sign(hinge.z), into_cab = -Mathf.Sign(hinge.x);
        // ACROSS door (cab back wall): from hinge foot toward that cab's right (+x cab A, -x cab B), room side of hinge
        if (across
            ? Mathf.Abs(box.min.y) > 0.01f || box.size.y < 1.8f || box.size.y > 1.9f || box.size.x < 0.62f || box.size.x > 0.76f
              || Mathf.Abs((toward_front > 0.0f ? box.min.x : -box.max.x)) > 0.01f || Mathf.Abs(box.center.z) > 0.06f || box.center.z * toward_front > 0.001f
            : Mathf.Abs(box.min.y) > 0.005f || box.size.y < 1.6f || box.size.y > 1.8f || box.size.z < 0.45f || box.size.z > 0.56f
              || Mathf.Abs((toward_front > 0.0f ? box.min.z : -box.max.z)) > 0.01f || Mathf.Abs(box.center.x) > 0.06f || box.center.x * into_cab < -0.001f)
            throw new InvalidDataException($"door {name} came in wrong: centre {box.center:F3} size {box.size:F3}");
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, box.center, box.size, Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        // player STOP box turning with the leaf (grab box doesn't block); kept on the walk layer, a bit smaller than the leaf
        GameObject stop = node(control.transform, "Stop", box.center);
        stop.AddComponent<BoxCollider>().size = box.size - (across ? new Vector3(0.03f, 0.03f, 0.0f) : new Vector3(0.0f, 0.03f, 0.03f));
        InteriorNonStandardLayerProxy solid_to_walk_into = stop.AddComponent<InteriorNonStandardLayerProxy>();
        solid_to_walk_into.Layer = DVLayer.Train_Walkable;
        solid_to_walk_into.includeChildren = false;
        // grabbed by free edge at handle height
        Vector3 grip = across
            ? new Vector3(box.center.x + Mathf.Sign(box.center.x) * (box.extents.x - 0.07f), 1.00f, box.center.z)
            : new Vector3(box.center.x, 0.77f, box.center.z + Mathf.Sign(box.center.z) * (box.extents.z - 0.07f));
        GameObject point = node(control.transform, "Interaction Point", grip);
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);

        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        // no static area: it stays where the leaf is shut and blocks the doorway; grab the leaf box
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 30.0f;
        lever.rigidbodyDrag = 15.0f;
        // DV drags a lever via its SPRING target (GrabHandlerHingeJoint.FixedUpdate); unnotched levers reset on release
        // so: notched, 13 stops, spring, one tick per stop
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 13;
        // across door turns the OTHER way to swing into its cab
        lever.jointAxis = across ? -door_axis : door_axis;
        lever.useSpring = true;
        lever.jointSpring = 400.0f;
        lever.jointDamper = 30.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = opens;
        serialised(lever, "interactionPoint", "useSteppedJoint", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        // DV's door slam clip (DH4/DE2/DE6/DM3/shunter cab doors) at the stops; no grinding
        sounds(lever, ("limitHit", "ItemCollision_MetalSheet_03"));
        lever.OnValidate();
    }

    // DOOR LEVER, spindle frame (x along, y up, -z out): cut-corner square neck to `neck`, flat bar 14 x 22 to 128 mm
    // 1.5 mm broken edges; two convex pieces, flat-lit
    private static Mesh door_lever(float neck)
    {
        string name = $"door_lever_{Mathf.RoundToInt(neck * 1000.0f)}";
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        const float half_neck = 0.011f, thick = 0.014f, reach = 0.128f, half_high = 0.011f, broken = 0.0015f, cut = 0.003f;
        float far = neck + thick;
        Vector2[][] pieces =
        {
            new[] { new Vector2(-half_neck, 0.0f), new Vector2(half_neck, 0.0f), new Vector2(half_neck, far), new Vector2(half_neck - 0.008f, far), new Vector2(-half_neck, far - thick) },
            new[] { new Vector2(half_neck, neck), new Vector2(reach - cut, neck), new Vector2(reach, neck + cut), new Vector2(reach, far - cut), new Vector2(reach - cut, far), new Vector2(half_neck, far) },
        };
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        void corner(Vector3 a, Vector3 b, Vector3 c, Vector3 lit)
        {
            int first = points.Count;
            bool turned_over = Vector3.Dot(Vector3.Cross(b - a, c - a), lit) < 0.0f;
            points.AddRange(turned_over ? new[] { a, c, b } : new[] { a, b, c });
            normals.AddRange(new[] { lit, lit, lit });
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
        Vector3 place(Vector2 at, float y) => new Vector3(at.x, y, -at.y);
        foreach (Vector2[] outline in pieces)
        {
            Vector2 middle = Vector2.zero;
            foreach (Vector2 at in outline)
                middle += at / outline.Length;
            Vector2[] drawn_in = outline.Select(at => at + (middle - at).normalized * broken).ToArray();
            for (int index = 0; index < outline.Length; ++index)
            {
                int next = (index + 1) % outline.Length;
                Vector2 edge = outline[next] - outline[index], away = new Vector2(edge.y, -edge.x).normalized;
                if (Vector2.Dot(away, (outline[index] + outline[next]) * 0.5f - middle) < 0.0f)
                    away = -away;
                Vector3 flat = new Vector3(away.x, 0.0f, -away.y);
                float top = half_high - broken;
                corner(place(outline[index], top), place(outline[next], top), place(outline[next], -top), flat);
                corner(place(outline[index], top), place(outline[next], -top), place(outline[index], -top), flat);
                foreach (float end in new[] { 1.0f, -1.0f })
                {
                    Vector3 slant = (flat + Vector3.up * end).normalized;
                    corner(place(outline[index], end * top), place(outline[next], end * top), place(drawn_in[next], end * half_high), slant);
                    corner(place(outline[index], end * top), place(drawn_in[next], end * half_high), place(drawn_in[index], end * half_high), slant);
                    corner(place(middle, end * half_high), place(drawn_in[index], end * half_high), place(drawn_in[next], end * half_high), Vector3.up * end);
                }
            }
        }
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // door handle places from the model (model x across, y along, z up; vehicle = (-x, z, -y))
    private static IEnumerable<(Vector3 spindle, Vector3 out_of, Vector3 along)> door_handle_places(string part)
    {
        string list = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_door_handles.txt"));
        foreach (string line in File.ReadAllLines(list))
        {
            string[] words = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 10 || words[0] != part)
                continue;
            float[] figures = words.Skip(1).Select(word => float.Parse(word, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Vector3 ours(int from) => new Vector3(-figures[from], figures[from + 2], -figures[from + 1]);
            yield return (ours(0), ours(3), ours(6));
        }
    }

    // both doors swing in by the same turn; ASSUMED SIGN: if they open outward in game, use Vector3.down
    private static readonly Vector3 door_axis = Vector3.up;

    // lamps and wipers per cab: these + A or B, on the desk
    private static readonly string[] end_lamp_ports = { "lightLeft", "lightTop", "lightRight", "redLeft", "redRight", "lightDimLeft", "lightDimTop", "lightDimRight", "wiperLeft", "wiperRight" };

    // TWO CABS, ONE LIVE: handles feed <control>A / <control>B; mod copies the live cab's to the canonical port
    // (cab B reverser turned); dead cab held by eu07.CAB_A_LIVE / CAB_B_LIVE < 0.5; keep in step with eu07_driver.cs
    private static readonly (string control, string canonical, float rest)[] cab_driving =
    {
        ("throttle", "throttle.EXT_IN", 0.0f), ("reverser", "reverser.CONTROL_EXT_IN", 0.5f), ("brake", "brake.EXT_IN", 0.0f),
        ("indBrake", "indBrake.EXT_IN", 0.0f), ("sander", "sander.CONTROL_EXT_IN", 0.0f), ("hornControl", "hornControl.EXT_IN", 0.5f),
        ("brakeCutout", "brakeCutout.EXT_IN", 1.0f),
        ("fastBreakerOn", "fastBreakerOn.EXT_IN", 0.0f), ("fastBreakerOff", "fastBreakerOff.EXT_IN", 0.0f), ("relayReset", "relayReset.EXT_IN", 0.0f),
        ("auxRelayReset", "auxRelayReset.EXT_IN", 0.0f), ("lineContactorsOff", "lineContactorsOff.EXT_IN", 0.0f), ("wheelSlip", "wheelSlip.EXT_IN", 0.0f),
        ("faultAck", "faultAck.EXT_IN", 0.0f), ("vigilance", "vigilance.EXT_IN", 0.0f), ("fieldLever", "fieldLever.EXT_IN", 0.0f),
        ("highStart", "highStart.EXT_IN", 0.0f), ("converterSwitch", "converterSwitch.EXT_IN", 0.0f), ("compressorSwitch", "compressorSwitch.EXT_IN", 0.0f),
        ("heatingSwitch", "heatingSwitch.EXT_IN", 0.0f), ("pantographAsk", "pantographAsk.EXT_IN", 0.0f), ("pantograph2Ask", "pantograph2Ask.EXT_IN", 0.0f),
    };
    // cab's non-driving own controls: A keeps ports, B = <control>B; cab light and heating shared
    private static readonly string[] cab_b_own = { "cupboardLeft", "cupboardMiddle", "cupboardRight", "instrumentLight", "cabCutout" };

    // lever (his Throttle / Reverser, axis a parameter), desk frame; notched value = notch / (notches - 1)
    private static GameObject make_lever(Transform desk, string name, Vector3 pivot, Vector3 handle_centre,
        Vector3 handle_size, Vector3 grip, Vector3 axis, float from, float to, int notches,
        string port, string scroll_key, string absolute_key, string[] names, Material handle_material = null, float reach = 0.0f, bool bare = false)
    {
        Transform root = node(desk, name, pivot).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, handle_material ?? white,
            handle_centre, handle_size, Quaternion.identity, true);
        if (bare)
        {
            // caller adds its own mesh; the box stays as the hidden grab
            UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
            UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        }
        else
        {
            solid(root, "Boss", PrimitiveType.Cylinder, silver, new Vector3(0.0f, -0.01f, 0.0f),
                new Vector3(0.03f, 0.01f, 0.03f), Quaternion.identity, false);
        }
        GameObject point = node(control.transform, "Interaction Point", grip);
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);     // as his

        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        // pointer area: box round the handle, or a ball round the pivot for far-swinging handles
        lever.nonVrStaticInteractionArea = reach > 0.0f
            ? sphere_area(root, name, Vector3.zero, reach)
            : box_area(root, name, handle_centre, handle_size + new Vector3(0.03f, 0.03f, 0.03f));
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 30.0f;
        lever.rigidbodyDrag = 15.0f;
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = notches;
        lever.jointAxis = axis;
        lever.useSpring = true;
        lever.jointSpring = 400.0f;
        lever.jointDamper = 20.0f;
        lever.useLimits = true;
        lever.jointLimitMin = from;
        lever.jointLimitMax = to;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax");
        feed(control, port);
        if (names != null)
            control.AddComponent<NotchedControlCustomNamesProxy>().customNames = names;
        else
            control.AddComponent<NotchedControlNumberedNamesProxy>();

        MouseScrollKeyboardInputProxy scroll = control.AddComponent<MouseScrollKeyboardInputProxy>();
        scroll.scrollAction = new AKeyboardInputProxy.ActionReference(scroll_key);
        scroll.OnValidate();
        AnalogSetValueJoystickInputProxy absolute = control.AddComponent<AnalogSetValueJoystickInputProxy>();
        absolute.action = new AKeyboardInputProxy.ActionReference(absolute_key);
        absolute.OnValidate();
        serialised(scroll, "scrollAction", "_json");
        serialised(absolute, "action", "_json");
        sounds(lever, ("notch", notches <= 3 ? "LeverNotch_01_Deep" : "LeverNotch_04_Tick"), ("limitHit", "LeverNotch_07_Deep"));
        lever.OnValidate();
        return control;
    }

    // CONTROLLER WHEEL as a LeverProxy (see below; WheelProxy has no notches, design_desk.md b4)
    // 44 positions over 300 deg, clockwise adds power; value = position / 43; built at mid-travel (mark at 12 o'clock)
    private static GameObject make_controller(Transform desk, Vector3 hub, string port)
    {
        const float radius = 0.1845f;    // rim circle: 397 mm overall
        Transform root = node(desk, "Controller", hub).transform;
        GameObject control = node(root, "C_Controller", Vector3.zero);

        // model wheel, built at position 0: hung turned by half travel; grab = hub box + rim boxes (key/field lever reachable)
        desk_part(control.transform, "Wheel", Vector3.zero).transform.localRotation = Quaternion.AngleAxis(controller_sweep, controller_axis);
        desk_part(root, "WheelColumn", Vector3.zero);
        List<GameObject> grips = new List<GameObject>
        {
            solid(control.transform, "Hub", PrimitiveType.Cylinder, black, new Vector3(0.0f, -0.003f, 0.0f), new Vector3(0.10f, 0.017f, 0.10f), Quaternion.identity, true),
        };
        const int segments = 24;
        for (int index = 0; index < segments; ++index)
        {
            Quaternion turn = Quaternion.Euler(0.0f, 360.0f * index / segments, 0.0f);
            grips.Add(solid(control.transform, "Grip" + index, PrimitiveType.Cube, black, turn * new Vector3(0.0f, 0.0f, radius),
                new Vector3(2.0f * Mathf.PI * radius / segments * 1.05f, 0.03f, 0.03f), turn, true));
        }
        foreach (GameObject grip in grips)
        {
            UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
            UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        }

        // LeverBase, not RotaryBase: only it disables stepped-joint snapping while scrolling (rotary snaps back a frame later)
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, 0.0f, radius));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);     // as his
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = grips.ToArray();
        // pointer area: hub only (a slab would block the key and field lever under the wheel)
        lever.nonVrStaticInteractionArea = box_area(root, "Controller", new Vector3(0.0f, -0.02f, 0.0f), new Vector3(0.13f, 0.07f, 0.13f));
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 5.0f;    // ~0.16 kg m2 about the hinge, as the field lever
        lever.rigidbodyDrag = 15.0f;
        lever.rigidbodyAngularDrag = 0.0f;
        lever.scrollWheelHoverScroll = 1.0f;    // one position per key tap or tick
        lever.scrollWheelSpring = 0.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = eu07_positions;
        lever.jointAxis = controller_axis;
        lever.useSpring = true;
        lever.jointSpring = 400.0f;
        lever.jointDamper = 20.0f;
        lever.useLimits = true;
        lever.jointLimitMin = -controller_sweep;
        lever.jointLimitMax =  controller_sweep;
        lever.invertDirection = false;
        serialised(lever, "interactionPoint", "rigidbodyMass", "rigidbodyDrag", "scrollWheelHoverScroll", "useSteppedJoint",
            "steppedValueUpdate", "notches", "jointAxis", "useSpring", "jointSpring", "jointDamper", "useLimits",
            "jointLimitMin", "jointLimitMax", "invertDirection");
        feed(control, port);
        control.AddComponent<NotchedControlNumberedNamesProxy>();

        MouseScrollKeyboardInputProxy scroll = control.AddComponent<MouseScrollKeyboardInputProxy>();
        scroll.scrollAction = new AKeyboardInputProxy.ActionReference("ThrottleIncremental");
        scroll.OnValidate();
        AnalogSetValueJoystickInputProxy absolute = control.AddComponent<AnalogSetValueJoystickInputProxy>();
        absolute.action = new AKeyboardInputProxy.ActionReference("ThrottleAbsolute");
        absolute.OnValidate();
        serialised(scroll, "scrollAction", "_json");
        serialised(absolute, "action", "_json");
        sounds(lever, ("notch", "LeverNotch_12_DeepMetal"), ("limitHit", "LeverNotch_07_Deep"));
        lever.OnValidate();
        return control;
    }

    // fills a control's sound fields with DV clips by name at load (else silent)
    private static void sounds(Component control, params (string field, string clip)[] clips)
    {
        SoundGrabber grabber = control.gameObject.AddComponent<SoundGrabber>();
        grabber.ScriptToAffect = control;
        grabber.Replacements = clips.Select(one => new ResourceReplacement { FieldName = one.field, ReplacementName = one.clip }).ToArray();
        grabber.OnValidate();
    }

    // BORROWED DV part: name only; CCL puts DV's mesh/material on at load (MeshGrabberFilter, MaterialGrabberRenderer)
    // empty in the editor and to desk_clearance.py
    private static GameObject borrowed(Transform parent, string name, string mesh, string material, Vector3 at, Quaternion turn, Vector3 scale)
    {
        GameObject made = node(parent, name, at);
        made.transform.localRotation = turn;
        made.transform.localScale = scale;
        MeshFilter filter = made.AddComponent<MeshFilter>();
        MeshRenderer renderer = made.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = null;
        MeshGrabberFilter take = made.AddComponent<MeshGrabberFilter>();
        take.Filter = filter;
        take.ReplacementName = mesh;
        if (!take.IsValid(out string wrong))
            throw new InvalidDataException(wrong);
        MaterialGrabberRenderer paint = made.AddComponent<MaterialGrabberRenderer>();
        paint.RenderersToAffect = new Renderer[] { renderer };
        paint.Replacements = new[] { new MaterialGrabberRenderer.IndexToName { RendererIndex = 0, ReplacementName = material } };
        paint.OnValidate();
        if (!paint.IsValid(out wrong))
            throw new InvalidDataException(wrong);
        return made;
    }

    // custom shapes, made once, kept as assets
    private static readonly Dictionary<string, Mesh> shapes = new Dictionary<string, Mesh>();

    private static GameObject shaped(Transform parent, string name, Mesh mesh, Material material, Vector3 at)
    {
        GameObject made = node(parent, name, at);
        made.AddComponent<MeshFilter>().sharedMesh = mesh;
        made.AddComponent<MeshRenderer>().sharedMaterial = material;
        return made;
    }

    // PRISM out of a panel: foot radius at z 0, top radius at z -height, capped, flat-lit; `turn` rotates corners
    private static Mesh prism(string name, int sides, float foot, float top, float height, float turn = 0.0f)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        Vector3 corner(int index, bool up)
        {
            float angle = (turn + 360.0f * index / sides) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0.0f) * (up ? top : foot) + new Vector3(0.0f, 0.0f, up ? -height : 0.0f);
        }
        void face(Vector3 outward, Vector3[] ring)    // a flat facing `outward`, whatever the corner order
        {
            Vector3 normal = Vector3.Cross(ring[1] - ring[0], ring[2] - ring[0]).normalized;
            if (Vector3.Dot(normal, outward) < 0.0f)
            {
                ring = ring.Reverse().ToArray();
                normal = -normal;
            }
            int first = points.Count;
            points.AddRange(ring);
            normals.AddRange(ring.Select(_ => normal));
            for (int index = 1; index < ring.Length - 1; ++index)
                triangles.AddRange(new[] { first, first + index, first + index + 1 });
        }
        for (int index = 0; index < sides; ++index)
        {
            Vector3[] ring = { corner(index, false), corner(index, true), corner(index + 1, true), corner(index + 1, false) };
            Vector3 middle = (ring[0] + ring[1] + ring[2] + ring[3]) * 0.25f;
            face(new Vector3(middle.x, middle.y, 0.0f), ring);
        }
        face(Vector3.back, Enumerable.Range(0, sides).Select(index => corner(index, true)).ToArray());    // top: out of the panel
        face(Vector3.forward, Enumerable.Range(0, sides).Select(index => corner(index, false)).ToArray());    // foot
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // LOFT out of a panel through outlines (equal point counts, each with its "away"); `proud` = level height
    // smooth round, flat across levels, or `faceted`; outline shrunk to a point closes it
    private static Mesh loft(string name, IList<(Vector2 at, Vector2 away)[]> rings, IList<float> proud, bool faceted)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        void corner(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
        {
            Vector3 lies = Vector3.Cross(b - a, c - a);
            if (lies.sqrMagnitude < 1e-16f)
                return;    // degenerate: no face
            int first = points.Count;
            if (Vector3.Dot(lies, na + nb + nc) < 0.0f)
            {
                points.AddRange(new[] { a, c, b });
                normals.AddRange(new[] { na, nc, nb });
            }
            else
            {
                points.AddRange(new[] { a, b, c });
                normals.AddRange(new[] { na, nb, nc });
            }
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
        int count = rings[0].Length;
        for (int level = 0; level + 1 < rings.Count; ++level)
        {
            (Vector2 at, Vector2 away)[] low = rings[level], high = rings[level + 1];
            // band normal (away, out): square to its own run
            float out_by = proud[level + 1] - proud[level], wider_by = high.Average(p => p.at.magnitude) - low.Average(p => p.at.magnitude);
            Vector2 looks = new Vector2(out_by, -wider_by);
            if (looks.sqrMagnitude < 1e-16f)
                continue;
            looks.Normalize();
            Vector3 place((Vector2 at, Vector2 away) p, int which) => new Vector3(p.at.x, p.at.y, -proud[which]);
            Vector3 lit(Vector2 away) => new Vector3(away.x * looks.x, away.y * looks.x, -looks.y).normalized;
            for (int index = 0; index < count; ++index)
            {
                int next = (index + 1) % count;
                Vector2 flat = (low[index].away + low[next].away + high[index].away + high[next].away).normalized;
                Vector3 a = place(low[index], level), b = place(high[index], level + 1), c = place(high[next], level + 1), d = place(low[next], level);
                Vector3 na = lit(faceted ? flat : low[index].away), nb = lit(faceted ? flat : high[index].away),
                        nc = lit(faceted ? flat : high[next].away), nd = lit(faceted ? flat : low[next].away);
                corner(a, b, c, na, nb, nc);
                corner(a, c, d, na, nc, nd);
            }
        }
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // DOME: half a flattened ball, `radius` at foot (`foot` out), `deep` to crown; smooth per-vertex normals
    private static Mesh domed(string name, int sides, int bands, float radius, float foot, float deep)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        for (int band = 0; band <= bands; ++band)
        {
            float rise = 0.5f * Mathf.PI * band / bands;
            for (int side = 0; side <= sides; ++side)
            {
                float round = 2.0f * Mathf.PI * side / sides;
                points.Add(new Vector3(radius * Mathf.Cos(rise) * Mathf.Cos(round), radius * Mathf.Cos(rise) * Mathf.Sin(round), -(foot + deep * Mathf.Sin(rise))));
                normals.Add(new Vector3(Mathf.Cos(rise) * Mathf.Cos(round) / radius, Mathf.Cos(rise) * Mathf.Sin(round) / radius, -Mathf.Sin(rise) / deep).normalized);
            }
        }
        // faces wound outward
        void face(int a, int b, int c)
        {
            bool inside_out = Vector3.Dot(Vector3.Cross(points[b] - points[a], points[c] - points[a]), normals[a] + normals[b] + normals[c]) < 0.0f;
            triangles.AddRange(inside_out ? new[] { a, c, b } : new[] { a, b, c });
        }
        for (int band = 0; band < bands; ++band)
        {
            for (int side = 0; side < sides; ++side)
            {
                int a = band * (sides + 1) + side, b = a + 1, c = a + sides + 1, d = c + 1;
                face(a, b, c);
                if (band + 1 < bands)
                    face(b, d, c);
            }
        }
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // TURNED shape about the panel normal via (radius, proud) pairs; radius 0 closes; inward run = bore; `faceted` = flat sides
    private static Mesh turned(string name, int sides, bool faceted, params (float radius, float proud)[] profile)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        Vector2[] round = Enumerable.Range(0, sides).Select(index => new Vector2(Mathf.Cos(2.0f * Mathf.PI * index / sides), Mathf.Sin(2.0f * Mathf.PI * index / sides))).ToArray();
        return loft(name, profile.Select(level => round.Select(way => (way * level.radius, way)).ToArray()).ToList(), profile.Select(level => level.proud).ToList(), faceted);
    }

    // TUMBLER LEVER about the hub axis (panel x): 4 mm hub, 8.5 mm wide, end r 2.6 mm at 20 mm, 5 ribs per face on the outer third
    // upright = straight out (-z); a bit under ref size, sits lower in the frame
    private const float tumbler_hub_out = 0.0005f;
    private static Mesh tumbler()
    {
        if (shapes.TryGetValue("tumbler", out Mesh known))
            return known;
        const float wide = 0.0085f, hub = 0.0040f, tip = 0.0026f, reach = 0.0200f;
        const int round = 8;
        float taper = Mathf.Asin((hub - tip) / reach);
        // outline in (y, out), anticlockwise: end, then hub; each point with its outward direction
        List<(Vector2 at, Vector2 away)> outline = new List<(Vector2 at, Vector2 away)>();
        for (int step = 0; step <= round; ++step)
        {
            float angle = Mathf.Lerp(taper, Mathf.PI - taper, (float) step / round);
            Vector2 away = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            outline.Add((new Vector2(0.0f, reach) + away * tip, away));
        }
        for (int step = 0; step <= round + 2; ++step)
        {
            float angle = Mathf.Lerp(Mathf.PI - taper, 2.0f * Mathf.PI + taper, (float) step / (round + 2));
            Vector2 away = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            outline.Add((away * hub, away));
        }
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        void corner(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
        {
            int first = points.Count;
            bool turned_over = Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0.0f;
            points.AddRange(turned_over ? new[] { a, c, b } : new[] { a, b, c });
            normals.AddRange(turned_over ? new[] { na, nc, nb } : new[] { na, nb, nc });
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
        Vector3 place((Vector2 at, Vector2 away) p, float x) => new Vector3(x, p.at.x, -p.at.y);
        Vector3 lit((Vector2 at, Vector2 away) p) => new Vector3(0.0f, p.away.x, -p.away.y);
        for (int index = 0; index < outline.Count; ++index)
        {
            (Vector2 at, Vector2 away) here = outline[index], next = outline[(index + 1) % outline.Count];
            corner(place(here, -wide * 0.5f), place(next, -wide * 0.5f), place(next, wide * 0.5f), lit(here), lit(next), lit(next));
            corner(place(here, -wide * 0.5f), place(next, wide * 0.5f), place(here, wide * 0.5f), lit(here), lit(next), lit(here));
        }
        foreach (float x in new[] { -wide * 0.5f, wide * 0.5f })    // two flat sides
        {
            Vector3 side = new Vector3(Mathf.Sign(x), 0.0f, 0.0f), middle = new Vector3(x, 0.0f, -reach * 0.5f);
            for (int index = 0; index < outline.Count; ++index)
                corner(middle, place(outline[index], x), place(outline[(index + 1) % outline.Count], x), side, side, side);
        }
        Mesh body = new Mesh { name = "tumbler_body" };
        body.SetVertices(points);
        body.SetNormals(normals);
        body.SetTriangles(triangles, 0);
        // ribs: five per face, outer third
        Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        List<CombineInstance> parts = new List<CombineInstance> { new CombineInstance { mesh = body, transform = Matrix4x4.identity } };
        foreach (float face in new[] { -1.0f, 1.0f })
        {
            Vector2 from = new Vector2(face * hub * Mathf.Cos(taper), hub * Mathf.Sin(taper)), to = new Vector2(face * tip * Mathf.Cos(taper), reach + tip * Mathf.Sin(taper));
            Vector2 away = new Vector2(face * Mathf.Cos(taper), Mathf.Sin(taper));
            Vector3 along = new Vector3(0.0f, to.x - from.x, -(to.y - from.y)).normalized;
            for (int rib = 0; rib < 5; ++rib)
            {
                Vector2 at = Vector2.Lerp(from, to, 0.66f + 0.075f * rib) + away * 0.0002f;
                parts.Add(new CombineInstance { mesh = cube, transform = Matrix4x4.TRS(new Vector3(0.0f, at.x, -at.y), Quaternion.FromToRotation(Vector3.up, along),
                    new Vector3(wide, 0.0008f, 0.0009f)) });
            }
        }
        Mesh made = new Mesh { name = "tumbler" };
        made.CombineMeshes(parts.ToArray(), true, true);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_tumbler.asset");
        shapes["tumbler"] = made;
        return made;
    }

    // BLOCK out of a panel: square `half`, corners rounded to `corner`, per (half, corner, proud) level; half 0 closes
    private static Mesh block(string name, params (float half, float corner, float proud)[] levels)
    {
        return slab(name, levels.Select(level => (level.half, level.half, level.corner, level.proud)).ToArray());
    }

    // same, not square: half x, half y per level; negative `proud` = behind the panel
    private static Mesh slab(string name, params (float half_x, float half_y, float corner, float proud)[] levels)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        const int steps = 3;
        List<(Vector2 at, Vector2 away)[]> rings = new List<(Vector2 at, Vector2 away)[]>();
        foreach ((float half_x, float half_y, float corner, float proud) in levels)
        {
            List<(Vector2 at, Vector2 away)> ring = new List<(Vector2 at, Vector2 away)>();
            for (int quarter = 0; quarter < 4; ++quarter)
            {
                Vector2 centre = new Vector2(quarter == 0 || quarter == 3 ? half_x - corner : corner - half_x, quarter < 2 ? half_y - corner : corner - half_y);
                for (int step = 0; step <= steps; ++step)
                {
                    float angle = (quarter + (float) step / steps) * 0.5f * Mathf.PI;
                    Vector2 way = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                    ring.Add((centre + way * corner, way));
                }
            }
            rings.Add(ring.ToArray());
        }
        return loft(name, rings, levels.Select(level => level.proud).ToList(), false);
    }

    // LAMP COVER: curved glass, square in plan; arch along y, same section across x, flat ends; feet z 0, crown `rise` out
    private static Mesh lamp_lens()
    {
        if (shapes.TryGetValue("lamp_lens", out Mesh known))
            return known;
        const float half = 0.0158f, rise = 0.0044f;
        const int steps = 12;
        float radius = half * half / (2.0f * rise) + rise * 0.5f, reach = Mathf.Asin(half / radius);
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        void corner(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc)
        {
            int first = points.Count;
            bool turned_over = Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0.0f;
            points.AddRange(turned_over ? new[] { a, c, b } : new[] { a, b, c });
            normals.AddRange(turned_over ? new[] { na, nc, nb } : new[] { na, nb, nc });
            triangles.AddRange(new[] { first, first + 1, first + 2 });
        }
        float angle(int step) => -reach + 2.0f * reach * step / steps;
        Vector3 on(float x, int step) => new Vector3(x, radius * Mathf.Sin(angle(step)), -radius * (Mathf.Cos(angle(step)) - Mathf.Cos(reach)));
        Vector3 lit(int step) => new Vector3(0.0f, Mathf.Sin(angle(step)), -Mathf.Cos(angle(step)));
        for (int step = 0; step < steps; ++step)
        {
            corner(on(-half, step), on(-half, step + 1), on(half, step + 1), lit(step), lit(step + 1), lit(step + 1));
            corner(on(-half, step), on(half, step + 1), on(half, step), lit(step), lit(step + 1), lit(step));
        }
        foreach (float x in new[] { -half, half })    // two flat ends
        {
            Vector3 end = new Vector3(Mathf.Sign(x), 0.0f, 0.0f);
            for (int step = 1; step < steps; ++step)
                corner(on(x, 0), on(x, step), on(x, step + 1), end, end, end);
        }
        Mesh made = new Mesh { name = "lamp_lens" };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_lamp_lens.asset");
        shapes["lamp_lens"] = made;
        return made;
    }

    // flat ring (about y): Unity has no torus primitive
    private static Mesh torus(string name, float radius, float tube, int around, int across)
    {
        List<Vector3> points = new List<Vector3>(), normals = new List<Vector3>();
        List<int> triangles = new List<int>();
        for (int i = 0; i <= around; ++i)
        {
            Quaternion turn = Quaternion.Euler(0.0f, 360.0f * i / around, 0.0f);
            for (int j = 0; j <= across; ++j)
            {
                float angle = 2.0f * Mathf.PI * j / across;
                Vector3 out_of_tube = turn * new Vector3(0.0f, Mathf.Sin(angle), Mathf.Cos(angle));
                points.Add(turn * new Vector3(0.0f, 0.0f, radius) + out_of_tube * tube);
                normals.Add(out_of_tube);
            }
        }
        int row = across + 1;
        for (int i = 0; i < around; ++i)
        {
            for (int j = 0; j < across; ++j)
            {
                int a = i * row + j, b = a + row;
                triangles.AddRange(new[] { a, b, a + 1, a + 1, b, b + 1 });
            }
        }
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        return made;
    }

    // ---- EXPORT ----
    // CCL's export (normally behind an editor window): validate, build bundle, write Info.json
    private static void export(CustomCarPack pack, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder))
            File.Delete(old);
        Type wizard = Type.GetType("CCL.Creator.Wizards.ExportPackWizard, CCL.Creator", throwOnError: true);
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        object requirements = wizard.GetMethod("GetRequirements", hidden).Invoke(null, new object[] { pack });
        wizard.GetMethod("Export", hidden).Invoke(null, new object[] { pack, Path.GetFullPath(folder), requirements });
        if (!File.Exists(Path.Combine(folder, "ccl_bundle")) || !File.Exists(Path.Combine(folder, "Info.json")))
            throw new IOException("the export wrote no bundle: see the log above");
        if (!File.ReadAllText(Path.Combine(folder, "Info.json")).Contains("DVCustomLicenses"))
            throw new IOException("the export did not make DVCustomLicenses a requirement, and the licence " + license_id + " is that mod's");
        File.WriteAllText(Path.Combine(folder, "license.json"), license_json, new System.Text.UTF8Encoding(false));
    }

    // LICENCE for DV Custom Licenses (CL.Common/CustomLicense.cs v1.1.3: license.json in each mod folder)
    // 100 000, +15 000 insurance, no prerequisite, free roam only once bought; languages: 0 English, 14 Polish
    private const string license_id = "WE4U";
    private const string license_json = @"{
  ""LicenseType"": 0,
  ""Identifier"": ""WE4U"",
  ""Color"": { ""R"": 0.839, ""G"": 0.702, ""B"": 0.251, ""A"": 1.0 },
  ""Price"": 100000.0,
  ""InsuranceFeeQuotaIncrease"": 15000.0,
  ""BonusTimeDecreasePercentage"": 0.0,
  ""RequiredGeneralLicenseId"": """",
  ""RequiredJobLicenseId"": """",
  ""Availability"": 0,
  ""TranslationName"": { ""Items"": [ { ""Language"": 0, ""Value"": ""WE-4U"" }, { ""Language"": 14, ""Value"": ""WE-4U"" } ] },
  ""TranslationDescription"": { ""Items"": [
    { ""Language"": 0, ""Value"": ""Allows you to drive the WE-4U, a 1500 V DC electric locomotive on four powered axles, under the wire or on its traction battery."" },
    { ""Language"": 14, ""Value"": ""Uprawnia do prowadzenia WE-4U, elektrowozu prądu stałego 1500 V o czterech osiach napędnych, pod siecią lub na baterii trakcyjnej."" } ] },
  ""TranslationItem"": { ""Items"": [ { ""Language"": 0, ""Value"": ""WE-4U License"" }, { ""Language"": 14, ""Value"": ""Licencja WE-4U"" } ] },
  ""TranslationInfoItem"": { ""Items"": [ { ""Language"": 0, ""Value"": ""WE-4U License (sample)"" }, { ""Language"": 14, ""Value"": ""Licencja WE-4U (wzór)"" } ] }
}
";
}
