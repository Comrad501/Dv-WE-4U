// EU07 for Derail Valley - builds the CRUDE prefab and exports it, with no editor window.
//
//   Unity.exe -projectPath <this project> -executeMethod eu07_build.run
//             -eu07out <folder for the mod> -logFile <log> [-eu07desk v1|v2]
//
// -eu07desk picks the desk: v1 is Knsgf's desk with clones, as below; v2 is
// docs/desk/design_desk.md, the 303E's desk built from primitives under one parent, DeskA (see
// "desk v2" further down).
//
// NOT -batchmode: a Personal licence taken through the Hub is not seen by a 2019.4 editor
// in batch mode. The editor window opens, builds, and closes itself.
//
// It does not model anything. It takes Knsgf's example railcar (WE4PT, MIT, in this
// project under Assets/_CCL_CARS/WE4PT), which is a complete electric vehicle made of
// cubes - bogies, couplers, brakes, a desk, two pantographs and his bus bar - and makes
// an EU07 of it:
//
//   - new car type, livery and pack: id EU07, 83.4 t, 1250 mm wheels
//   - bogie pivots 8550 mm apart, 16 235 mm over buffers, body 2.9 m wide and 3.9 m high
//     (303E DTR), as a frame, a closed machine room and two open cabs
//   - the simulation keeps his pantographs, bus bar, brakes and controls, and gets one new
//     component, "eu07": the ports through which the mod (mod/eu07_driver.cs) drives the
//     wheels and reports back. traction.TORQUE_IN is fed from eu07.TORQUE_OUT instead of
//     his traction motors, and his bus bar reads eu07.LINE_CURRENT. His motors are
//     also taken off the throttle, or the game destroys them
//   - the throttle lever gets 44 notches: it is the controller wheel until there is one
//   - the desk gets what the railcar's has no use for: switches for the fast breaker, the
//     relay reset, the line contactors and high start, and a seven-notch field lever,
//     each feeding a control of its own in the simulation (fastBreaker.EXT_IN ...)
//   - and loses two slips of his: the train brake answered the THROTTLE's keys, and every
//     switch answered the electrics fuse's. A switch with no key of its own has no keyboard
//     component at all: an empty action name fails CCL's validator
//
// Everything is rebuilt from the WE4PT assets on every run, so nothing under
// Assets/_CCL_CARS/EU07 is to be edited by hand.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

using UnityEditor;
using UnityEngine;

using CCL.Types;
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
    // What the game calls it, in the game's own scheme: Wire, Electric, 4 axles, Universal
    // (the author, 2026-10-10). The id stays EU07: the mod finds the vehicle by it.
    private const string display_name = "WE-4U";

    private const string source = "Assets/_CCL_CARS/WE4PT", target = "Assets/_CCL_CARS/EU07";

    // 303E DTR, metres
    private const float over_buffers = 16.315f, pivot_half = 8.550f * 0.5f, body_width = 2.90f, roof_top = 3.90f;
    private const float wheel_radius = 0.625f, mass_kg = 83400.0f;
    // the railcar's own proportion: coupler rig 0.53 short of the buffer faces
    private const float rig_z = over_buffers * 0.5f - 0.53f;
    // The body is model/eu07_blockout.py's (its hull model/eu07_hull.py's), and these are its
    // numbers: the nose's foot from the middle, the body's lower edge, the cab floor, the
    // cab's back wall, the pantographs. cab_front is where the cab's free space ends at the
    // wall under the windscreen. Only ONE door opens into each cab, the assistant's side's:
    // on the LEFT (-x) of cab A, on the right of cab B (the body's two sides are each other
    // turned end for end); the driver's-side door outside is behind the cab's back wall.
    private const float body_half = 7.354f, body_bottom = 1.285f, floor_top = 1.66f;
    private const float cab_back = 5.20f, cab_front = 7.25f, cab_length = cab_front - cab_back;
    private const float door_z = 5.5375f, door_half = 0.25f, pantograph_z = 3.41f;
    // where the desk's near edge stands - the controller's axis is 0.22 on, 6.419 from the
    // middle - and how high its top is over the cab floor
    private const float desk_z = 6.199f, desk_top = 0.79f;
    // the desk's bulk begins this far behind DeskA's origin: its near edge is 165 mm behind the controller's axis
    private const float desk_front = 0.055f;
    private const string body_fbx = target + "/EU07_body.fbx", parts_fbx = target + "/EU07_desk_parts.fbx", bogie_fbx = target + "/EU07_bogie.fbx", panto_fbx = target + "/EU07_pantograph.fbx";
    private const float axle_half = 3.050f * 0.5f;         // the bogie's wheelbase, halved

    // A label on the desk is a translation key, and a key the game does not know reads
    // "Regulator" (the author, looking at five switches all called that, 2026-10-10).
    // So the pack brings its own terms.
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
        // the speed recorder's clock (hours 0-12, minutes 0-60) and the six drums of its counter (0-10 each)
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
            desk = argument("-eu07desk") ?? "v2";              // v2 since it was seen working, 2026-10-10
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
        // The body: the block-out model, straight from the repository's model folder.
        string model_file = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_blockout.fbx"));
        File.Copy(model_file, Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_body.fbx"), true);
        check_paint();
        AssetDatabase.Refresh();
        AssetDatabase.ImportAsset(body_fbx, ImportAssetOptions.ForceSynchronousImport);
        body_materials.Clear();
        paint_pictures.Clear();
        finished.Clear();
        inside_cans.Clear();
        // The desk's right-hand group - wheel, pedestal, key, field lever, brake valve - as
        // meshes of our own, from model/eu07_desk_parts.py.
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_desk_parts.fbx")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_desk_parts.fbx"), true);
        AssetDatabase.ImportAsset(parts_fbx, ImportAssetOptions.ForceSynchronousImport);
        parts_model = AssetDatabase.LoadAssetAtPath<GameObject>(parts_fbx) ?? throw new IOException("no model at " + parts_fbx);
        // The bogie: frame, wheelset and brake blocks, from model/eu07_bogie.py. Its meshes
        // are kept readable, as Custom Car Loader wants a bogie's to be.
        File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_bogie.fbx")),
            Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_bogie.fbx"), true);
        AssetDatabase.ImportAsset(bogie_fbx, ImportAssetOptions.ForceSynchronousImport);
        ModelImporter bogie_importer = (ModelImporter) AssetImporter.GetAtPath(bogie_fbx) ?? throw new IOException("no model at " + bogie_fbx);
        bogie_importer.isReadable = true;
        bogie_importer.SaveAndReimport();
        // The pantograph's four parts, from model/eu07_pantograph.py.
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
            edit_prefab($"{target}/EU07_interactables.prefab", shape_desk_v2);
        else
            edit_prefab($"{target}/EU07_interactables.prefab", shape_desk);

        CustomCarType    type   = AssetDatabase.LoadAssetAtPath<CustomCarType>($"{target}/EU07_cartype.asset");
        CustomCarVariant livery = AssetDatabase.LoadAssetAtPath<CustomCarVariant>($"{target}/EU07_livery.asset");
        CustomCarPack    pack   = AssetDatabase.LoadAssetAtPath<CustomCarPack>($"{target}/EU07_pack.asset");
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{target}/EU07_template.prefab");
        GameObject desk   = AssetDatabase.LoadAssetAtPath<GameObject>($"{target}/EU07_interactables.prefab");

        set(type, o =>
        {
            text(o, "id", "EU07");
            text(o, "carIdPrefix", "-");                 // as the game's own: L-080
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
            o.FindProperty("UseCustomHosePositions").boolValue = true;     // the brake hose where shape_vehicle puts it
            // MULTIPLE UNITS BY THE GAME'S OWN SYSTEM ("I propose staying with the Dv standard", the author,
            // 2026-10-12 - so that it works with the game's diesels too, not only with its own kind).
            // With this Custom Car Loader gives the vehicle the game's MultipleUnitModule and its cables,
            // and the game passes throttle, both brakes, reverser and sander between coupled units
            // through the overridable controls the simulation already has. What a WE-4U must share
            // with another WE-4U besides is named in mu_shared (shape_vehicle).
            o.FindProperty("HasMUCable").boolValue = true;
        });
        ExtraTranslations words = ScriptableObject.CreateInstance<ExtraTranslations>();
        words.Terms = desk_v2
            ? labels_v2.Select(label => new ExtraTranslations.TermData { Term = label.term, Data = both(label.english, label.polish) }).ToArray()
            : labels.Select(label => new ExtraTranslations.TermData { Term = label.term, Data = TranslationData.Default(label.text) }).ToArray();
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
            text(o, "Version", "0.1.0");
            o.FindProperty("Cars.Array.data[0]").objectReferenceValue = type;
        });
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return pack;
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

    // ---- the vehicle ----------------------------------------------------------------------
    private static void shape_vehicle(GameObject root)
    {
        root.name = "EU07_template";
        Transform top = root.transform;

        // Knsgf's railcar carries Custom Car Loader's port plotter, his window of pantograph graphs ("the mod still
        // spawns the electric loco debug graphs thingy", the author, 2026-10-14): it opens for every one spawned,
        // even while the game loads. A developer's tool, not the locomotive's.
        foreach (SimPortPlotter plotter in root.GetComponentsInChildren<SimPortPlotter>(true))
            UnityEngine.Object.DestroyImmediate(plotter);

        // THE GAME'S OWN GADGETS ("to have one of those wood cabinets open up, and to have space for the stock stuff,
        // like the vigilance box. that would connect to our system", the author, 2026-10-13). The game gives every
        // vehicle a TrainCarCustomization; what a gadget asks of it (read in the game: TrainCarCustomizerBase) is a
        // FUSE to be powered by - without one a powered gadget cannot even be mounted - and the vehicle's readings
        // by their standard names. The fuse is one the mod alone sets: the 110 V there and the control breaker in.
        // The wheels' speed is what the game's vigilance device (GadgetATS) goes by: it only counts while moving.
        // WHERE gadgets may be mounted is said on the desk's prefab (shape_desk_v2: the instrument cupboard).
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
        // The game's brake hose and its cock, out at the bracket the model has for them on each end's left of the
        // hook: 0.446 off the middle, 0.964 above the rail, 0.20 ahead of the headstock's face. It hung 0.10
        // further in and 0.06 nearer the hook, where the game's own vehicles have it ("that also needs to be moved
        // out to where it should be", the author, 2026-10-11). Each rig's own frame: the rear one is turned round.
        foreach (string rig in new[] { "[coupler_rig_front]", "[coupler_rig_rear]" })
        {
            Transform hose = child(child(child(child(top, rig), "BuffersAndChainRig"), "hoses"), "CouplingHoseRig");
            hose.localPosition = new Vector3(-0.446f, 0.964f - child(top, rig).localPosition.y, body_half + 0.007f + 0.20f - rig_z);
        }
        foreach (Transform stem in child(top, "[BufferStems]"))
            move_z(stem, Mathf.Sign(stem.localPosition.z) * (rig_z - 0.13f));
        move_z(child(top, "BogieF"),  pivot_half);
        move_z(child(top, "BogieR"), -pivot_half);
        // The bogies were the game's freight bogie - 2 m wheelbase, 0.9 m wheels - which the
        // railcar this started from wore ("Bogeys look like wack, stock Derail valley
        // bogies", the author, 2026-10-10). Ours go in its place, part for part: the wheelset
        // on each [axle] (it is what the game turns), the frame, the brake blocks where the
        // game looks for the pads it makes glow, the wheels' contact points under the axles.
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
        // whatever turns wheels by a radius of its own must use the real one, or they would be seen to slip
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
        // The pantographs were his stretched cubes. The rig stays - an animation turns its three
        // pivots as the game raises and lowers it - and each part gets a shape of ours in
        // place of its cube: a single-arm collector ("detailing on the panto's", the author,
        // 2026-10-10). The model's parts are drawn in the rig's own frames.
        GameObject panto_model = AssetDatabase.LoadAssetAtPath<GameObject>(panto_fbx) ?? throw new IOException("no model at " + panto_fbx);
        foreach (string pantograph in new[] { "[Pantograph]", "[Pantograph2]" })
        {
            Transform rig = child(top, pantograph), lower = child(rig, "Pivot1"), upper = child(lower, "Pivot2"), head = child(upper, "HeadPivot");
            bogie_part(rig.Find("Base") ?? child(rig, "Base2"), panto_model, "Panto_base", pantograph);       // the second one's is "Base2"
            bogie_part(child(lower, "LowerArm"), panto_model, "Panto_lower", pantograph);
            bogie_part(child(upper, "UpperArm"), panto_model, "Panto_upper", pantograph);
            bogie_part(child(head, "Head"), panto_model, "Panto_head", pantograph);
            foreach (string horn in new[] { "Head (1)", "Head (2)" })
                UnityEngine.Object.DestroyImmediate(child(head, horn).gameObject);
        }
        foreach (string pantograph in new[] { "[Pantograph]", "[Pantograph2]" })
        {
            Transform item = child(top, pantograph);
            item.localPosition = new Vector3(0.0f, roof_top + 0.19f, Mathf.Sign(item.localPosition.z) * pantograph_z);     // on the model's insulators
        }
        foreach (string plate in new[] { "[car plate anchor1]", "[car plate anchor2]" })
        {
            Transform item = child(top, plate);
            Vector3 at = item.localPosition;
            // in the dark band, where the real one carries its number: toward cab A on the right side, toward cab B on the left
            item.localPosition = new Vector3(Mathf.Sign(at.x) * (body_width * 0.5f + 0.015f), 2.215f, Mathf.Sign(at.x) * 3.83f);
        }
        child(top, "AOShadow").localScale = new Vector3(body_width + 1.4f, 1.0f, over_buffers);

        // Colliders. [collision] is what other vehicles and the world hit: one box, the
        // body. [walkable] is what the PLAYER stands on and walks into: a floor through the
        // whole body, the machine room's walls, cases and machines with its passage left free
        // (make_room_blocks), each cab's walls with a gap at each
        // door, the desk's bulk, a roof - and outside each door a landing and a flight of
        // INVISIBLE treads along the body, because the doors do not open yet and there is
        // no ladder: one walks up beside the body and in through the door.
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
            if (group != "[walkable]")
                continue;
            float wall_y = floor_top + room_height * 0.5f;
            // The machine room ("a machine room would be nice, hell maybe even access to the small
            // compressors and a visual on the switchboards", the author, 2026-10-10): its roof, its
            // side walls, and for each end - in that end's OWN frame, x to its cab's right and z
            // from the middle toward its cab, which is how model/eu07_blockout.py builds it - the
            // cab's back wall either side of the passage door and over it, the resistor cases,
            // the whole high-voltage side (its own passage is locked), and on the machines' side
            // the pneumatic frame, the main compressor, the converter under its board, and at cab
            // A's end the small compressor, at cab B's the last-mile battery's cabinet. What is
            // left is the passage and the floor by each outside door.
            const float aisle = 0.36f, resistors = 1.55f, door_from = -0.30f, door_to = 0.40f, door_high = 1.85f;
            block("Room_roof", 0.0f, roof_top - 0.15f, 0.0f, body_width, 0.30f, cab_back * 2.0f);
            foreach (float side in new[] { 1.0f, -1.0f })
            {
                // each side wall has the doorway of the outside door behind the cab whose RIGHT it is on: the
                // right wall at cab A's end, the left at cab B's - from 4.70 to 5.19 m, 1.74 m high
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
                // (as the machines stand since they were rebuilt after the reference, 2026-10-12: the
                // frame with its reservoir, the compressor inside its guard rail, the converter
                // with its fan's casing and the cabinet over it - and at cab A's end the small
                // compressor's table, which is part of the same block)
                fill("frame", 0.45f, half_width, resistors, resistors + 0.34f, room_height);
                fill("compressor", 0.43f, half_width, 1.93f, 2.63f, 1.00f);
                fill("converter", aisle, half_width, 2.63f, end > 0.0f ? 4.60f : 4.29f, room_height);
                // (the last-mile battery's cabinet stood by the outside door at cab B's end, a block of its own: the
                // accumulators are on a rack at the resistors' end now, inside "frame")
            }
            foreach (float end in new[] { 1.0f, -1.0f })
            {
                string cab = end > 0.0f ? "CabA" : "CabB";
                block($"{cab}_roof", 0.0f, roof_top - 0.15f, end * (cab_back + body_half) * 0.5f, body_width, 0.30f, body_half - cab_back);
                // WINDOWS ARE HOLES. Each cab wall was one slab, floor to roof, and what the game
                // aims through a window - the hand-held radio's beam, the "go there" pointer -
                // stopped at it ("both the F held move command and the radio beam pass through
                // the DH-4's windows, which ours dont", the author, 2026-10-11). So a wall with
                // a window is the wall under its sill, the wall over its head and the pillars
                // beside it. The openings are the model's: the three windscreen panes, 1.07 m of
                // side window by the driver and 0.71 m on the other side (heights over the rail).
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
                if (end > 0.0f)                         // the desk; cab B has none yet
                    block($"{cab}_desk", 0.0f, floor_top + desk_top * 0.5f, (desk_z + desk_front + cab_front) * 0.5f, body_width - 0.2f, desk_top, cab_front - desk_z - desk_front);
                foreach (float side in new[] { 1.0f, -1.0f })
                {
                    // The cab narrows toward the nose; the walls here are straight, at its narrowest.
                    string tag = cab + (side > 0.0f ? "_R" : "_L");
                    float wall_x = side * 1.36f, ahead = door_z + door_half, behind = door_z - door_half;
                    if (side == end)                     // the driver's side: no door into the cab
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

        // Getting in. Under the one door of each cab hangs the strap step; the game climbs a
        // LADDER - a box one walks into and goes up - so there is one at each of those doors,
        // from near the ground to above the cab floor, facing the body (the author, 2026-10-10:
        // "getting in and out the cab is problematic, without a defined ladder").
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
        // Inside a cab the game shuts the outside sounds down - when it knows the camera is in
        // one. That is a box each cab and a switcher that watches it; without them the driver
        // heard the whole valley at full strength (the author, the same day).
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

        // The body: his cubes out, the block-out model in. Of the model, what the game or
        // his prefab already brings stays out: bogies, pantographs, the bus bar, buffers,
        // hooks.
        Transform model = child(top, "Model");
        int layer = model.GetComponentInChildren<MeshRenderer>().gameObject.layer;
        for (int index = model.childCount - 1; index >= 0; --index)
            UnityEngine.Object.DestroyImmediate(model.GetChild(index).gameObject);
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(body_fbx) ?? throw new IOException("no model at " + body_fbx);
        GameObject body = UnityEngine.Object.Instantiate(source);
        body.name = "Body";
        body.transform.SetParent(model, false);
        body.transform.localPosition = Vector3.zero;
        // the doors hang on hinges, with the desk; the wipers on their spindles; the side windows' opening panes slide, with the desk
        string[] not_ours = { "Bogie", "Pantograph_", "Buffer_", "Hook_", "Door_cab_", "Door_side_", "Door_room_", "Wiper_", "Window_cab_opening_" };
        // ...and are hung exactly where they stand in the body as it stands in the vehicle: that
        // frame is taken here, from the doors themselves, before they are taken out.
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
            renderer.sharedMaterials = renderer.sharedMaterials.Select(material => body_material(material.name)).ToArray();
        // The model must have come in the right way up, the right way round and in metres.
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
        // what the game's paint cans repaint: every place in the vehicle that wears a painted material
        // (the outside doors hang with the desk, in another prefab: the mod gives them the body's paint)
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
        // ...and the inside's paint: the shell's inside and the machine room
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
                // the shell's inside in its new state: the shell's own new pictures
                fresh = new Material(Shader.Find("Standard")) { color = Color.white };
                Texture2D fresh_mask = paint_picture("ext_a_new_mask", false, 1024);
                fresh.mainTexture = paint_picture("ext_a_new_albedo", true, 2048);
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
        wire_simulation(child(top, "[sim]"));
        if (desk_v2)
        {
            foreach ((string connection, string control, int notches) in mu_shared)
            {
                CCL.Types.Components.MultipleUnit.MultipleUnitGenericControl shared = top.gameObject.AddComponent<CCL.Types.Components.MultipleUnit.MultipleUnitGenericControl>();
                shared.ConnectionId = connection;
                shared.PortId = control + ".EXT_IN";
                shared.ResetOnConnectionChange = false;             // coupling up must not switch a converter off
                shared.EnsureNotches = notches > 2;
                shared.Notches = notches;
                EditorUtility.SetDirty(shared);
            }
            // WHAT THE OTHERS ARE TOLD ("Should we list a motor overload/ WS open as one of the general faults for
            // in game locos when in uwielokrotniona?", the author, 2026-10-12). The game's locomotives have no
            // general fault lamp; what they have for one another is this observer: each says a temperature,
            // and every desk in the train lights its motor-temperature lamp for the worst said by any - steady
            // from 90, blinking from 105. It is the only word one unit has for another, so ours says
            // more with it than its motors' heat: the mod writes the port (mod/eu07_driver.cs, said_celsius).
            // (Custom Car Loader gives a vehicle with the cable an observer with no port: one that says nothing.)
            CCL.Types.Proxies.MultipleUnitStateObserverProxy says = top.gameObject.AddComponent<CCL.Types.Proxies.MultipleUnitStateObserverProxy>();
            says.temperaturePortId = "eu07.SAID_CELSIUS";
            EditorUtility.SetDirty(says);
        }
    }

    // ---- wipers --------------------------------------------------------------------------------
    // Four, one over each outer pane of each cab, each with a valve of its own on that cab's
    // sill, as the real ones have ("start on that": horn, sanding, wipers - the author,
    // 2026-10-11). The model brings each arm with its blade as a part of its own and says where
    // its spindle is and which way it points (model/eu07_blockout.py prints both; the figures
    // here are those, and are checked against the part). To the game each is a rotary wiper
    // with a controller of its own: off, slow, fast. They swing whether or not anybody is
    // aboard, and sound as the game's own air-driven wipers do. They wipe no rain yet: these
    // windscreens are plain glass, not the game's wet windows.
    // The model's frame is x across, y along, z up; this vehicle's is (-x, z, -y) of it - the
    // body stands in it turned half round - so the model's cab B is the vehicle's cab A.
    private static readonly (string part, string name, string control, Vector3 spindle, Vector3 points)[] wiper_parts =
    {
        ("Wiper_B_L", "WiperLeftA" , "wiperLeftA" , new Vector3( 0.8300f, -7.0185f, 3.3700f), new Vector3( 0.2578f, -0.9395f, 0.2255f)),
        ("Wiper_B_R", "WiperRightA", "wiperRightA", new Vector3(-0.8300f, -7.0185f, 3.3700f), new Vector3(-0.2578f, -0.9395f, 0.2255f)),
        ("Wiper_A_L", "WiperLeftB" , "wiperLeftB" , new Vector3(-0.8300f,  7.0185f, 3.3700f), new Vector3(-0.2578f,  0.9395f, 0.2255f)),
        ("Wiper_A_R", "WiperRightB", "wiperRightB", new Vector3( 0.8300f,  7.0185f, 3.3700f), new Vector3( 0.2578f,  0.9395f, 0.2255f)),
    };
    private static readonly float[] wiper_speeds = { 0.0f, 0.7f, 1.3f };      // sweeps a second: off, slow, fast

    private static void make_wipers(Transform top, Transform model, GameObject source, int layer)
    {
        foreach ((string part, string name, string control, Vector3 spindle, Vector3 points) in wiper_parts)
        {
            if (!door_frames.TryGetValue(part, out Matrix4x4 frame))
                throw new KeyNotFoundException("the body brought no " + part);
            Vector3 at = new Vector3(-spindle.x, spindle.z, -spindle.y), axis = new Vector3(-points.x, points.z, -points.y).normalized;
            Transform shape = child(source.transform, part);
            // the spindle: its own z along the axis the arm turns about
            Transform driver = node(model, name, Vector3.zero).transform;
            driver.position = top.TransformPoint(at);
            driver.rotation = top.rotation * Quaternion.LookRotation(axis, Vector3.up);
            Transform arm = node(driver, "Arm", Vector3.zero).transform;
            // the part stays exactly where it stood in the body
            Matrix4x4 own = (top.worldToLocalMatrix * driver.localToWorldMatrix).inverse * frame;
            GameObject shown = node(arm, "Blade", own.GetColumn(3));
            shown.transform.localRotation = own.rotation;
            shown.transform.localScale = own.lossyScale;
            Mesh mesh = shape.GetComponent<MeshFilter>().sharedMesh;
            shown.AddComponent<MeshFilter>().sharedMesh = mesh;
            shown.AddComponent<MeshRenderer>().sharedMaterials = shape.GetComponent<MeshRenderer>().sharedMaterials.Select(material => body_material(material.name)).ToArray();
            foreach (Transform piece in driver.GetComponentsInChildren<Transform>(true))
                piece.gameObject.layer = layer;
            // its far end, and how near the part comes to the spindle the figures give
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
            // Parked, the arm slants to one side of straight down the pane; it swings to as far
            // the other side. Which way round that is, is tried: the way that brings its end
            // under the spindle.
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
            // NO WipersSimControlInput: the game takes ONE for a vehicle (SimController.wipersController),
            // so of four only the first found was ever wired - cab A's left wiper swung and the
            // driver's never did, on one object or on four ("the drivers side wiper doesnt wipe
            // for some reason", the author, 2026-10-11). The mod sets each controller's speed from
            // its own control (mod/eu07_driver.cs), finding the controller by this object's name.
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

    // ---- lights --------------------------------------------------------------------------------
    // The vehicle had none: lenses that were bright paint, and a simulation with headlight
    // controls that led nowhere. These are the game's own headlights (HeadlightsMainController),
    // which take ONE number for an end and show the setup of that number. The real desk has a
    // SWITCH for every lamp ("light control is per cab ... theyre all switches", the author,
    // 2026-10-10): this cab's left, top and right white, its two red tail lamps ("we have 2
    // types per lamp. red and white (only for bottom)"), and a dimming switch for each white
    // lamp ("so wheres the przyciemnianie reflektora?" - on the desk top, one a lamp, in the
    // reference cab). So an end has a setup for every way its eight switches can stand - 256 -
    // and the mod (eu07_driver.cs) turns the switches into the number: left 1, top 2, right 4,
    // red left 8, red right 16, and NOT dimmed 32, 64, 128 for left, top, right. Left and right
    // are as one sits in that end's cab. Undimmed, a white lamp throws a beam.
    private static void make_lights(Transform top, Transform body)
    {
        Material white_unlit = body_material("eu07_lamp"), red_unlit = body_material("eu07_red_lens");
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
        optimizer.headLightGlassDisableDistance = 200.0f;          // CCL's "recommended closed cab values"
        Transform lights = node(top, "[headlights]", Vector3.zero).transform;
        MeshRenderer[] lenses = body.GetComponentsInChildren<MeshRenderer>(true).Where(one => one.name.StartsWith("Lamp_") || one.name.StartsWith("Headlight_top_")).ToArray();
        if (lenses.Length != 10)
            throw new InvalidDataException($"the body brought {lenses.Length} lamp lenses, expected 10 (six white, four red)");

        foreach (bool front in new[] { true, false })
        {
            float end = front ? 1.0f : -1.0f;
            Transform group = node(lights, front ? "Front" : "Rear", Vector3.zero).transform;
            // this end's lenses: by where they are, not by their names (the body comes in turned half round);
            // side +1 is the right-hand lamp as one sits in this end's cab, -1 the left
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
                // the glare: a flat piece the game gives its own material and shows from afar
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
                        beam.intensityInside = 0.42f;             // the game's DE6 / DH4 / DM3 figures (CCL's defaults)
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
                // DARK WHERE THE CABLE IS IN. The game's own lamps are each told which end's multiple-unit cable puts
                // them out, so that nothing burns between two coupled locomotives; ours were told nothing, and
                // behind a DH4 she lit the end against it, white and then red, while the DH4's own coupled end
                // stayed dark ("i set the Lights R to the red rear, only the WE's lights go red, not the locos",
                // the author, 2026-10-12, with two pictures). Every lamp of an end goes out with that end's cable in.
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

        // A lamp in each cab's ceiling, on the cab-light switch, whose glass glows when it burns
        // ("cablight - emits okay, no visual light source", the author, 2026-10-10).
        // IT IS A DOME ("i think we need to make this into a dome light, instead of a strip which kinda
        // doesnt adhere", the author, 2026-10-12). It was a strip 1.6 m long across the cab: a straight box
        // under an arched ceiling, which touched it nowhere but at its middle. Now a round fitting on the
        // centre line, where the lining is highest and - from the back wall to 0.7 m ahead of it - level
        // along the cab: a black ring let into the lining (across the cab the arch falls 11 mm in the ring's
        // radius, so the ring starts 14 mm above the lining's crown and is buried all round), and in it an
        // opal glass 6 cm deep. Half a metre ahead of the back wall: behind the driver's head, as the strip.
        const float cab_ceiling = 3.8715f;                                    // the lining on the centre line over the rail: measured in the model
        List<Renderer> covers = new List<Renderer>();
        GameObject[] cab_lights = new[] { 1.0f, -1.0f }.Select(end =>
        {
            string name = end > 0.0f ? "CabLightA" : "CabLightB";
            Vector3 at = new Vector3(0.0f, cab_ceiling, end * (cab_back + 0.50f));
            Transform fitting = node(top, name + "Fitting", at).transform;
            fitting.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.forward);      // a turned shape stands out along its own -z: down
            shaped(fitting, "Base", turned("cab_dome_base", 32, false, (0.118f, -0.014f), (0.118f, 0.026f), (0.110f, 0.033f), (0.099f, 0.033f), (0.099f, 0.020f)),
                body_material("eu07_black"), Vector3.zero);
            // (the glass starts inside the ring's bore, 2 mm clear of it all round, and comes down through its mouth)
            GameObject glass = shaped(fitting, "Cover", domed("cab_dome_glass", 32, 9, 0.097f, 0.022f, 0.060f), white_unlit, Vector3.zero);
            covers.Add(glass.GetComponent<MeshRenderer>());
            // ITS LIGHT comes from the dome, and is stopped by what stands in its way: one lamp under the
            // glass shining straight down in a wide cone and CASTING SHADOWS - the back wall stops it, so the
            // machine room stays dark unless the door is open ("the cab light leaks through into the machine
            // room", the author, 2026-10-11, of lamps that shone all round and cast none). A lamp that
            // casts shadows is dear, and a cone is dim toward its edge: so the cab ahead is filled by a
            // second, which casts none and shines forward and down in a cone whose rear edge is upright -
            // none of it crosses the back wall's plane. (The strip had four of those along it and a fifth,
            // shadowed, turned back at the wall.)
            // TWO things turn a cab's lamps on and off in the game: its switch (CabLightsController,
            // which also lights the cover), and how far away the player is (CarLightsOptimizer,
            // which knows nothing of the switch). Given the SAME object, the second turned the
            // lamps on as one walked up, switch or no switch, and the cover stayed dark ("the
            // cabin spawns with the top cab light emitting, but the model isnt actually lit",
            // the author, 2026-10-11). So: a group for the distance, and in it the lamps for the switch.
            GameObject group = node(top, name + "Group", Vector3.zero);
            GameObject made = node(group.transform, name, at + new Vector3(0.0f, -0.105f, 0.0f));
            // The switch's side wants a Light ON the object it turns (it hangs the game's ItemLight
            // on it, which without one logs an error and throws on its way out): a lamp that gives nothing.
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
            source.spotAngle = 94.0f;                                       // 47 a side of 48 from the upright: nothing behind
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

        // THE MACHINE ROOM'S LAMPS ("back to the compressors and the machinery room", the author,
        // 2026-10-11; the room had none and was dim). Four along the passage's ceiling. They
        // are one object, EU07RoomLights, which the MOD turns on and off (mod/eu07_driver.cs:
        // a switch by each passage door, the lighting's breaker): the lamps, and a lit cover over
        // each dark one. The two end lamps cast shadows, so that a cab's back wall stops them;
        // the two inner ones reach no further than the room.
        if (desk_v2)
        {
            // ("the lights arent actually adhering", the author, 2026-10-12: they were box fittings of
            // the build's own at a height of its own, hanging in the air under the roof's arch and
            // under the model's round lamps, which were there all the time. Now each light is AT one
            // of the model's lamps - a round glass in a black ring on the roof, 0.9 and 3.4 m either
            // way from the middle - and what lights up is a disc on that glass.)
            GameObject room_lamps = node(top, "EU07RoomLights", Vector3.zero);
            foreach (float along in new[] { -3.40f, -0.90f, 0.90f, 3.40f })
            {
                Vector3 glass = new Vector3(0.0f, 3.795f, along);                  // the foot of the model's glass
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

    // A material that shines by itself: what a lit lamp's lens is given.
    private static Material glowing(string name, Color colour, float strength)
    {
        Material made = new Material(Shader.Find("Standard")) { color = colour };
        made.EnableKeyword("_EMISSION");
        made.SetColor("_EmissionColor", colour * strength);
        made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.mat");
        return made;
    }

    // The model's materials, by the names model/eu07_blockout.py gives them: colour, how
    // metallic, how smooth, and for the cab's panes how clear.
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
        ("eu07_red_lens"  , new Color(0.50f, 0.03f, 0.03f), 0.0f, 0.80f),
        ("eu07_grille"    , new Color(0.10f, 0.11f, 0.11f), 0.5f, 0.40f),
        ("eu07_lining"    , new Color(0.62f, 0.66f, 0.60f), 0.0f, 0.15f),
        ("eu07_cab_floor" , new Color(0.20f, 0.20f, 0.21f), 0.0f, 0.10f),
        // the machine room (model/eu07_blockout.py)
        ("eu07_room_floor"    , new Color(0.300f, 0.310f, 0.320f), 0.5f, 0.30f),      // chequer plate
        // (grey steel, as the real room is; they were grey-green, dark green and blue)
        ("eu07_cabinet"       , new Color(0.460f, 0.470f, 0.470f), 0.0f, 0.25f),      // cases and cabinets: grey sheet
        ("eu07_trim"          , new Color(0.360f, 0.360f, 0.310f), 0.0f, 0.30f),      // the cover strips over the cab lining's joints
        ("eu07_machine"       , new Color(0.230f, 0.240f, 0.250f), 0.3f, 0.35f),      // rotating machines: dark grey castings
        ("eu07_compressor"    , new Color(0.520f, 0.540f, 0.560f), 0.2f, 0.35f),      // compressors and their vessels: light grey
        ("eu07_guard_red"     , new Color(0.400f, 0.100f, 0.060f), 0.0f, 0.20f),      // the pipe rail round the main compressor
        ("eu07_board_blue"    , new Color(0.550f, 0.680f, 0.760f), 0.0f, 0.30f),      // the insulating board in the low-voltage cabinet
        ("eu07_brass"         , new Color(0.700f, 0.550f, 0.200f), 0.8f, 0.50f),      // fuse clips
        ("eu07_cage"          , new Color(0.050f, 0.050f, 0.050f, 0.45f), 0.3f, 0.30f),   // expanded-metal screens: seen through
        ("eu07_visor"         , new Color(0.060f, 0.085f, 0.070f, 0.80f), 0.0f, 0.85f),   // the sun visors: smoked sheet, seen through darkly
        ("eu07_contact"       , new Color(0.470f, 0.270f, 0.180f), 0.6f, 0.45f),          // a contactor's arm and contact: worked copper
        ("eu07_panto_red"     , new Color(0.330f, 0.075f, 0.055f), 0.1f, 0.30f),      // the pantograph's frame: red oxide
        ("eu07_mirror"        , new Color(0.800f, 0.820f, 0.840f), 1.0f, 0.96f),      // the side mirrors' glass: all metal, all smooth
        // the bogie (model/eu07_bogie.py). These are still flat colours, and so they are the colours
        // of a bogie that has RUN - black under brake dust, not black - to sit under the painted
        // body: clean black, they were the one new thing on a worked locomotive.
        ("eu07_bogie"         , new Color(0.150f, 0.132f, 0.115f), 0.0f, 0.12f),
        ("eu07_bogie_spring"  , new Color(0.165f, 0.145f, 0.125f), 0.2f, 0.18f),
        ("eu07_wheel_centre"  , new Color(0.185f, 0.155f, 0.125f), 0.1f, 0.14f),
        ("eu07_brake_shoe"    , new Color(0.200f, 0.130f, 0.090f), 0.0f, 0.10f),
        ("eu07_axlebox_cover" , new Color(0.560f, 0.450f, 0.150f), 0.0f, 0.18f),
        // the desk's parts (model/eu07_desk_parts.py)
        ("eu07_wheel"       , new Color(0.06f, 0.065f, 0.07f), 0.0f, 0.45f),
        ("eu07_desk_grey"   , new Color(0.62f, 0.63f, 0.62f), 0.0f, 0.25f),
        ("eu07_desk_beige"  , new Color(0.62f, 0.55f, 0.42f), 0.0f, 0.15f),
        ("eu07_chrome"      , new Color(0.78f, 0.79f, 0.80f), 0.9f, 0.80f),
        ("eu07_bare_steel"  , new Color(0.70f, 0.68f, 0.62f), 0.6f, 0.50f),
        ("eu07_valve_green" , new Color(0.05f, 0.27f, 0.09f), 0.0f, 0.35f),
        ("eu07_desk_green"  , new Color(0.10f, 0.42f, 0.14f), 0.0f, 0.30f),
        ("eu07_handle_green", new Color(0.09f, 0.45f, 0.16f), 0.0f, 0.40f),
        ("eu07_grip_green"  , new Color(0.07f, 0.38f, 0.30f), 0.0f, 0.15f),
        ("eu07_cap_red"     , new Color(0.68f, 0.05f, 0.04f), 0.0f, 0.50f),
        ("eu07_white"       , new Color(0.85f, 0.85f, 0.80f), 0.0f, 0.30f),
    };

    // A part of the bogie model put on an object of the template's bogie, in place of what was there.
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

    // A part of the desk made in model/eu07_desk_parts.py: its mesh, its materials by name.
    // The mesh is kept as an asset of its own, as text, so that tools/desk_clearance.py can
    // read it (a model file it cannot).
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

    // The model says where each of its parts belongs (an object's origin there is the part's
    // place here). This script has the same numbers; the two must agree to a millimetre.
    private static Vector3 placed(string name, Vector3 expected)
    {
        Vector3 found = child(parts_model.transform, name).localPosition;
        if ((found - expected).magnitude > 0.001f)
            throw new InvalidDataException($"desk part {name}: the model puts it at {found:F4}, this script at {expected:F4}");
        return expected;
    }

    // THE OUTSIDE'S PAINT ("a colouring texturing and decal pass", the author, 2026-10-12). The
    // model gives its outside two materials, eu07_ext_a (the shell) and eu07_ext_b (every other
    // outside part), a square picture each that every face has its own place in ("atlas", the
    // first set of UVs), and a second set in metres for what tiles ("world"). The pictures are
    // ours: baked by the model's build and painted by tools/paint.py into model/paint/ -
    //   ext_<a|b>_<state>_albedo   the colour, shade and dirt in it;
    //   ext_<a|b>_<state>_mask     red how metallic, green the shade again, alpha how smooth.
    // On top of that the game's own fine wear - scratches and grain, the detail maps of its DE2 -
    // which Custom Car Loader puts into the material as the game starts (its "procedural
    // materials": the pack names ours, below); a grey picture and a flat one stand in their
    // places until then, so that the shader that takes them is in the bundle.
    // A bake belongs to ONE packing of the atlases: check_paint refuses paint made for another.
    // (the game's DE2 lays its wear picture ten times across its body's own picture - some
    // metre of locomotive a time. At one every two metres ours had blisters the size of a hand.)
    private const float paint_wear_tiles = 1.0f;               // the game's wear picture: one a metre
    private static readonly Dictionary<string, Texture2D> paint_pictures = new Dictionary<string, Texture2D>();
    private static string paint_folder => Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/paint"));

    private static void check_paint()
    {
        string of_model = File.ReadAllText(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/eu07_paint_uv.txt"))).Replace("\r", "").Trim();
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
        importer.wrapMode = largest <= 16 || folder != null ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;        // stand-ins and finishes tile
        importer.alphaIsTransparency = false;
        importer.textureCompression = colour ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
        importer.SaveAndReimport();
        Texture2D made = AssetDatabase.LoadAssetAtPath<Texture2D>(picture) ?? throw new IOException("no picture at " + picture);
        paint_pictures[file] = made;
        return made;
    }

    // a small picture of one colour, written here: what stands where the game's wear will be put
    private static Texture2D stand_in(string file, Color32 colour, bool normal)
    {
        Texture2D drawn = new Texture2D(8, 8, TextureFormat.RGBA32, false);
        drawn.SetPixels32(Enumerable.Repeat(colour, 64).ToArray());
        File.WriteAllBytes(Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_paint_" + file + ".png"), drawn.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(drawn);
        return paint_picture(file, false, 8, normal);
    }

    // THE GAME'S PAINT CANS. The game repaints a vehicle by swapping materials for a "theme";
    // ours as delivered is its worn one (DVRT). Three more states are painted (tools/paint.py)
    // and offered to three of the game's themes through Custom Car Loader: fresh paint to
    // DVRT_New, rust to Relic_Rusty, and - "a primer tex is required for that", the author,
    // 2026-10-12: the sprayer puts new paint only on a stripped surface - the livery rubbed
    // down to grey primer to Null, the game's stripped theme. They are kept at half the size
    // of the worn one's pictures - three states at full size made a bundle of some 60 MB.
    private static readonly (string state, string theme, ProceduralMaterialDefinitions.MaterialType wear)[] paint_cans =
    {
        ("worn" , "DVRT"       , ProceduralMaterialDefinitions.MaterialType.PaintDetailsOld),      // itself: so that the game counts the worn theme as one this car takes
        ("new"  , "DVRT_New"   , ProceduralMaterialDefinitions.MaterialType.PaintDetailsNew),
        ("rusty", "Relic_Rusty", ProceduralMaterialDefinitions.MaterialType.PaintDetailsRusted),
        ("primer", "Null"      , ProceduralMaterialDefinitions.MaterialType.PaintDetailsNew),
    };
    private static readonly string[] painted_names = { "eu07_ext_a", "eu07_ext_b" };
    // WHAT ONE WE-4U SHARES WITH ANOTHER over the multiple-unit cable besides the game's six: a name for
    // the connection (only vehicles that know the name take part - a diesel does not), the control, and
    // for a notched one its notches. The leader's desk then raises the other's pantographs, closes its
    // line breaker, resets its relays, starts its converter and compressors. NOT shared: the 110 V battery
    // and the breakers (a fuse box is each locomotive's own: every unit is made ready by hand), the
    // lights, the hauled-run switch.
    private static readonly (string connection, string control, int notches)[] mu_shared =
    {
        ("EU07_PANTOGRAPH_REAR", "pantographAsk", 2), ("EU07_PANTOGRAPH_FRONT", "pantograph2Ask", 2),
        ("EU07_WS_ON", "fastBreakerOn", 2), ("EU07_WS_OFF", "fastBreakerOff", 2),
        ("EU07_RELAY_RESET", "relayReset", 2), ("EU07_AUX_RESET", "auxRelayReset", 2), ("EU07_LINE_OFF", "lineContactorsOff", 2), ("EU07_WHEEL_SLIP", "wheelSlip", 2),
        ("EU07_CONVERTER", "converterSwitch", 2), ("EU07_COMPRESSORS", "compressorSwitch", 2), ("EU07_HEATING", "heatingSwitch", 2),
        ("EU07_HIGH_START", "highStart", 2), ("EU07_FIELD", "fieldLever", 7),
        // ("What about the last mile bat?", the author, 2026-10-12: it had been left out.) Off the wire the
        // units go onto their batteries TOGETHER or the follower is dead weight - or, the other way about,
        // runs its battery down with nobody having asked it to. Each still has its own charge and its own
        // battery breaker; only the switch is shared.
        ("EU07_LAST_MILE", "batterySwitch", 2),
    };
    // THE INSIDE HAS ITS PAINT TOO ("For the clean out of factory, just a plain green is fine", the
    // author, 2026-10-12). The game keeps a vehicle's inside paint apart from its outside's: a
    // second TrainCarPaint, "Interior", on the vehicle, and one on the cab's fittings that
    // follows it. Ours as delivered is worn (DVRT, each material to itself, so that the game
    // counts the theme as one this car takes); a can of fresh paint (DVRT_New) sprayed inside
    // gives the desk plain enamel with none of the wear, and the walls their new state.
    // What is offered: (state, the material as delivered, what the can puts in its place).
    private static readonly List<(string state, Material original, Material substitute)> inside_cans = new List<(string state, Material original, Material substitute)>();

    // One picture of the plates' and signs' sheet (tools/decals.py) on a panel of the build's own:
    // a flat piece in the panel's plane, looking out of it (-z), its size the sheet's own word.
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
        piece.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };              // both ways round: it is seen from one side only
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
        made.SetFloat("_UVSec", 1.0f);                                                 // the wear by the second set, in metres
        made.SetTextureScale("_DetailAlbedoMap", new Vector2(paint_wear_tiles, paint_wear_tiles));
        AssetDatabase.CreateAsset(made, $"{target}/EU07_paint_{name}_{state}.mat");
        return made;
    }

    private static Material body_material(string name)
    {
        if (body_materials.TryGetValue(name, out Material known))
            return known;
        if (name == "eu07_ext_a" || name == "eu07_ext_b")
        {
            body_materials[name] = painted(name, "worn");
            return body_materials[name];
        }
        if (name == "eu07_decals")
        {
            // THE PLATES AND SIGNS: one sheet of our own drawing (tools/decals.py -> model/decals/sheet.png);
            // the model's flat pieces carry their parts of it in their first set of UVs
            string sheet = $"{target}/EU07_decals.png";
            File.Copy(Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/decals/sheet.png")), Path.Combine(Application.dataPath, "_CCL_CARS/EU07/EU07_decals.png"), true);
            AssetDatabase.ImportAsset(sheet, ImportAssetOptions.ForceSynchronousImport);
            TextureImporter sheet_importer = (TextureImporter) AssetImporter.GetAtPath(sheet) ?? throw new IOException("no picture at " + sheet);
            sheet_importer.mipmapEnabled = true;
            sheet_importer.anisoLevel = 8;
            sheet_importer.maxTextureSize = 2048;
            sheet_importer.wrapMode = TextureWrapMode.Clamp;
            sheet_importer.textureCompression = TextureImporterCompression.CompressedHQ;
            sheet_importer.SaveAndReimport();
            Material plates = new Material(Shader.Find("Standard")) { color = Color.white };
            plates.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(sheet) ?? throw new IOException("no picture at " + sheet);
            plates.SetFloat("_Glossiness", 0.30f);
            AssetDatabase.CreateAsset(plates, $"{target}/EU07_decals.mat");
            body_materials[name] = plates;
            return plates;
        }
        if (name == "eu07_int_a" || name == "eu07_int_c" || name == "eu07_int_d")
        {
            // THE INSIDE. a: the shell's inside - the shell's own pictures, without the outside's
            // weather-beaten fine wear, and no can's business. c: the cabs' fittings. d: the machine
            // room. (The cabs' fittings are little surface: their pictures are kept at half size.)
            string pictures = name == "eu07_int_a" ? "ext_a_worn" : "int_" + name.Substring(name.Length - 1);
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
                // THE LINING HAS A GRAIN ("is it me or the walls and ceilling are kinda LQ", the author, 2026-10-12). Its
                // picture is the shell's, 5 mm a dot: from a seat it was a smear. Over it, by the model's own
                // measure in metres, brushed enamel's orange peel and brush marks (tools/finishes.py, peel),
                // three to the metre: a third of a millimetre a dot, whatever the picture under it.
                string finishes = Path.GetFullPath(Path.Combine(Application.dataPath, "../../model/finishes"));
                indoors.SetTexture("_BumpMap", stand_in("flat", new Color32(128, 128, 255, 255), true));
                indoors.EnableKeyword("_NORMALMAP");
                indoors.SetTexture("_DetailAlbedoMap", paint_picture("peel_albedo", true, 512, false, finishes));
                indoors.SetTexture("_DetailNormalMap", paint_picture("peel_normal", false, 512, true, finishes));
                indoors.SetFloat("_DetailNormalMapScale", 0.28f);      // (0.55 was corduroy where the light grazes the brow over the windscreen)
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
            // the Standard shader's "Transparent" mode, set by hand as its inspector does
            made.SetFloat("_Mode", 3.0f);
            made.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            made.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            made.SetInt("_ZWrite", 0);
            made.DisableKeyword("_ALPHATEST_ON");
            made.DisableKeyword("_ALPHABLEND_ON");
            made.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            made.renderQueue = 3000;
        }
        if (name == "eu07_brake_shoe")
        {
            // the game makes hot brake blocks glow through _EmissionColor: the keyword must be on (see lens())
            made.EnableKeyword("_EMISSION");
            made.SetColor("_EmissionColor", new Color(0.002f, 0.002f, 0.002f));
            made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        AssetDatabase.CreateAsset(made, $"{target}/EU07_body_{name}.mat");
        body_materials[name] = made;
        return made;
    }

    // The EU07's own handles: a simulation control each, fed by a switch or lever on the desk.
    private static readonly string[] controls = { "fastBreaker", "relayReset", "lineContactorsOff", "highStart", "fieldLever" };

    private static void wire_simulation(Transform sim)
    {
        // The railcar's brake cut-out valve starts CUT OUT: the brake pipe then never fills and
        // the driver's valve does nothing ("train brake doesnt brake the loco, ive been braking
        // with independant" - the author, 2026-10-10). A locomotive working alone has it cut in,
        // and gets it so at every load.
        ExternalControlDefinitionProxy[] cutouts = sim.GetComponentsInChildren<ExternalControlDefinitionProxy>(true).Where(one => new SerializedObject(one).FindProperty("ID").stringValue == "brakeCutout").ToArray();
        if (cutouts.Length != 1)
            throw new System.Exception($"EU07: {cutouts.Length} brakeCutout controls in the simulation, expected 1");
        SerializedObject cut_in = new SerializedObject(cutouts[0]);
        number(cut_in, "defaultValue", 1.0f);
        flag(cut_in, "saveState", false);
        cut_in.ApplyModifiedPropertiesWithoutUndo();
        // THE HORN. His railcar's horn control rests at 0 and has no handle on any desk. The real
        // valve's lever rests in the MIDDLE - one way the low tone, the other the high - so the
        // control rests at a half and the horn reads how far it stands from there; which way,
        // the mod reads for itself (mod/eu07_driver.cs).
        if (desk_v2)
        {
            // (the control is not an ExternalControlDefinition but one that smooths: found by its ID, whatever its kind)
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
                // (the game's lamp selectors rest at 0.4, which is "off" of their six positions)
                number(made, "defaultValue", on_at_start_v2.Contains(control) ? 1.0f : control.StartsWith("trainLights") ? 0.4f : 0.0f);
                flag(made, "saveState", saved_v2.Contains(control));
            }
            made.ApplyModifiedPropertiesWithoutUndo();
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
            // A NEW LOCOMOTIVE IS COLD ("drop all the breakers on the loco so its a true cold
            // start", the author, 2026-10-12): the battery's switch off, the motors' breaker and
            // every breaker under the flap out. Only the three fuses the mod sets by itself
            // start in. (A save keeps each as it was left.)
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
            // THE MOTOR ISOLATORS ARE LOCKED AWAY, as the real ones are behind the high-voltage
            // compartment's door, by the game's own lock of one handle by another (ControlBlocker):
            //   their box's DOOR is held shut while the interlock cock stands at P;
            //   the COCK cannot go back to P while that door is open;
            //   each ISOLATOR is held while the door is not open (and the cock at P, which is the
            //   same thing said twice), and while the wheel is off 0.
            // (A held lever can be pointed at and names itself; it will not move.)
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
            // A FOLLOWER'S DESK IS HELD. The game holds the handles of a unit that another drives through the
            // same locks, all at once (ControlsBlockController.MUSlaveBlockAllControls) - but only handles
            // that HAVE a lock. So every control the leader has gets one, with nothing of its own to hold
            // it: it holds only when the game, or the mod for a cab that is cut out, says "follower".
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
            // The locomotive's own brake. His railcar's brake set-up says it has one, but
            // his desk has no handle for it; the game takes it through an overridable control.
            GameObject direct = new GameObject("indBrake");
            direct.transform.SetParent(sim, false);
            direct.transform.SetSiblingIndex(child(sim, "traction").GetSiblingIndex());
            SerializedObject made = new SerializedObject(direct.AddComponent<ExternalControlDefinitionProxy>());
            text(made, "ID", "indBrake");
            made.ApplyModifiedPropertiesWithoutUndo();
            OverridableControlProxy overridable = direct.AddComponent<OverridableControlProxy>();
            overridable.portId = "indBrake.EXT_IN";
            overridable.ControlType = OverridableControlType.IndBrake;
            // AND EACH OF THE GAME'S CONTROLS KNOWS ITS LOCK. The locks above hold the HANDLES of a follower;
            // but a key, or the game's remote controller, moves a control through its overridable control,
            // which asks only the lock it has been told of - and these had been told of none. Found dry
            // ("Could you dry test the stock to ours test both ways", the author, 2026-10-12): by radio behind
            // a DE6, held 23 of 23, ours still took a throttle of 0.9 where the DE6 behind ours refused it.
            // THE LAMPS ARE NOT THE GAME'S TO SHARE. The template's simulation gave the two headlight ports the game's
            // overridable controls, and over a multiple-unit cable the game shares those: a diesel's
            // selector, a few positions, with ours - which is no selector but a NUMBER, one of 256
            // setups made of this cab's eight lamp switches (make_lights), written by the mod every
            // step. So a diesel's selector moved, ours took its value for some setup of lamps for a
            // moment, the mod wrote its own number back, and THAT went up the cable to the diesel as a
            // selector position it has not got ("A MU issue with the stock light controls. our locos
            // lights freak out. when changing mode", the author, 2026-10-12; seventy "state not defined"
            // in the game's log of that run). So the game's two controls come OFF the number's ports -
            // and go onto two ports of their own, trainLightsFront and trainLightsRear, which hold
            // nothing but what a leading vehicle's selector says ("the MU light controller in our loco
            // doesnt listen to given commands", the author, the same evening). The mod turns that into
            // her lamps while she is led by a foreign desk, and never writes there itself: nothing of
            // hers goes up the cable.
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

        // THE MOTORS' HEAT. The game keeps a temperature for the traction motors (tmHeat): its
        // lamp, its wear, the blown fuse and the burnt motor all go by it. Here it stood at 25
        // for good - it was heated by HIS motors, which take no current - while ours got hot
        // unseen. Its heat now comes from eu07.MOTOR_HEAT, through which the mod makes it
        // follow the temperature our circuit works out (mod/eu07_driver.cs); and the game's own
        // cooler is stopped, the cooling being part of what is worked out.
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

        // His traction motors stay in the simulation, off the wheels - and must be off the
        // throttle too. Left on it they are fed full volts by a throttle that is really
        // the controller wheel while the wheels turn at a speed that is none of their
        // doing; they take damage, and the game blows the electrical powertrain up
        // (the author, in the cab, 2026-10-10). A reference cannot simply be left open -
        // the simulation will not start - so they read a port of ours that is always 0.
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

    // ---- the desk --------------------------------------------------------------------------
    private static void shape_desk(GameObject root)
    {
        root.name = "EU07_interactables";
        Transform desk = child(root.transform, "ControlTemplate");
        move_z(desk, body_half - 0.47f);
        notches(child(desk, "Throttle"), 44);

        // His train brake lever carries the throttle's key actions, so the throttle keys
        // move both and the brake keys move nothing (the author, 2026-10-10).
        keys(child(desk, "TrainBrake"), "BrakeIncremental", "BrakeAbsolute");
        // And all four switches answer the electrics fuse's key.
        keys(child(desk, "MotorFuse"), "TractionMotorFuse");
        keys(child(desk, "Pantograph"), "Pantograph");
        no_keys(child(desk, "Pantograph2"));

        // The field lever: his train brake again, seven notches, on the dynamic brake's keys,
        // which this locomotive has no other use for.
        Transform field = clone(child(desk, "TrainBrake"), "FieldLever", new Vector3(0.05f, 1.00f, 0.0f), "fieldLever.EXT_IN", "eu07/field_lever");
        notches(field, 7);
        keys(field, "DynamicBrakeIncremental", "DynamicBrakeAbsolute");

        // Four switches after his own four, along the same row.
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

    // Sets the key actions of a control, in the order its keyboard components come.
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

    // A control with no key has no keyboard component: an empty action name is not a name
    // in ControlActions.Names, and CCL's InputValidator fails it ("useAction is invalid").
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
        // The label is whatever component on it has a text key.
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

    // ---- desk v2 ---------------------------------------------------------------------------
    // docs/desk/design_desk.md, built: the 303E's desk laid out as the real one (docs/research/research_cab.md), from
    // primitives, under ONE parent, DeskA, so that it can later be lifted into a cab interior
    // unchanged. The default since 2026-10-10; see docs/desk/DESK_V2_REPORT.md section 6 for its first run.
    //
    // Frames, as docs/desk/design_desk.md:
    //   desk   origin on the cab floor, centreline, driver-side edge of the desk; +X to the
    //          driver's right, +Y up, +Z toward the windscreen. The driver sits at x +0.50.
    //   panel  X right, Y "up the panel", -Z out of the panel toward the viewer: the frame
    //          Knsgf's switches and gauges are built in. PanelTop lies flat (panel -Z up,
    //          panel +Y toward the windscreen), PanelSlope is the 45-degree face, and
    //          PanelPedestal lies flat on the controller's pedestal under the wheel's hub.
    //
    // Angles: a positive hinge or needle angle about an axis is taken to turn the way
    // Quaternion.AngleAxis does, i.e. CLOCKWISE seen from the axis's positive end. Not
    // checked in the game; every direction that depends on it is one constant below.

    // Captions. The mod is for everybody, so the caption a player sees by default is
    // ENGLISH (the author, 2026-10-10); the wording of the real plates is kept as the Polish
    // translation, which the game shows when it is set to Polish.
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
        // (a caption is two lines, and not of a fixed count of letters: "Converter, heating overload" ended "OVERL...", while
        // "Line contactors open" is shown whole - "is fine. doesnt tripple dot", the author, 2026-10-12)
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

    // docs/desk/design_desk.md (b8). The mod (mod/eu07_driver.cs) reads them by these ids.
    private static readonly string[] controls_v2 =
    {
        "fastBreakerOn", "fastBreakerOff", "relayReset", "auxRelayReset", "lineContactorsOff", "wheelSlip",
        "faultAck",                         // the fault lamp's "seen it" button
        "vigilance",                        // the driver's answer to a vigilance device in the instrument cupboard (the game's own gadget)
        "vigilanceCock",                    // the cock before that device's brake valve, on the cab's back wall: 1 open (as it starts), 0 shut - it cannot brake
        "cupboardLeft", "cupboardMiddle", "cupboardRight",     // the three plywood doors under the desk: 0 shut, up to 1 open
        // THE HIGH VOLTAGE BY HAND ("motor isolators and the knife switch", the author, 2026-10-12):
        "hvInterlock",                      // the cock on the cab's back wall: 1 = P (pantographs may go up, the high voltage shut away), 0 = open
        "hauledRun",                        // the switch on the cab's back wall behind the driver: 1 = jazda ciagniona (hauled: takes no power), 0 = train running
        "cabCutout",                        // beside it: 1 = this cab is cut out - in a multiple unit the leader drives this locomotive and shows its lamps
        "motorIsolator12", "motorIsolator34",       // a knife switch a pair of motors, in a box in the cage: 1 = pulled, the pair cut out
        "isolatorDoor",                     // that box's door: 0 shut (as it always starts), up to 1 open
        "trainLightsFront", "trainLightsRear",      // no handle: what a LEADING vehicle's lamp selector says, by the game's cable or radio (wire_simulation)
        "lvDoor",                           // the low-voltage cabinet's door over the converter's knife switch, likewise
        "converterKnife",                   // the knife switch in the low-voltage cabinet: 1 = pulled, the converter cannot start
        "instrumentLight",                  // the lamps over the meters
        "highStart", "converterSwitch", "compressorSwitch", "heatingSwitch", "fieldLever",
        "lightLeftA", "lightTopA", "lightRightA", "redLeftA", "redRightA", "lightDimLeftA", "lightDimTopA", "lightDimRightA",
        "lightLeftB", "lightTopB", "lightRightB", "redLeftB", "redRightB", "lightDimLeftB", "lightDimTopB", "lightDimRightB",
        "odometer",                         // no handle: the kilometres run, which the mod writes and the game saves
        "wiperLeftA", "wiperRightA", "wiperLeftB", "wiperRightB",      // a valve a wiper: off, slow, fast
        "batterySwitch",                    // the last-mile battery's switch,
        "batteryCharge",                    // and, with no handle, what is left in it (0-1): the mod writes it, the game saves it
        // The pantographs' desk switches ASK; what the valves get is the railcar's own
        // pantographSwitch controls, which the mod writes: the asking, if the 110 V is there
        // and the pantographs' breaker is in (mod/eu07_driver.cs).
        "pantographAsk", "pantograph2Ask",
        "cabHeatingSwitch",                 // under the flap
        "lowBatteryCharge",                 // what is left in the 110 V battery (0-1), kept as the other
        "smallCompressor",                  // the pantographs' small compressor: its switch, on the low-voltage board
        "pantoCock",                        // the three-way cock on the pneumatic frame: 1 = the pantographs on the small compressor
        "controlAirCock", "pantoAirCock", "drainCock", "mainResCock",      // the frame's labelled cocks: 1 = open
        "pantoAir",                         // with no handle: the pantographs' air (bar + 1), which the mod writes and the game saves
        "roomLightA", "roomLightB",         // the machine room's lamps: a switch by each passage door, either changes them
        // no handle: what each of the cage's 48 contactors has been through - its soot and its heat, three bits each, six
        // to a number (no more than six figures, whoever writes it down) - which the mod writes and the game saves
        // (mod/eu07_driver.cs: mark_contactors)
        "contactorMarks0", "contactorMarks1", "contactorMarks2", "contactorMarks3", "contactorMarks4", "contactorMarks5",
        "contactorMarks6", "contactorMarks7", "contactorMarks8", "contactorMarks9", "contactorMarks10", "contactorMarks11",
        "contactorMarks12", "contactorMarks13", "contactorMarks14", "contactorMarks15",
    };
    // THE 110 V BREAKERS, under the flap in the assistant's desk: each a fuse of the game's own
    // fuse box - which keeps them over a save, and lets the game or the mod throw one out and
    // the lever follow. In the order they stand: far row, then near row, from the side wall.
    // The game's traction-motor fuse IS one of them: "control".
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
    // And three with no lever, that the mod alone sets: is the thing FED - the 110 V there and
    // its breaker in. The game's lights and sander are given these as their power fuse.
    private const string fed_headlights = "fusebox.FED_HEADLIGHTS", fed_lighting = "fusebox.FED_LIGHTING", fed_sanding = "fusebox.FED_SANDING";
    // A fourth: what the GAME'S GADGETS mounted in this locomotive are powered by (shape_vehicle) - the 110 V
    // there and the control circuits' breaker in.
    private const string fed_gadgets = "fusebox.FED_GADGETS";
    // The switches and the field lever keep their place over a save; the buttons do not.
    // (trainLights...: what the leader's lamp selector last said. The game sends it only when the selector MOVES, so
    // a train loaded from a save would have her dark behind a lit leader until its driver touched his switch.)
    private static readonly string[] saved_v2 = { "trainLightsFront", "trainLightsRear", "highStart", "converterSwitch", "compressorSwitch", "heatingSwitch", "fieldLever",
        "lightLeftA", "lightTopA", "lightRightA", "redLeftA", "redRightA", "lightDimLeftA", "lightDimTopA", "lightDimRightA",
        "lightLeftB", "lightTopB", "lightRightB", "redLeftB", "redRightB", "lightDimLeftB", "lightDimTopB", "lightDimRightB", "odometer",
        "batterySwitch", "batteryCharge", "pantographAsk", "pantograph2Ask", "cabHeatingSwitch", "lowBatteryCharge", "pantoAir", "pantoCock", "controlAirCock", "pantoAirCock", "drainCock", "mainResCock", "roomLightA", "roomLightB",
        "hvInterlock", "hauledRun", "cabCutout", "motorIsolator12", "motorIsolator34", "converterKnife", "instrumentLight", "vigilanceCock",
        "contactorMarks0", "contactorMarks1", "contactorMarks2", "contactorMarks3", "contactorMarks4", "contactorMarks5",
        "contactorMarks6", "contactorMarks7", "contactorMarks8", "contactorMarks9", "contactorMarks10", "contactorMarks11",
        "contactorMarks12", "contactorMarks13", "contactorMarks14", "contactorMarks15" };
    // The converter's and compressors' switches were ON at spawn, as the crude version forced
    // them. They spawn OFF now, as the real desk stands (the author, 2026-10-11, to a list that
    // had it first: "i concur with that"): a new locomotive is cold, and is started.
    // The batteries are FULL at spawn, and in a save made before each had one.
    private static readonly string[] on_at_start_v2 = { "batteryCharge", "lowBatteryCharge", "hvInterlock", "controlAirCock", "pantoAirCock", "mainResCock", "vigilanceCock" };

    private static readonly (string id, DVPortType type, DVPortValueType value)[] ports_v2 =
    {
        ("LAMP_LSGP", DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("LAMP_BATTERY"  , DVPortType.READONLY_OUT, DVPortValueType.STATE),
        ("BATTERY_CHARGE", DVPortType.READONLY_OUT, DVPortValueType.GENERIC),       // 0-100
        ("PANTO_AIR"     , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),       // bar
        ("LOW_VOLTS"     , DVPortType.READONLY_OUT, DVPortValueType.GENERIC),       // the 110 V bus
        ("LAMP_FAULT"    , DVPortType.READONLY_OUT, DVPortValueType.STATE),         // the game's lamp codes: 0 off, 1 on, 3 blinking
        ("LAMP_VIGILANCE", DVPortType.READONLY_OUT, DVPortValueType.STATE),         // likewise: blinking while the vigilance device asks
        ("LAMP_SHP"      , DVPortType.READONLY_OUT, DVPortValueType.STATE),         // the train stop's lamp in the same box: nothing writes it yet (no SHP is built)
        ("SAID_CELSIUS"  , DVPortType.READONLY_OUT, DVPortValueType.TEMPERATURE),   // what the other units of a train are told of this one: see shape_vehicle
    };

    // The model runs on half the real volts and 1.5 times the real amps
    // (eu07_traction_circuit.cs: line_voltage_ratio, current_ratio), so the 600 A relays act
    // at 900 A of the model's. The meters are scaled back to real figures, so that the dials
    // read as the real ones (0-800 A) and the relays act at 600 on them. NOT the voltmeter:
    // the valley's wire is 1.5 kV and it shows that, on a 0-2 kV face ("one thing, we are not
    // on 3kv", the author, 2026-10-10, of a meter that doubled it).
    // Nor, since the same evening, the ammeters ("make the ammeters honest too"): the model's
    // own amperes on a 0-1200 A face, red marks where the relays act (900, and 1125 on high start).
    private const float ammeter_scale = 1.0f, voltmeter_scale = 1.0f;

    // ASSUMED SIGNS (see the comment at the top of this section). If the wheel adds power
    // ANTICLOCKWISE in the game, make this Vector3.down; nothing else changes.
    private static readonly Vector3 controller_axis = Vector3.up;
    // The field bar points left at 0 and swings toward the driver: anticlockwise from above.
    private static readonly Vector3 field_lever_axis = Vector3.down;

    private static Material black, white, grey_panel, desk_green, valve_green, teal, silver, red_cap, green_cap, chrome, bakelite, ivory, door_metal, desk_wood, yellow_cap;

    private static void shape_desk_v2(GameObject root)
    {
        root.name = "EU07_interactables";
        make_materials();
        Transform old = child(root.transform, "ControlTemplate");
        // THE ARRANGEMENT is the real desk's, as the driver on the right has it: from his
        // left, a toggle panel, the lamp-and-button panel, the controller on a pedestal of
        // its own, the brake valve by the wall; the meters and brake gauges on the slope
        // ahead. Where each thing stands was checked against the cab of the MaSzyna
        // simulator's 303E, looked at and not copied (its licence keeps its files to the
        // simulator; nothing of it is in this repository). Rows are spread a little wider
        // than there, because a label plate here is 65 mm.
        //
        // The desk's own frame: x to the driver's right from the centre line, y up from the
        // cab floor, z forward from the desk's near edge.
        // The railcar carries a handbrake wheel on each side of its underframe; a locomotive has
        // one in each cab ("we need to move the handbrake's into the cabs", the author,
        // 2026-10-10). They go on the back walls, on the driver's side of the machine-room
        // door - the other side is where the cab's outside door swings. The loader's dummy
        // faces its own +z and is replaced by the game's wheel when the vehicle loads.
        // The cab doors: the model's two leaves, each on a hinge of the game's at its rear edge,
        // swinging into the cab ("getting in and out the cab is problematic, without a defined
        // ladder nor a functional door"; "we need to ... do the doors" - the author, 2026-10-10).
        GameObject body_model = AssetDatabase.LoadAssetAtPath<GameObject>(body_fbx) ?? throw new IOException("no model at " + body_fbx);
        // The hinges: the foot of each leaf's rear edge, as model/eu07_blockout.py prints them.
        // The body comes into the vehicle turned half round from the model's own frame - which
        // nobody can see, the body being the same turned half round - so the model's door "B"
        // is the one that stands at this vehicle's cab A, and the other way about.
        make_door(root.transform, body_model, "Door_cab_B", "DoorCabA", new Vector3(-1.4487f, 1.690f,  5.287f));
        make_door(root.transform, body_model, "Door_cab_A", "DoorCabB", new Vector3( 1.4487f, 1.690f, -5.287f));
        // The OTHER outside door of each end, on the driver's side: behind the cab's back wall (the
        // handbrake wheel hangs on the cab's face of it), into the machine room ("what we need is
        // the doors behind the parking brakes working", the author, 2026-10-11). Hinged at its
        // rear edge and swinging in, as the cab's; open, it stands across the floor by the door.
        make_door(root.transform, body_model, "Door_side_B", "DoorSideA", new Vector3( 1.4515f, 1.690f,  4.700f), false, 88.0f);
        make_door(root.transform, body_model, "Door_side_A", "DoorSideB", new Vector3(-1.4515f, 1.690f, -4.700f), false, 88.0f);
        // The doors from the cabs into the machine room's passage: in each cab's back wall, a
        // little to that cab's right of the middle, hinged on the edge toward its left and
        // swinging into the cab. The body's "B" end is the vehicle's cab A, as with the doors above.
        make_door(root.transform, body_model, "Door_room_B", "DoorRoomA", new Vector3(-0.296f, floor_top,  cab_back), true);
        make_door(root.transform, body_model, "Door_room_A", "DoorRoomB", new Vector3( 0.296f, floor_top, -cab_back), true);
        // The side windows' opening panes and the sun visors ("the blinds and windows i think need to move
        // now", the author, 2026-10-12).
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
            float end = index == 0 ? 1.0f : -1.0f;               // cab A, cab B
            handbrakes[index].localPosition = new Vector3(end * 0.95f, floor_top + 1.05f, end * (cab_back + 0.02f));
            handbrakes[index].localRotation = Quaternion.Euler(0.0f, end > 0.0f ? 0.0f : 180.0f, 0.0f);
        }
        Transform desk = node(root.transform, "DeskA", new Vector3(0.0f, floor_top, desk_z)).transform;
        // The controller's pedestal stands hardly proud of the desk top; the wheel's rim is 123 mm
        // over its plate, which leaves the room under the rim that the reverser's key and the
        // field lever lie in. These and the other numbers of the right-hand group are the
        // arrangement of the MaSzyna simulator's 303E cab, measured there and rebuilt here
        // (model/eu07_desk_parts.py; the author, 2026-10-10: "reference the actual setup").
        // (ped_z was 0.22, the pedestal's near face flush with the desk's front and its cut corners notches:
        // "Id pull it out", the author, 2026-10-12 - the station stands 9 cm proud; model/eu07_desk_parts.py)
        const float top_y = desk_top, ped_x = 0.765f, ped_z = 0.13f, ped_top = top_y + 0.012f, hub_y = ped_top + 0.123f, slope_z = 0.60f;

        // The body. No colliders: they would only catch the mouse.
        // The cabinet, with a WELL let into its top on the assistant's side: the box of the 110 V
        // breakers, under its flap (see make_breaker_box). So it is a lower part whole, and an
        // upper part in four round the well's mouth.
        // WALL TO WALL ("i realise we have gaps behind the desk", the author, 2026-10-13). The cabinet stopped 5 cm
        // short of the left wall, the brake valve's stand 3 cm short of the right one, the sloping panel 13 cm short
        // of both, and the sill behind it 35 cm short of each corner: down each of those slots was the floor.
        // THE CAB IS NOT AS WIDE AS THE BODY, and narrows all the way to the nose (eu07_hull.py, half_width: the
        // skin's outside is 1.401 from the middle at the desk's near face, 1.371 at the cabinet's back, 1.365 where
        // the sloping panel ends; the skin is 30 mm thick). The first try took the body's 1.42 for it and the desk
        // stood out through the cab's side and nose, green on blue. So each part runs to ITS OWN wall: into the
        // skin's thickness at its far end, never through it; the sliver left at its near end is 2 cm at most.
        const float cabinet_wall = 1.36f, stand_wall = 1.368f, ledge_wall = 1.39f, slope_wall = 1.33f;
        const float cab_x0 = -cabinet_wall, cab_x1 = 0.555f, cab_z0 = desk_front, cab_z1 = 0.66f;
        void cabinet(string name, float x0, float x1, float y0, float y1, float z0, float z1) => solid(desk, name, PrimitiveType.Cube, desk_green,
            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.identity, false);
        // THE CUPBOARDS UNDER THE DESK. The cabinet below the well was one solid block with three plywood doors painted
        // on its face. "one of those wood cabinets open up ... also it would make sense to open those up" (the
        // author, 2026-10-13): so it is a carcass now - a back, a floor, a head and the walls between - with three
        // hollows 45 cm deep, and each door hangs on its hinges and swings out into the cab. The one nearest the
        // driver is the INSTRUMENT CUPBOARD: boards the game's own gadgets may be mounted on, and the only place
        // in the locomotive where they may ("not the right most? because then the whole cluster of controls and
        // stuff is in the middle, leaving stuff in the other cabinets").
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
        // What is in them. The instrument cupboard: a plywood back, floor and cheeks, each a REAL-SIZE mesh (the
        // game makes its mounting colliders from the mesh as it is, with no scale: CustomizationPlacementMeshes),
        // named to the game on this prefab's root. Nothing else of the locomotive is, so nothing else takes a gadget.
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
        // The other two: a shelf each, and what a crew keeps in a desk.
        foreach ((string cup_name, float cup_x0, float cup_x1, string cup_port, string cup_label) in cupboards.Take(2))
            solid(desk, cup_name + "Shelf", PrimitiveType.Cube, desk_wood, new Vector3((cup_x0 + cup_x1) * 0.5f, 0.345f, (cab_z0 + 0.03f + cup_back) * 0.5f),
                new Vector3(cup_x1 - cup_x0 - 2.0f * rebate, 0.012f, cup_back - cab_z0 - 0.03f), Quaternion.identity, false);
        {
            // left: the signal flags rolled on their sticks, the hand lamp, the books
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
            // middle: the tool box, an oil can, the enamel mug and its tin
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
        // behind the slope, up to under the windscreen's sill; narrow, because the nose is bowed in plan
        // THE SILL FOLLOWS THE NOSE. It was one box 1.96 m wide with a straight far edge, and the nose is bowed: in
        // the middle the wall under the windscreen stood 16 cm beyond it, and down that crescent was the floor
        // ("found another gap", the author, 2026-10-13, with a picture looking down over it); and at each end it
        // stopped 35 cm short of the corner. Now it is strips side by side, each as deep as the nose lets it be
        // at its OUTER edge (eu07_hull.py, front_y at the sill's own heights, less 12 mm: 18 mm into the skin's
        // 30, never through it). What is left open at a strip's inner end is 1.3 cm at most.
        (float inner, float outer, float deep)[] sill_strips =
        {
            (0.00f, 0.30f, 1.042f), (0.30f, 0.50f, 1.016f), (0.50f, 0.70f, 0.988f), (0.70f, 0.80f, 0.961f), (0.80f, 0.90f, 0.935f), (0.90f, 0.98f, 0.914f),
            (0.98f, 1.05f, 0.892f), (1.05f, 1.13f, 0.860f), (1.13f, 1.20f, 0.833f), (1.20f, 1.25f, 0.813f), (1.25f, 1.30f, 0.794f), (1.30f, 1.33f, 0.772f),
        };
        foreach ((float strip_from, float strip_to, float strip_far) in sill_strips)
            foreach (float end in strip_from > 0.0f ? new[] { -1.0f, 1.0f } : new[] { 0.0f })
                solid(desk, "Sill", PrimitiveType.Cube, desk_green, new Vector3(end * (strip_from + strip_to) * 0.5f, top_y + 0.06f, (0.68f + strip_far) * 0.5f),
                    new Vector3(end == 0.0f ? 2.0f * strip_to : strip_to - strip_from, 0.12f, strip_far - 0.68f), Quaternion.identity, false);
        Transform top      = panel(desk, "PanelTop", new Vector3(0.0f, top_y, 0.0f), 90.0f);
        Transform slope    = panel(desk, "PanelSlope", new Vector3(0.0f, top_y + 0.085f, slope_z), 45.0f);
        Transform pedestal = panel(desk, "PanelPedestal", new Vector3(ped_x, ped_top + 0.002f, ped_z), 90.0f);
        solid(slope, "Face", PrimitiveType.Cube, desk_green, new Vector3(0.0f, 0.0f, 0.06f), new Vector3(2.0f * slope_wall, 0.24f, 0.12f), Quaternion.identity, false);
        // The two bare metal plates the controls are mounted on.
        // The lamps' plate has the reference's outline: 786 mm wide, and its corner nearest the
        // driver's right hand cut away (the vigilance button stands there on the green).
        solid(top, "PlateLamps", PrimitiveType.Cube, silver, new Vector3(0.162f, 0.376f, 0.0005f), new Vector3(0.786f, 0.264f, 0.003f), Quaternion.identity, false);
        solid(top, "PlateLampsNear", PrimitiveType.Cube, silver, new Vector3(0.1015f, 0.157f, 0.0005f), new Vector3(0.665f, 0.174f, 0.003f), Quaternion.identity, false);

        // Of his controls only the brake cut-out and the two fuse switches stay, moved.
        //
        // THE REVERSER is not a stick: it is the driver's flat KEY, dropped on a shaft end in a
        // boss on the pedestal's plate, ahead of the hub and to its right, lying under the
        // wheel's rim (the author, 2026-10-10: "it's like the field weakening. flat, 3
        // positions"). Built pointing to the driver's right = the MIDDLE of its travel =
        // neutral. A rising value turns it anticlockwise seen from above, as the field lever
        // and the brakes: 0 = reverse, the tip toward the driver; 1 = forward, the tip toward
        // the windscreen. ASSUMED which way is which - nothing read says; the turn is 25
        // degrees each way so that the three positions can be told apart from the seat.
        string[] directions = child(old, "Reverser").GetComponentInChildren<NotchedControlCustomNamesProxy>().customNames;
        Vector3 key_at = placed("ReverserKey", new Vector3(ped_x + 0.0475f, ped_top + 0.038f, ped_z + 0.128f));
        desk_part(desk, "Boss", placed("Boss", new Vector3(key_at.x, ped_top, key_at.z)));
        GameObject reverser = make_lever(desk, "Reverser", key_at,
            new Vector3(0.095f, 0.0f, 0.0f), new Vector3(0.15f, 0.012f, 0.030f), new Vector3(0.15f, 0.0f, 0.0f),
            Vector3.down, -25.0f, 25.0f, 3, "reverser.CONTROL_EXT_IN", "ReverserIncremental", "ReverserAbsolute", directions, null, 0.0f, true);
        desk_part(reverser.transform, "ReverserKey", Vector3.zero);

        // The driver's brake valve, an Oerlikon FV4a: a casting on a plinth right of the
        // pedestal. Its handle is an arm through the spindle under the red pressure knob, a
        // short tail behind and the long arm with the grip ahead, swinging in a HORIZONTAL
        // plane 27 mm above the controller's rim - in the braking positions it passes OVER the
        // wheel, as the real one does. Running it points ahead and 25 degrees to the left;
        // pulled round toward the driver it brakes, to pointing back at him in emergency.
        Vector3 valve = placed("BrakeValve", new Vector3(ped_x + 0.331f, top_y, ped_z + 0.039f));
        desk_part(desk, "BrakeValve", valve);
        desk_part(desk, "BrakeValveCap", placed("BrakeValveCap", valve));
        GameObject brake = make_lever(desk, "TrainBrake", placed("BrakeHandle", valve + new Vector3(0.0f, 0.200f, 0.0f)),
            new Vector3(-0.11f, 0.010f, 0.0f), new Vector3(0.22f, 0.045f, 0.045f), new Vector3(-0.19f, 0.020f, 0.0f),
            Vector3.down, 0.0f, 158.0f, 11, "brake.EXT_IN", "BrakeIncremental", "BrakeAbsolute", null, null, 0.09f, true);
        desk_part(brake.transform, "BrakeHandle", Vector3.zero);
        brake.transform.parent.localRotation = Quaternion.Euler(0.0f, 65.0f, 0.0f);      // built pointing left; turned to ahead-left

        // The locomotive's own brake, an FD1: a small valve on a ledge along the right wall,
        // behind the desk by the driver's right hand, its top a little under the desk's.
        // Released its handle points straight ahead; round to the left and back toward the
        // driver applies it.
        Vector3 direct = new Vector3(1.30f, top_y, -0.242f);
        solid(desk, "DirectBrakeLedge", PrimitiveType.Cube, desk_green, new Vector3((1.15f + ledge_wall) * 0.5f, top_y - 0.30f, -0.20f), new Vector3(ledge_wall - 1.15f, 0.26f, 0.42f), Quaternion.identity, false);
        solid(desk, "DirectBrakeBody", PrimitiveType.Cylinder, black, direct + new Vector3(0.0f, -0.1045f, 0.0f), new Vector3(0.105f, 0.066f, 0.105f), Quaternion.identity, false);
        GameObject direct_brake = make_lever(desk, "IndependentBrake", direct + new Vector3(0.0f, -0.026f, 0.0f),
            new Vector3(-0.07f, 0.0f, 0.0f), new Vector3(0.142f, 0.016f, 0.022f), new Vector3(-0.12f, 0.0f, 0.0f),
            Vector3.down, 0.0f, 170.0f, 11, "indBrake.EXT_IN", "IndependentBrakeIncremental", "IndependentBrakeAbsolute", null, silver, 0.08f);
        solid(direct_brake.transform, "Grip", PrimitiveType.Sphere, black, new Vector3(-0.126f, 0.0f, 0.0f), Vector3.one * 0.048f, Quaternion.identity, false);
        direct_brake.transform.parent.localRotation = Quaternion.Euler(0.0f, 90.0f, 0.0f);   // built pointing left; turned to straight ahead

        // His brake cut-out: on the stand's top ahead of the valve, lying in the top's plane as
        // the fuse switches do (carried upright it pressed sideways).
        // (It was his control carried over, a small red tab lying on the stand behind the valve: "Whats that
        // red cylinder behind the train break ... We also need some pipes stemming so we can place the
        // cutout", the author, 2026-10-12 - asked what it was, I named the valve's own red cap.)
        // Now TWO PIPES stem from the back of the brake valve, run back along the stand 4.5 cm over
        // its top and go down into it through a flange each; and the cut-out is a COCK in the left one,
        // the brake pipe's: a brass body, a red handle shoved a quarter turn - along the pipe, cut in.
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
        // (a panel of its own lying on the stand's top with its x along the pipe: make_line_cock's handle lies along x when open)
        Transform cutout_panel = node(top, "CutoutPanel", new Vector3(cock_x, cock_z, 0.0f)).transform;
        cutout_panel.localRotation = Quaternion.Euler(0.0f, 0.0f, 90.0f);
        make_line_cock(cutout_panel, "BrakeCutout", Vector2.zero, pipe_up + 0.040f, "brakeCutout.EXT_IN", null);
        make_label(top, new Vector3(cock_x + 0.155f, cock_z, -0.003f), "eu07/brake_cutout", control_caption);
        make_breaker_box(desk, top, old);
        // THE SMALL COMPRESSOR's switch and gauge (eu07_low_voltage.cs, pantograph_air). They
        // stood together on a bright plate on the wall over the compressor: "It stands out alot.
        // how is the small compressor control board built in the ref?" (the author, 2026-10-11).
        // LOOKED AT (the reference's corridor): there is no such board. The switch is a bare
        // tumbler on the frame of the low-voltage cabinet at the passage's edge, 1.44 m over the
        // floor, among that cabinet's own breakers and knife switch; the gauge is a small
        // manometer, 56 mm across its face, 1.26 m over the floor among the pipes of the
        // pneumatic frame, 1.75 m further along; a three-way cock under it (NOT made). And the
        // room's lamps have a round black switch box on the wall by each passage door.
        // So, at cab A's end: the tumbler on the low-voltage board's face, at its edge nearest
        // the outside door; the gauge on the pneumatic frame's board, in the gap between its
        // valves and its vessels, at its passage end.
        // (since the machines were rebuilt: on the low-voltage cabinet's front POST at its end toward
        // the cab, 1.50 over the floor - the reference has it on that post, at 1.52)
        Transform low_board = node(root.transform, "SmallCompressorSwitch", new Vector3(0.3595f, floor_top + 1.50f, 3.6525f)).transform;
        low_board.localRotation = Quaternion.LookRotation(Vector3.right, Vector3.up);          // out of it is toward the passage
        make_toggle(low_board, "SmallCompressor", Vector2.zero, "smallCompressor.EXT_IN", "eu07/small_compressor", null);
        // THE HIGH VOLTAGE BY HAND ("motor isolators and the knife switch", the author, 2026-10-12).
        //  - the converter's KNIFE SWITCH in the low-voltage cabinet, inside its end toward the
        //    cab: the model has the board and its six clips, the three blades and their handle are
        //    here, and move. Pulled, the converter cannot start.
        //  - the MOTOR ISOLATORS, a two-bladed knife switch a pair of motors, on a plate on the
        //    high-voltage cage at cab A's end. The real ones are INSIDE the high-voltage
        //    compartment, behind its interlocked door; ours has no door, so they stand on a
        //    sheet panel of the cage's last bay and are locked as the door would be (wire_simulation). Pulled, that pair of
        //    motors is out of the circuit and the other pair works alone.
        //  - the INTERLOCK COCK on the cab's back wall between the passage door and the
        //    handbrake, where the reference has its dial "0 / P": up, at P, the pantographs may
        //    be raised; down, at 0, they come down and the isolators are free.
        Transform knife_wall = node(root.transform, "ConverterKnifeBoard", new Vector3(0.45f, floor_top + 1.35f, 3.646f)).transform;
        make_knife(knife_wall, "ConverterKnife", Vector2.zero, true, 0.100f, new[] { 0.07f, 0.0f, -0.07f }, 55.0f, false, false,
            "converterKnife.EXT_IN", "eu07/converter_knife", new Vector3(0.05f, -0.125f, -0.002f));
        // THE CABINET IS SHUT NOW ("lets get the LV racks in an enclosed cabinet so the player doesnt have to see no
        // function", the author, 2026-10-12): two sheet leaves across its front, the model's. The one toward the
        // cab is this one, and opens - the knife switch is behind it. From 3.28 to 3.67 along the body, 0.88
        // to 1.78 over the floor, on the cabinet's face at the passage's edge; hinged at the cab's end.
        // (to the POST at the cab's end, not over it: the small compressor's tumbler is on that post, and stood
        // through the leaf - "the small compressor rocker is phasing through the door", the author, 2026-10-12.
        // And it swings 92 degrees, not 105: further, it would lie against the tumbler.)
        make_sheet_door(root.transform, "LvDoor", new Vector3(0.36f - 0.0005f, floor_top + 1.33f, 3.4575f), Quaternion.LookRotation(Vector3.right, Vector3.up),
            0.343f, 0.882f, "lvDoor.EXT_IN", "eu07/lv_door", false, 92.0f);
        // IN A BOX IN THE CAGE, BEHIND A DOOR. (They hung on a plate on the expanded metal, and
        // floated; then stood on a sheet panel in the cage's last bay, in the open with a red bar
        // across them that came and went with the cock: "they suddenly spawn when i flick the
        // isolation in the back ... shouldnt the motor isolation be in a cabinet instead of
        // sticking out in the open?", the author, 2026-10-12.) The model lets a box into the
        // cage behind an opening in that panel (eu07_blockout.py, ISO_*: 40 by 36 cm, 21 deep -
        // a pulled handle stands 18 cm off the back); the switches stand on its back.
        const float passage = 0.36f, box_deep = 0.21f, box_wide = 0.40f, box_high = 0.36f, box_middle = 4.6825f;
        Transform cage_plate = node(root.transform, "MotorIsolators", new Vector3(-passage - box_deep + 0.0005f, floor_top + 1.30f, box_middle)).transform;
        cage_plate.localRotation = Quaternion.LookRotation(Vector3.left, Vector3.up);            // out of it is into the passage; its x is toward the cab
        foreach (float base_x in new[] { -0.10f, 0.10f })
            solid(cage_plate, "IsolatorBase", PrimitiveType.Cube, bakelite, new Vector3(base_x, 0.01f, 0.0f), new Vector3(0.115f, 0.230f, 0.008f), Quaternion.identity, false);
        make_knife(cage_plate, "MotorIsolator12", new Vector2(-0.10f, -0.06f), false, 0.140f, new[] { -0.025f, 0.025f }, 60.0f, true, true,
            "motorIsolator12.EXT_IN", "eu07/isolator_12", new Vector3(0.0f, -0.050f, -0.002f));
        make_knife(cage_plate, "MotorIsolator34", new Vector2(0.10f, -0.06f), false, 0.140f, new[] { -0.025f, 0.025f }, 60.0f, true, true,
            "motorIsolator34.EXT_IN", "eu07/isolator_34", new Vector3(0.0f, -0.050f, -0.002f));
        make_isolator_door(root.transform, new Vector3(-passage + 0.001f, floor_top + 1.30f, box_middle), cage_plate.localRotation, box_wide, box_high);
        Transform back_wall = node(root.transform, "HvInterlockDial", new Vector3(0.62f, floor_top + 1.55f, cab_back + 0.0005f)).transform;
        back_wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);              // out of it is into the cab
        make_cock(back_wall, "HvInterlock", Vector2.zero, "hvInterlock.EXT_IN", "eu07/hv_interlock", 0.075f);
        // THE VIGILANCE DEVICE'S BRAKE CUT-OFF ("vigilancy now calls for another valve. a Czuwak brake cutoff valve",
        // the author, 2026-10-13): a cock before the valve by which the device empties the brake pipe, low on the
        // same wall between the two switches' conduits, 0.70 m over the floor. Lever up, open - the device can
        // brake; down, shut - it can only light its lamp and buzz (mod/eu07_driver.cs, watch_vigilance).
        Transform cock_wall = node(root.transform, "VigilanceCockWall", new Vector3(0.635f, floor_top + 0.70f, cab_back + 0.0005f)).transform;
        cock_wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);              // out of it is into the cab
        make_cock(cock_wall, "VigilanceCock", Vector2.zero, "vigilanceCock.EXT_IN", "eu07/cock_vigilance");
        // TWO GUARDED SWITCHES on the cab's back wall behind the driver, between the passage door and the
        // handbrake's wheel, 1.06 m over the floor - where the reference cab has its pair. Each a tumbler
        // in a black box with a conduit down to the floor, and NOT TO BE TAKEN FOR A LIGHT SWITCH ("I just
        // wanna be sure that yhe player doesnt mistakenly isolate the loco thinking its a light switch",
        // the author, 2026-10-12): a coloured lever between two steel guards that stand prouder than
        // it, and over it a yellow plate that says what it is.
        //   JAZDA CIAGNIONA ("Right. Make it"), red. Down, as it stands, train running; up, HAULED:
        //   the wheel closes nothing (eu07_power_circuit.cs).
        //   CAB CUT OUT ("we just put a cab cutout option, which ignores desk I/O and lets the leader do
        //   the I/O"), teal. Up, this unit is a FOLLOWER in a multiple unit: its own desk is held, it
        //   takes every shared control from the leader, and the leader's desk shows its lamps
        //   (mod/eu07_driver.cs, multiple_unit_step).
        void wall_switch(string name, float x, string port, string label, string notice, Material lever)
        {
            Transform wall = node(root.transform, name + "Switch", new Vector3(x, floor_top + 1.06f, cab_back + 0.0005f)).transform;
            wall.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);               // out of it is into the cab
            const float box_out = 0.030f;
            solid(wall, "Box", PrimitiveType.Cube, bakelite, new Vector3(0.0f, -0.014f, -box_out * 0.5f), new Vector3(0.078f, 0.118f, box_out), Quaternion.identity, false);
            solid(wall, "Conduit", PrimitiveType.Cylinder, bakelite, new Vector3(0.0f, -0.555f, -0.011f), new Vector3(0.016f, 0.482f, 0.016f), Quaternion.identity, false);      // down the wall to the floor
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
        frame_board.localRotation = Quaternion.LookRotation(Vector3.back, Vector3.up);         // out of it is toward cab A
        make_gauge(frame_board, "PantographAir", Vector2.zero, 0.0f, 10.0f, "port:eu07.PANTO_AIR", 1.0f, "eu07/panto_air", 135.0f, 0.3f, 0.066f, face: "bar10");
        // THE TWO GAUGES UNDER THE COCKS ("there are two pressure gauges here", the author, 2026-10-12: two blank
        // white discs of the model's, hung on their thin pipes from the lower run). They read what their
        // pipes are on: the left one, between the control-air cock and the drain, the pantographs' and
        // control circuit; the right one, between the drain and the main reservoir's cock, the main
        // reservoir. Each a face and a pointer standing a millimetre proud of the model's disc.
        // (On this board the control-air cock is at x 1.12 and the main reservoir's at 0.74: the first build had the two gauges the wrong way round.)
        foreach ((string name, float x, float low, float high, string reader, string face, string label) in new[]
                 { ("ControlAirGauge", 1.03f, 0.0f, 10.0f, "port:eu07.PANTO_AIR", "bar10", "eu07/cock_control_air"), ("MainReservoirGauge", 0.84f, 1.0f, 17.0f, "reservoir", "bar16", "car/main_res") })
        {
            Transform hung = node(root.transform, name + "Board", new Vector3(x, floor_top + 0.47f, 1.55f + 0.051f)).transform;
            hung.localRotation = frame_board.localRotation;
            make_gauge(hung, name, Vector2.zero, low, high, reader, 1.0f, label, 135.0f, 0.3f, 0.092f, face: face);
        }
        // THE THREE-WAY COCK ("yeah do the three way cock", the author, 2026-10-11): under the
        // gauge on the same frame, between the two rows of valves. The reference's is a lever
        // 14 cm long low on its frame. Ours was 11 and swung 35 degrees: "cock small - you can
        // see it clipping" - up, its grip stood in the pipe that runs under the upper row of
        // valves. What is free there, off the board's face, is a band 12 cm high between the
        // lower valves' handles (0.845 over the floor) and that pipe (0.965): so 8.5 cm of
        // lever, 28 degrees either way, about 0.905 - the whole swing is 0.853 to 0.957.
        // And along the frame it stands under the gauge, 8 cm clear of where the main
        // compressor's delivery pipe comes to the frame - it stood right under that, hidden by it.
        // Built lying toward the passage; DOWN is the main reservoir (0, as it spawns), UP the
        // small compressor.
        make_cock(frame_board, "PantographCock", new Vector2(-0.04f, -0.340f), "pantoCock.EXT_IN", "eu07/panto_cock");
        // THE FRAME'S LABELLED COCKS ("Since the handles are titled, we can make em functional",
        // the author, 2026-10-12). The model has their bodies in the pipe runs (at this end without
        // handles or plates); here are the handles, shoved a quarter turn - along the pipe open,
        // down across it shut - and the game's own captions where the plates were. The pipes'
        // axis stands 34 mm off the board, a handle's plane 40 mm further.
        // What each does is the mod's (pantograph_air, and the main compressor's switch).
        make_line_cock(frame_board, "PantographAirCock", new Vector2(-0.58f, -0.225f), 0.074f, "pantoAirCock.EXT_IN", "eu07/cock_panto_air");
        make_line_cock(frame_board, "MainReservoirCock", new Vector2(-0.18f, -0.545f), 0.074f, "mainResCock.EXT_IN", "eu07/cock_main_res");
        make_line_cock(frame_board, "DrainCock", new Vector2(-0.37f, -0.545f), 0.074f, "drainCock.EXT_IN", "eu07/cock_drain");
        make_line_cock(frame_board, "ControlAirCock", new Vector2(-0.56f, -0.545f), 0.074f, "controlAirCock.EXT_IN", "eu07/cock_control_air");
        // The room's lamps: a round black box with a tumbler on it, on the room's face of each
        // cab's back wall beside the passage door's free edge, 1.13 m over the floor. Two-way:
        // either changes them (the mod reads both).
        foreach ((string cab, float end) in new[] { ("A", 1.0f), ("B", -1.0f) })
        {
            Transform wall = node(root.transform, "RoomLightSwitch" + cab, new Vector3(end * 0.50f, floor_top + 1.13f, end * (cab_back - 0.030f))).transform;
            wall.localRotation = Quaternion.LookRotation(end * Vector3.forward, Vector3.up);   // out of it is into the room
            const float box_out = 0.024f;
            shaped(wall, "Box", turned("room_switch_box", 20, false, (0.034f, 0.0f), (0.034f, box_out - 0.004f), (0.030f, box_out), (0.0f, box_out)), bakelite, Vector3.zero);
            GameObject lamp_switch = make_toggle(wall, "RoomLight" + cab, Vector2.zero, $"roomLight{cab}.EXT_IN", "eu07/room_light", null);
            lamp_switch.transform.parent.localPosition += new Vector3(0.0f, 0.0f, -box_out);
            child(lamp_switch.transform.parent, "Label").localPosition = new Vector3(0.0f, -0.052f, box_out - 0.002f);
        }
        // SANDING ("start on that": horn, sanding, wipers - the author, 2026-10-11). The real
        // desk has a pedal in the pedestal's footrest and, on the assistant's plate, a switch
        // that holds it on; the reference cab has the switch (0.695 m left of the middle,
        // 0.34 m in from the desk's edge) and no pedal, and this desk's knee room is not open.
        // It stood there for an afternoon and he could not find it: "we need that closer to
        // the desk then, since thats gonna be a must in the valley". So the switch is on the
        // DRIVER's plate, in its empty corner nearest his right hand, under the two dimming
        // switches. The railcar's own sander does the rest - the sand, what it does to the grip.
        make_toggle(top, "Sanding", new Vector2(0.375f, 0.151f), "sander.CONTROL_EXT_IN", "eu07/sanding", "SandToggle");
        // THE WIPERS' VALVES: one each side on the sill under its own pane, a flat lever on a
        // small body, where the reference cab has them (0.725 m either side of the middle,
        // 0.76 m in from the desk's edge, about a metre over the floor). Off, slow, fast. The
        // game's wiper keys work the driver's one.
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
        // THE HORN: a valve under the desk's front edge left of the pedestal, its lever toward
        // the driver's knee - in the reference 0.370 m right of the middle, 76 mm under the desk
        // top, 75 mm long. Pushed left the low tone, right the high; let go, it comes back.
        Transform front = panel(desk, "PanelFront", new Vector3(0.0f, 0.0f, desk_front), 0.0f);
        make_horn(front, new Vector2(0.370f, top_y - 0.076f));
        // The rest of his desk goes: throttle, both pantograph switches, the gauges, the box.
        UnityEngine.Object.DestroyImmediate(old.gameObject);

        // The lamp-and-button plate left of the controller, ARRANGED AS THE REFERENCE CAB'S ("have
        // a look at the desk and reorganise the lights and switches how they look in the ref", the
        // author, 2026-10-10): every place below is measured there (reference/maszyna/
        // kabina_a_parts.txt; the plate's frame is x to the driver's right, y toward the
        // windscreen from the desk's origin). From the windscreen toward the driver:
        //   the lamps' row    y 0.384   four switches at its left end (y 0.371), then eight lamps
        //   the buttons' row  y 0.305   three switches at its left end (y 0.296), then seven buttons
        //   the near lamps    y 0.230   three lamps, the two side lamps' dimming right of them
        //   the near edge     y 0.181 / 0.151   the selectors' place, and the top lamp's dimming
        // The reference's leftmost switch of each row (dimming of the SHP and vigilance lamps) and
        // its spare lamp belong to things this locomotive has not got; their places stay bare.
        const float row_lamps = 0.384f, row_buttons = 0.305f, row_near = 0.230f, row_switches = 0.371f, row_switches_near = 0.296f;
        // The lights: switches, two rows of three on the slope left of the speed recorder, where
        // the reference cab has them (measured there: the whites in the row nearer the driver
        // - left, top, right - and the two tail lamps in the row above, a gap between them where
        // the reference has window heating). The cab light's switch stands in the slope's lower
        // row further left, where the reference's is.
        // REGROUPED ("We also need to rearrange the switches so theyre concise and grouped on the main
        // part of the desk", the author, 2026-10-12). They stood in four places: a plate of their own
        // on the slope, two more switches alone on the slope's green further left, the dimming in two
        // places on the main plate, and the fault lamp with its button alone beside a meter. Now
        // EVERY switch of the desk is on the main plate, and the slope has instruments only:
        //   THE LIGHTS, a block of two rows of five at the plate's near left corner (where the real
        //   desk has its three headlight selectors): the lamps' row - left, top, right, the two
        //   tail lamps - and under each white lamp's switch its dimming, then the cab's light and
        //   the instruments';
        //   the fault lamp and its button at the right end of the near lamps' row;
        //   high start under its own lamp (LS4); sanding where it was, nearest the right hand.
        float[] light_columns = { -0.200f, -0.145f, -0.090f, -0.035f, 0.020f };
        const float row_lights = 0.215f, row_dimming = 0.135f;
        (string name, string label)[] lights_far = { ("lightLeft", "eu07/light_left"), ("lightTop", "eu07/light_top"), ("lightRight", "eu07/light_right"), ("redLeft", "eu07/red_left"), ("redRight", "eu07/red_right") };
        (string name, string label)[] lights_near = { ("lightDimLeft", "eu07/dim_left"), ("lightDimTop", "eu07/dim_top"), ("lightDimRight", "eu07/dim_right") };
        for (int index = 0; index < lights_far.Length; ++index)
            make_toggle(top, char.ToUpper(lights_far[index].name[0]) + lights_far[index].name.Substring(1) + "A", new Vector2(light_columns[index], row_lights), lights_far[index].name + "A.EXT_IN", lights_far[index].label, null);
        for (int index = 0; index < lights_near.Length; ++index)
            make_toggle(top, char.ToUpper(lights_near[index].name[0]) + lights_near[index].name.Substring(1) + "A", new Vector2(light_columns[index], row_dimming), lights_near[index].name + "A.EXT_IN", lights_near[index].label, null);
        make_toggle(top, "CabLight", new Vector2(light_columns[3], row_dimming), "cabLight.EXT_IN", "eu07/cab_light", "CabLightToggle");
        // THE LAMPS OVER THE METERS ("then instrument lighting", the author, 2026-10-12: at night
        // nothing on the slope could be read). A small hooded lamp over each meter or pair of
        // meters, shining down its face; one object, EU07InstrumentLights, that the MOD turns
        // on and off - their switch beside the cab light's, the lighting's breaker. Under the
        // game's own caption for it.
        make_toggle(top, "InstrumentLight", new Vector2(light_columns[4], row_dimming), "instrumentLight.EXT_IN", "car/dash_light", null);
        GameObject meter_lamps = node(slope, "EU07InstrumentLights", Vector3.zero);
        foreach (float lamp_x in new[] { meter_x[0], (meter_x[1] + meter_x[2]) * 0.5f, (meter_x[3] + meter_x[4]) * 0.5f, meter_x[5], 0.75f, 0.92f, 1.09f })
        {
            bool big = lamp_x > 0.7f;                       // the three brake gauges are 130 mm across, the meters 80
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
        }
        meter_lamps.SetActive(false);
        // The DIMMING is on the plate as there, a switch a lamp: the top lamp's at the near edge,
        // the left and right lamps' as a pair beside the near lamps. The reference's pair stands
        // 29 mm apart under ONE caption; here each has a caption of its own, which wants 60 mm,
        // so the pair starts 15 mm further right and is 60 mm apart.
        // (the dimming is in the lights' block now, each under its lamp's switch)
        // Cab B has no desk yet, but its end's lamps must be switched from somewhere: the same six
        // switches on a small sloped box where its desk's will be.
        Transform desk_b = node(root.transform, "DeskB", new Vector3(0.0f, floor_top, -desk_z)).transform;
        desk_b.localRotation = Quaternion.Euler(0.0f, 180.0f, 0.0f);
        Transform slope_b = panel(desk_b, "PanelSlope", new Vector3(0.0f, top_y + 0.085f, slope_z), 45.0f);
        solid(slope_b, "Face", PrimitiveType.Cube, desk_green, new Vector3(0.12f, 0.0f, 0.06f), new Vector3(0.52f, 0.24f, 0.12f), Quaternion.identity, false);
        solid(desk_b, "Stand", PrimitiveType.Cube, desk_green, new Vector3(0.12f, (top_y + 0.02f) * 0.5f, slope_z + 0.05f), new Vector3(0.52f, top_y + 0.02f, 0.16f), Quaternion.identity, false);
        light_switches(slope_b, "B", 0.013f, new Vector2(0.21f, -0.055f));
        Color red = new Color(0.25f, 0.03f, 0.03f), green = new Color(0.03f, 0.20f, 0.05f), blue = new Color(0.04f, 0.06f, 0.25f),
              orange = new Color(0.30f, 0.12f, 0.02f), clear = new Color(0.5f, 0.5f, 0.5f);
        // The lamps' row: two, a gap, the main breaker's alone, a gap, five.
        make_lamp(top, "LampLSC" , new Vector2(0.031f, row_lamps), red   , "eu07.LAMP_LSC" , "eu07/lamp_lsc");
        make_lamp(top, "LampLS6" , new Vector2(0.090f, row_lamps), red   , "eu07.LAMP_LS6" , "eu07/lamp_ls6");
        make_lamp(top, "LampLSS" , new Vector2(0.177f, row_lamps), green , "eu07.LAMP_LSS" , "eu07/lamp_lss");
        make_lamp(top, "LampLS8" , new Vector2(0.265f, row_lamps), red   , "eu07.LAMP_LS8" , "eu07/lamp_ls8");
        make_lamp(top, "LampLS7" , new Vector2(0.325f, row_lamps), red   , "eu07.LAMP_LS7" , "eu07/lamp_ls7");
        make_lamp(top, "LampLSPO", new Vector2(0.383f, row_lamps), red   , "eu07.LAMP_LSPO", "eu07/lamp_lspo");
        make_lamp(top, "LampLS10", new Vector2(0.440f, row_lamps), blue  , "eu07.LAMP_LS10", "eu07/lamp_ls10");
        make_lamp(top, "LampLS9" , new Vector2(0.497f, row_lamps), clear , "eu07.LAMP_LS9" , "eu07/lamp_ls9");
        // The buttons' row, each under or beside the lamp it serves. The compressors' reset is a
        // button of its own on the real desk; here it feeds the same control as the converter's:
        // one reset does every auxiliary. Main breaker off and slip are the mushroom kind, as there.
        make_button(top, "CompressorReset"  , new Vector2(0.061f, row_buttons), green_cap, "auxRelayReset.EXT_IN"    , "eu07/comp_reset"         , null);
        make_button(top, "FastBreakerOff"   , new Vector2(0.145f, row_buttons), red_cap  , "fastBreakerOff.EXT_IN"   , "eu07/ws_off"             , "FuelCutoff", true);
        make_button(top, "FastBreakerOn"    , new Vector2(0.205f, row_buttons), green_cap, "fastBreakerOn.EXT_IN"    , "eu07/ws_on"              , "Starter");
        make_button(top, "RelayReset"       , new Vector2(0.289f, row_buttons), green_cap, "relayReset.EXT_IN"       , "eu07/relay_reset"        , "StarterFuse");
        make_button(top, "AuxRelayReset"    , new Vector2(0.376f, row_buttons), green_cap, "auxRelayReset.EXT_IN"    , "eu07/aux_reset"          , null);
        make_button(top, "LineContactorsOff", new Vector2(0.433f, row_buttons), red_cap  , "lineContactorsOff.EXT_IN", "eu07/line_contactors_off", null);
        make_button(top, "WheelSlip"        , new Vector2(0.492f, row_buttons), green_cap, "wheelSlip.EXT_IN"        , "eu07/slip_button"        , null, true);
        // The near lamps. High start, a rotary selector on the real desk, is a toggle at the
        // selector's place.
        make_lamp(top, "LampLS4" , new Vector2(0.136f, row_near), red   , "eu07.LAMP_LS4" , "eu07/lamp_ls4");
        make_lamp(top, "LampLS5" , new Vector2(0.194f, row_near), orange, "eu07.LAMP_LS5" , "eu07/lamp_ls5");
        make_lamp(top, "LampLSGP", new Vector2(0.253f, row_near), blue  , "eu07.LAMP_LSGP", "eu07/lamp_lsgp");
        make_toggle(top, "HighStart", new Vector2(0.136f, 0.151f), "highStart.EXT_IN", "eu07/high_start", null);       // under its lamp, LS4
        // THE LAST-MILE BATTERY ("we need a last mile battery", the author, 2026-10-11): not on
        // the real desk, so it takes the places the real one leaves empty - a lamp at the left
        // end of the near lamps, lit while the traction circuit is on the battery; its switch
        // in the gap of the near switches' row, beside the train heating's; and a meter of what
        // is left in it on the slope, left of the HV meters.
        make_lamp(top, "LampBattery", new Vector2(0.078f, row_near), new Color(0.26f, 0.21f, 0.02f), "eu07.LAMP_BATTERY", "eu07/lamp_battery");
        // The switches at the rows' left ends, 50 mm apart. The two pantograph switches stand
        // one behind the other: the FRONT pantograph's in the row nearer the window, the REAR
        // one's in the row nearer the driver. pantograph2Switch drives [Pantograph2], at cab A's
        // end: the front from this desk.
        make_toggle(top, "PantographFront", new Vector2(-0.124f, row_switches     ), "pantograph2Ask.EXT_IN"   , "eu07/panto_front", "Pantograph");
        make_toggle(top, "Compressors"    , new Vector2(-0.074f, row_switches     ), "compressorSwitch.EXT_IN" , "eu07/compressors", null);
        make_toggle(top, "Converter"      , new Vector2(-0.023f, row_switches     ), "converterSwitch.EXT_IN"  , "eu07/converter"  , null);
        make_toggle(top, "PantographRear" , new Vector2(-0.124f, row_switches_near), "pantographAsk.EXT_IN"    , "eu07/panto_rear" , null);
        make_toggle(top, "TrainHeating"   , new Vector2(-0.073f, row_switches_near), "heatingSwitch.EXT_IN"    , "eu07/heating"    , null);
        make_toggle(top, "Battery"        , new Vector2(-0.023f, row_switches_near), "batterySwitch.EXT_IN"    , "eu07/battery"    , null);

        // The slope: the HV meters ahead of the panel, the three brake gauges ahead of the
        // controller. Round needle dials stand in for the real edgewise meters. The
        // speedometer is on the slope too, though the real one stands on a column at the
        // window pillar.
        // The speedometer stands on a column of its own at the window pillar, facing the driver.
        // It is a speed RECORDER: a teal box on a round stalk, the dial filling its upper part,
        // over the dial the two swing bolts that hold its cover shut with a black knob between
        // them, a cable out of its underside ("we need a pass on the desk details, especially
        // the hassler", the author, 2026-10-10). The two bolts were LAMPS for a day, and burned
        // with the cab light: "those are not lights. those are screw[s]" (the same, the day
        // after, with two photographs of the instrument). The face is a drawing of ours:
        // tools/dial_faces.py.
        // Its CLOCK goes and its COUNTER counts ("wanna do the Km counter and inbuilt clock
        // next? also we're gonna need a details pass, on thee geometry", the author,
        // 2026-10-11): two hands on the small dial that follow the game's time, and six drums
        // in the window under the centre, the lowest - red on white - for hundreds of metres.
        // The box, after his two photographs: taller than wide, its edges broken, standing on
        // the drum of its drive with the cable beside it; under the dial the black bar of its
        // latch; the knob over the dial on a flange with two screws.
        const float dial = 0.14f, dial_radius = dial * 0.5f;
        solid(desk, "RecorderDrive", PrimitiveType.Cylinder, teal, new Vector3(0.29f, top_y + 0.1675f, 0.80f), new Vector3(0.110f, 0.0475f, 0.110f), Quaternion.identity, false);
        solid(desk, "RecorderDriveFoot", PrimitiveType.Cylinder, black, new Vector3(0.29f, top_y + 0.127f, 0.80f), new Vector3(0.118f, 0.007f, 0.118f), Quaternion.identity, false);
        solid(desk, "RecorderGland", PrimitiveType.Cylinder, black, new Vector3(0.362f, top_y + 0.205f, 0.83f), new Vector3(0.022f, 0.012f, 0.022f), Quaternion.identity, false);
        solid(desk, "RecorderCable", PrimitiveType.Cylinder, black, new Vector3(0.362f, top_y + 0.160f, 0.83f), new Vector3(0.014f, 0.040f, 0.014f), Quaternion.identity, false);
        Transform column = panel(desk, "PanelColumn", new Vector3(0.29f, top_y + 0.40f, 0.74f), 0.0f);
        shaped(column, "RecorderBox", slab("recorder_box", (0.0f, 0.0f, 0.0f, -0.120f), (0.094f, 0.144f, 0.012f, -0.120f), (0.100f, 0.150f, 0.016f, -0.112f),
            (0.100f, 0.150f, 0.016f, -0.008f), (0.094f, 0.144f, 0.012f, 0.0f), (0.0f, 0.0f, 0.0f, 0.0f)), teal, new Vector3(0.0f, -0.035f, 0.0f));
        make_gauge(column, "Speedometer", Vector2.zero, 0.0f, 150.0f, "port:traction.WHEEL_SPEED_KMH_EXT_IN", 1.0f, null, 135.0f, 0.3f, dial, 7, "speed");
        make_label(column, new Vector3(0.0f, -0.150f, -0.004f), "car/speedometer");                 // where the maker's plate is
        recorder_bolt(column, "RecorderBoltLeft", new Vector2(-0.070f, 0.093f), 30.0f);
        recorder_bolt(column, "RecorderBoltRight", new Vector2(0.070f, 0.093f), -30.0f);
        solid(column, "RecorderKnobFlange", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.097f, -0.002f), new Vector3(0.030f, 0.002f, 0.030f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderKnob", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.097f, -0.009f), new Vector3(0.016f, 0.007f, 0.016f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        foreach (float side in new[] { -0.0115f, 0.0115f })
            solid(column, "RecorderKnobScrew", PrimitiveType.Cylinder, chrome, new Vector3(side, 0.097f, -0.0045f), new Vector3(0.0045f, 0.0006f, 0.0045f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderLatch", PrimitiveType.Cube, black, new Vector3(0.0f, -0.100f, -0.004f), new Vector3(0.105f, 0.018f, 0.008f), Quaternion.identity, false);
        solid(column, "RecorderLatchPivot", PrimitiveType.Cylinder, black, new Vector3(-0.040f, -0.100f, -0.009f), new Vector3(0.012f, 0.002f, 0.012f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(column, "RecorderKeyhole", PrimitiveType.Cylinder, chrome, new Vector3(0.036f, -0.100f, -0.0085f), new Vector3(0.009f, 0.0006f, 0.009f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        // the clock's two hands, over the face and under the pointer
        Vector2 clock = new Vector2(0.0f, recorder_clock_up * dial_radius);
        recorder_hand(column, "RecorderClockHours", clock, recorder_clock_radius * dial_radius * 0.52f, 0.0016f, 12.0f, "eu07.CLOCK_HOURS", -0.0070f);
        recorder_hand(column, "RecorderClockMinutes", clock, recorder_clock_radius * dial_radius * 0.84f, 0.0011f, 60.0f, "eu07.CLOCK_MINUTES", -0.0078f);
        solid(column, "RecorderClockBoss", PrimitiveType.Cylinder, black, new Vector3(clock.x, clock.y, -0.0084f), new Vector3(0.0028f, 0.0003f, 0.0028f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        // the counter: six drums standing a millimetre and a half through the face, in a black frame
        float window_wide = recorder_counter_wide * dial_radius, window_high = recorder_counter_high * dial_radius;
        float drum_wide = window_wide / 6.0f, drum_radius = window_high * 10.0f / (2.0f * Mathf.PI), counter_y = -recorder_counter_down * dial_radius;
        for (int drum = 0; drum < 6; ++drum)                                                         // drum 0 is the lowest and stands at the right
            recorder_drum(column, "RecorderDrum" + drum, new Vector3((2.5f - drum) * drum_wide, counter_y, -0.0075f + drum_radius), drum_wide - 0.0003f, drum_radius,
                "eu07.ODOMETER_" + drum, drum == 0 ? "odometer_red" : "odometer");
        foreach (float side in new[] { -1.0f, 1.0f })
        {
            solid(column, "RecorderCounterFrame", PrimitiveType.Cube, black, new Vector3(0.0f, counter_y + side * (window_high * 0.5f + 0.0011f), -0.0073f),
                new Vector3(window_wide + 0.004f, 0.0022f, 0.003f), Quaternion.identity, false);
            solid(column, "RecorderCounterFrame", PrimitiveType.Cube, black, new Vector3(side * (window_wide * 0.5f + 0.001f), counter_y, -0.0073f),
                new Vector3(0.002f, window_high + 0.0044f, 0.003f), Quaternion.identity, false);
        }
        // The real HV meters are edgewise instruments in black surrounds; round dials stand in
        // The real HV meters are edgewise instruments in black surrounds; round dials stand in
        // for them, on a black bank.
        // FIVE meters in the bank, as the reference cab's has, at its places (from the middle:
        // 0.195, 0.291, 0.404, 0.499, 0.590 - its parts are named for battery volts, the two
        // motor currents and the traction volts; the fifth has no name). The battery's
        // voltmeter was missing: with the panel on the screen gone it is the only thing that
        // says the 110 V is there and whether the converter charges it (the documentation:
        // low-voltage voltmeters and ammeters on the desks). The nameless fifth place keeps
        // the last-mile battery's charge.
        // (Those places were the reference cab's, for ITS meters. Ours stood 91 to 96 mm apart with rims 97 mm
        // across, each rim lying over the next: "the gauges need a bit of separation. parts of the borders are
        // overalying each others borders", the author, 2026-10-13. Now `meter_x`: 80 mm dials, rims 92 mm
        // across and 99 mm apart, the three of the low voltage and the three of the high a little further.)
        solid(slope, "MeterBank", PrimitiveType.Cube, black, new Vector3(0.3985f, 0.0f, -0.0005f), new Vector3(0.512f, 0.16f, 0.003f), Quaternion.identity, false);
        make_gauge(slope, "VoltmeterNN", new Vector2(meter_x[2], 0.0f), 0.0f, 150.0f, "port:eu07.LOW_VOLTS", 1.0f, "eu07/low_voltmeter", 135.0f, 0.3f, meter_dial, face: "volts150");
        make_gauge(slope, "AmmeterWN1", new Vector2(meter_x[3], 0.0f), 0.0f, 1200.0f, "port:eu07.AMMETER_12", ammeter_scale, "eu07/ammeter_1", 135.0f, 0.3f, meter_dial, face: "amps");
        make_gauge(slope, "AmmeterWN2", new Vector2(meter_x[4], 0.0f), 0.0f, 1200.0f, "port:eu07.AMMETER_34", ammeter_scale, "eu07/ammeter_2", 135.0f, 0.3f, meter_dial, face: "amps");
        make_gauge(slope, "VoltmeterWN", new Vector2(meter_x[5], 0.0f), 0.0f, 2000.0f, "port:busBar.SUPPLY_VOLTAGE", voltmeter_scale, "eu07/voltmeter", 135.0f, 0.3f, meter_dial, face: "kilovolts");
        // the last-mile battery's meter, in a surround of its own
        make_gauge(slope, "BatteryCharge", new Vector2(meter_x[1], 0.0f), 0.0f, 100.0f, "port:eu07.BATTERY_CHARGE", 1.0f, "eu07/battery_charge", 135.0f, 0.3f, meter_dial, face: "charge");
        // THE GAME'S OWN TWO ("we also need a general fault indicator and a motor temp indicator,
        // so we follow the Dv standard", the author, 2026-10-11): what every locomotive of the
        // game's shows its driver and this one only showed on a panel on the screen. On the
        // slope left of the lights' plate, where the real desk has nothing: the traction
        // motors' temperature, off the game's own figure for it and under the game's own
        // caption, red from 120 where the game throws their breaker; and one red lamp that
        // says what the game's own two motor lamps say (mod/eu07_driver.cs: blinking for the
        // motors' breaker out or 105 C, steady for a dead motor or 90 C), with the sounds the
        // game's own DE6 gives those lamps: its buzz as the lamp comes on or gets worse, its
        // continuous buzz while it blinks.
        solid(slope, "MeterBankMotors", PrimitiveType.Cube, black, new Vector3(-0.195f, 0.0f, -0.0005f), new Vector3(0.105f, 0.16f, 0.003f), Quaternion.identity, false);
        make_gauge(slope, "MotorTemperature", new Vector2(meter_x[0], 0.0f), 0.0f, 150.0f, "port:tmHeat.TEMPERATURE", 1.0f, "car/tm_temp", 135.0f, 0.3f, meter_dial, face: "celsius150");
        make_lamp(top, "LampFault", new Vector2(0.320f, row_near), red, "eu07.LAMP_FAULT", "eu07/lamp_fault");
        sounds(child(top, "LampFault").GetComponent<LampControlProxy>(), ("warningAudio", "DashWarning_02_Buzz"), ("blinkStateBuzzingLoopAudio", "DashWarning_07_BuzzContinuous"));
        // And under it a button that says "seen it" ("add an acknowledge fault button so that
        // bloody buzzing gets killed off", the author, 2026-10-12): the lamp goes from blinking
        // to steady and the buzz stops, until something NEW goes wrong (mod/eu07_driver.cs).
        make_button(top, "FaultAcknowledge", new Vector2(0.385f, row_near), red_cap, "faultAck.EXT_IN", "eu07/fault_ack", null);
        // THE VIGILANCE BUTTON, where the real desk has it: on the green, in the corner cut out of the lamps' plate by
        // the driver's left hand. It answers the game's own vigilance device mounted in the instrument cupboard
        // ("we can answer the box's vigillance with our click", the author, 2026-10-13: mod/eu07_driver.cs), and the
        // lamp over it blinks, with the game's buzz, while that device asks. No device aboard: a dead button.
        make_button(top, "Vigilance", new Vector2(0.495f, 0.150f), yellow_cap, "vigilance.EXT_IN", "eu07/vigilance", null);
        // (Its lamp stood over it for an hour, a round one like the desk's others. "what we need is the Czuwak SHP
        // box modelled. have a look at the ref", the author, 2026-10-13: the lamps of the vigilance device and of
        // the train stop are not on the desk at all, they are in a box of their own on the sill - make_shp_box.)
        make_shp_box(desk, new Vector3(0.497f, top_y + 0.12f, 0.772f), blue, red);      // ("give it the blue color not green", the author)
        // the six meters stand in a row on one black plate, as the real desk's stand in black housings
        solid(slope, "PlateMeters", PrimitiveType.Cube, bakelite, new Vector3(0.351f, 0.0f, -0.0005f), new Vector3(0.612f, 0.205f, 0.003f), Quaternion.identity, false);
        // The game's pressures count the atmosphere (1.0 is empty), so 1..11 reads 0..10 bar.
        make_gauge(slope, "BrakeCylinder", new Vector2(0.75f, 0.0f), 1.0f, 11.0f, "cylinder", 1.0f, "car/brake_cyl", 135.0f, 0.3f, 0.13f, face: "bar10");
        make_gauge(slope, "BrakePipe"    , new Vector2(0.92f, 0.0f), 1.0f, 11.0f, "pipe", 1.0f, "car/brake_pipe", 135.0f, 0.3f, 0.13f, face: "bar10");
        make_gauge(slope, "MainReservoir", new Vector2(1.09f, 0.0f), 1.0f, 17.0f, "reservoir", 1.0f, "car/main_res", 135.0f, 0.3f, 0.13f, face: "bar16");

        foreach (float gauge_x in new[] { 0.75f, 0.92f, 1.09f })           // the brake gauges stand in round green flanges
            solid(slope, "GaugeFlange", PrimitiveType.Cylinder, valve_green, new Vector3(gauge_x, 0.0f, -0.0015f), new Vector3(0.158f, 0.001f, 0.158f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);

        // The controller: the 44-position wheel on the pedestal, and round its column a small
        // graduated ring whose pointer shows where the contactors are (which may lag the wheel).
        make_controller(desk, placed("Wheel", new Vector3(ped_x, hub_y, ped_z)), "throttle.EXT_IN");
        make_gauge(pedestal, "Position", Vector2.zero, 0.0f, 43.0f, "port:eu07.CONTROLLER_POSITION", 1.0f, null,
            controller_sweep, 0.1f, 0.136f, eu07_positions, "positions", false);
        // The field lever: the reverser's twin on the other side, a thin flat bar on a boss
        // ahead of the hub and to its LEFT, over a small sector plate. At 0 it points ahead and
        // 29 degrees left, its tip beyond the rim; each of the six steps brings it 15 degrees
        // round toward the driver, under the wheel.
        Vector3 field_at = placed("FieldLever", new Vector3(ped_x - 0.061f, ped_top + 0.040f, ped_z + 0.128f));
        desk_part(desk, "FieldSector", placed("FieldSector", new Vector3(field_at.x, ped_top, field_at.z)));
        desk_part(desk, "Boss", new Vector3(field_at.x, ped_top, field_at.z));
        GameObject field = make_lever(desk, "FieldLever", field_at,
            new Vector3(-0.085f, 0.012f, 0.0f), new Vector3(0.13f, 0.035f, 0.026f), new Vector3(-0.14f, 0.025f, 0.0f),
            field_lever_axis, 0.0f, 90.0f, 7, "fieldLever.EXT_IN", "DynamicBrakeIncremental", "DynamicBrakeAbsolute", null, null, 0.0f, true);
        desk_part(field.transform, "FieldLever", Vector3.zero);
        field.transform.parent.localRotation = Quaternion.Euler(0.0f, 61.0f, 0.0f);          // built pointing left; turned to ahead-left
        // Captions along the plate's near edge, ahead of the rim as the driver sees it: field
        // lever, controller, reverser.
        make_label(pedestal, new Vector3(-0.11f, -0.150f, -0.003f), "eu07/field_lever");
        make_label(pedestal, new Vector3(0.0f, -0.150f, -0.003f), "eu07/controller");
        make_label(pedestal, new Vector3(0.11f, -0.150f, -0.003f), "car/reverser");

        // THE OUTLINE of what the hand is on. The game outlines the renderers a HighlightTag on
        // the control lists, and without one only the FIRST renderer it finds under the control
        // (DV.Interaction.GrabberHoverHighlight) - so of the horn's lever only the stem lit up,
        // and of a door one of its materials ("the lever is not full highlighted as selected,
        // nor are the doors", the author, 2026-10-11). Every control made of more than one
        // piece now lists them all.
        foreach (ControlSpecProxy control in root.GetComponentsInChildren<ControlSpecProxy>(true))
        {
            Renderer[] pieces = control.GetComponentsInChildren<MeshRenderer>(true);
            if (pieces.Length > 1 && control.GetComponentInChildren<HighlightTagProxy>(true) == null)
                control.gameObject.AddComponent<HighlightTagProxy>().renderers = pieces.ToList();
        }
        // THE DESK'S SURFACES, PAINTED ("The desk tex still looks off, have a look through the stock tex
        // anf ref tex", the author, 2026-10-12; see skin and paint_skins).
        Color green_paint = desk_green.color, grey_paint = new Color(0.66f, 0.67f, 0.65f);
        skins.Clear();
        skin(top, "SkinTop", new Rect(cab_x0, cab_z0, cab_x1 - cab_x0, cab_z1 - cab_z0), 0.0004f, green_paint, 0.42f, false,
            new Rect(cab_x0, cab_z0, well_x0 - cab_x0, cab_z1 - cab_z0), new Rect(well_x1, cab_z0, cab_x1 - well_x1, cab_z1 - cab_z0),
            new Rect(well_x0, cab_z0, well_x1 - well_x0, well_z0 - cab_z0), new Rect(well_x0, well_z1, well_x1 - well_x0, cab_z1 - well_z1));
        skin(top, "SkinPlate", new Rect(-0.231f, 0.070f, 0.786f, 0.438f), 0.0014f, grey_paint, 0.34f, true,
            new Rect(-0.231f, 0.244f, 0.786f, 0.264f), new Rect(-0.231f, 0.070f, 0.665f, 0.174f));
        skin(slope, "SkinSlope", new Rect(-slope_wall, -0.12f, 2.0f * slope_wall, 0.24f), 0.0004f, green_paint, 0.42f, false);
        // (The front's paint was one sheet from the floor to the top, and with the cupboards hollowed out behind it
        // it still was: the doors swung open on a green wall with their own outlines drawn on it - the author's
        // picture, 2026-10-13, "close. far". It is the carcass's face only now: a rail below the doors, one above,
        // and the stiles between.)
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
        skin(front, "SkinFront", new Rect(cab_x0, 0.0f, cab_x1 - cab_x0, top_y), 0.0004f, green_paint, 0.42f, false, front_pieces.ToArray());
        skin(top, "SkinStandTop", new Rect(0.975f, desk_front, stand_wall - 0.975f, 0.48f - desk_front), 0.0004f, green_paint, 0.42f, false);
        skin(front, "SkinStandFront", new Rect(0.975f, 0.0f, stand_wall - 0.975f, top_y), 0.0004f, green_paint, 0.42f, false);
        paint_skins(root.transform);
        lay_finish(root.transform);
    }

    // THE DESK'S SURFACES ARE PAINTED FROM ITS OWN LAYOUT. Looked at first: the reference's desk
    // (its main plate is light grey PAINT, chipped at the edges and grimy round every control;
    // its green chipped and bolted; nothing on it a clean flat colour) and the game's own cab
    // pictures (enamel chipped to dark metal, shade in every corner and round every fitting).
    // Ours was clean enamel and clean aluminium with nothing under anything.
    // A SKIN is a flat piece (or several, sharing one picture) lying 0.4 mm over a surface of the
    // desk, in a panel's own plane. When the desk is built, paint_skins looks for everything that
    // stands on each skin - any part within reach of its plane - and paints its picture round them:
    //   the paint, not quite one tone; chipped along the surface's edges, and here and there;
    //   a dark soft ring of grime and shade round every thing that stands on it;
    //   on a plate, a screw in each corner.
    // So the paint follows the layout: move a switch, and its ring moves with it.
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
            // ONE way round, the way that faces out of the panel, and its tangents said: wound both ways (as the
            // plates' flat pieces are) a surface that leans has two opposite answers for which way is along it,
            // they cancel, and it is lit as nothing - the first skins were nearly black.
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.tangents = Enumerable.Repeat(new Vector4(1.0f, 0.0f, 0.0f, -1.0f), 4).ToArray();
            mesh.RecalculateBounds();
            GameObject made = node(panel, $"{name}_{index}", Vector3.zero);
            made.AddComponent<MeshFilter>().sharedMesh = mesh;
            plan.shown.Add(made.AddComponent<MeshRenderer>());
        }
        skins.Add(plan);
    }

    // 0 under `from`, 1 over `to`, eased between. (Unity's Mathf.SmoothStep(a, b, t) is NOT this: it goes from a to b
    // by t. Taken for this, it painted every surface seven tenths chipped and dirty - the first skins were nearly black.)
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
                // it stands OUT of the skin (-z), reaches it to within 15 mm, and is not a great solid standing over it
                float area = (top.x - low.x) * (top.y - low.y);
                // (and is not sunk 4 cm into it: the cabinet's blocks cross the slope's plane, and were painted as dark oblongs on it)
                if (low.z > z0 - 0.0004f || top.z < z0 - 0.015f || top.z > z0 + 0.04f || (z0 - low.z > 0.10f && area > 0.05f) || area > 0.6f * plan.whole.width * plan.whole.height)
                    continue;
                Rect on = Rect.MinMaxRect(low.x, low.y, top.x, top.y);
                if (!on.Overlaps(plan.whole))
                    continue;
                Vector2 half = on.size * 0.5f;
                bool round = Mathf.Abs(half.x - half.y) < 0.15f * Mathf.Max(half.x, half.y);
                standing.Add((on.center, half, round ? Mathf.Min(half.x, half.y) : 0.002f));
            }
            // how far every place is from the nearest thing standing on it (within 3 cm; less than 0 under it)
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
            // the screws: in from each corner of each piece of a plate
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
                    // how far in from the edge of the piece this place is on (a place on none is never seen)
                    float edge = -1.0f;
                    foreach (Rect piece in plan.pieces)
                        if (piece.Contains(new Vector2(px, py)))
                            edge = Mathf.Max(edge, Mathf.Min(Mathf.Min(px - piece.xMin, piece.xMax - px), Mathf.Min(py - piece.yMin, piece.yMax - py)));
                    // ("the texture was too uniform over the whole area", the author: one finish at one strength
                    // from end to end.) The paint is not one tone: broad drifts of it, blotches where dirt has
                    // been wiped about and stayed, and patches rubbed smooth and a little pale by sleeves.
                    // (The first answer to that was blotches of dirt and rubbed patches a hand's breadth across, all
                    // over: "The camo is kinda meh". Dirt is not spread about a desk like that: it lies round things
                    // and along edges, which is below. Here only a slow drift of tone, and a fine dustiness.)
                    Color colour = plan.paint * (0.90f + 0.14f * skin_noise(px, py, 1.1f, seed) + 0.05f * skin_noise(px, py, 70.0f, seed + 2.0f));
                    float metal = 0.0f, smooth = plan.smooth * (0.88f + 0.24f * skin_noise(px, py, 2.0f, seed + 31.0f));
                    if (edge >= 0.0f)
                    {
                        // chipped: along the edges, and here and there; a chip shows what is under the paint
                        float chip_at = Mathf.Lerp(0.585f, 0.80f, Mathf.SmoothStep(0.0f, 1.0f, edge / 0.022f));
                        float chipped = ramp(chip_at, chip_at + 0.025f, skin_noise(px, py, 85.0f, seed + 5.0f) * 0.75f + skin_noise(px, py, 260.0f, seed + 9.0f) * 0.25f);
                        // round what stands on it: grime, and under its very edge shade
                        // (under a thing the ring fades out within 7 mm of its edge: what was taken for a thing by mistake is then an outline, not a slab)
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
                    masks[y * wide + x] = new Color(metal, 1.0f, 0.0f, smooth);
                }
            }
            Material made = finish(new Material(Shader.Find("Standard")) { color = Color.white }, "hammer", 18.0f, is_light ? 0.35f : 0.7f, 1.0f, 1.0f);
            made.mainTexture = skin_picture(plan.name + "_albedo", colours, wide, high, true);
            made.SetTexture("_MetallicGlossMap", skin_picture(plan.name + "_mask", masks, wide, high, false));
            made.EnableKeyword("_METALLICGLOSSMAP");
            made.SetFloat("_GlossMapScale", 1.0f);
            AssetDatabase.CreateAsset(made, $"{target}/EU07_skin_{plan.name}.mat");
            foreach (MeshRenderer shown in plan.shown)
                shown.sharedMaterial = made;
            Debug.Log($"EU07 BUILD: skin {plan.name}: {wide} x {high}, {standing.Count} things standing on it, {screws.Count} screws");
        }
        // The cab's fittings follow the vehicle's inside paint: worn as delivered, plain enamel from a can of
        // fresh paint. The swaps are offered to the game's themes here (inside_cans); the MOD puts them on
        // (mod/eu07_driver.cs, watch_doors): a paint component on this prefab did not repaint anything.
        Material plate_fresh = finish(material("desk_plate_paint", new Color(0.66f, 0.67f, 0.65f)), "hammer", 18.0f, 0.35f, 0.34f, 0.0f);
        foreach (skin_plan plan in skins)
        {
            Material worn = plan.shown[0].sharedMaterial;
            inside_cans.Add(("worn", worn, worn));
            inside_cans.Add(("new", worn, plan.paint.grayscale > 0.5f ? plate_fresh : desk_green));
        }
    }

    // 0 to 43: a bit less than one turn on the real disc; 300 degrees here, 6.98 a position.
    private const float controller_sweep = 150.0f;
    private const int eu07_positions = 44;
    // Where a label sits against its item in a panel frame: nearer the driver, flush.
    // 3 mm out, clear of the metal plate the panel's controls stand on (its face is 1 mm out).
    private static readonly Vector3 label_offset = new Vector3(0.0f, -0.035f, -0.003f);
    // The game's caption plate is 65 mm wide; the reference's lamps and buttons stand 57 to 60 mm
    // apart and its switches 50, so a control's caption is made three quarters the size.
    private const float control_caption = 0.75f;

    private static void make_materials()
    {
        black      = material("black", new Color(0.03f, 0.03f, 0.03f));
        white      = material("white", new Color(0.85f, 0.85f, 0.82f));
        // (the plates the controls stand on are sheet aluminium rubbed one way, the doors under the desk varnished
        // plywood - the cab's pass, step 3: "None of the derail valley locos nor the ref have one unified texture")
        grey_panel = finish(material("desk_grey", new Color(0.42f, 0.44f, 0.43f)), "brushed", 7.0f, 0.5f, 0.48f, 0.35f);
        desk_wood  = finish(material("desk_wood", new Color(0.56f, 0.39f, 0.21f)), "wood", 3.2f, 0.6f, 0.55f, 0.0f);
        // THE DESK'S GREEN is hammer-finish enamel, and a green a desk is painted, not a signal's:
        // beside the painted walls the first one, flat and as green as green goes, was the one
        // toy in the cab. Muted, and with the dimples of the finish (hammered, below).
        desk_green = finish(material("desk_green", new Color(0.19f, 0.39f, 0.25f)), "hammer", 18.0f, 0.7f, 0.42f, 0.0f);
        valve_green = finish(material("valve_green", new Color(0.10f, 0.26f, 0.15f)), "hammer", 18.0f, 0.7f, 0.42f, 0.0f);
        teal       = material("teal", new Color(0.10f, 0.38f, 0.40f));
        silver     = finish(material("pedestal", new Color(0.66f, 0.67f, 0.68f)), "brushed", 7.0f, 0.5f, 0.52f, 0.55f);
        red_cap    = material("button_red", new Color(0.60f, 0.07f, 0.05f));
        green_cap  = material("button_green", new Color(0.09f, 0.34f, 0.14f));
        yellow_cap = material("button_yellow", new Color(0.66f, 0.50f, 0.05f));
        bakelite   = material("bakelite", new Color(0.035f, 0.035f, 0.040f));          // lamp housings and button collars: black, a little shine
        bakelite.SetFloat("_Glossiness", 0.60f);
        ivory      = material("ivory", new Color(0.80f, 0.77f, 0.66f));                // the tumbler switches' levers: old cream plastic
        ivory.SetFloat("_Glossiness", 0.45f);
        chrome     = material("chrome", new Color(0.78f, 0.79f, 0.80f));
        door_metal = material("door_metal", new Color(0.50f, 0.51f, 0.52f));           // the doors' handles: worn bright metal
        door_metal.SetFloat("_Glossiness", 0.62f);
        chrome.SetFloat("_Metallic", 0.9f);
        chrome.SetFloat("_Glossiness", 0.75f);
        shapes.Clear();
    }

    // A FINISH: what a surface is besides its colour, as a tiling pair of pictures of our own
    // (tools/finishes.py -> model/finishes/) - which way the surface leans, and a faint
    // mottling - laid so many to the metre by a second set of UVs in metres, which lay_finish
    // gives every mesh that wears such a material.
    //   hammer   hammer-finish enamel, the dimpled paint of switchgear and desks: eighteen to
    //            the metre (a picture every 5.5 cm, dimples of a few millimetres);
    //   brushed  sheet aluminium rubbed one way;
    //   wood     varnished plywood, the grain up the door.
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

    // A second set of UVs IN METRES for every mesh under a root that wears a finished material:
    // each corner laid flat along the axis its face is nearest to, in the root's own frame, so
    // that a finish is the same size on a knob and on the desk's top however each was stretched
    // into shape. The meshes are copies: the game's own cube serves a hundred parts.
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
            // (a shape of the build's own may have no first set and no tangents: a surface that leans needs both)
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

    private static Material material(string name, Color colour)
    {
        Shader standard = Shader.Find("Standard") ?? throw new MissingReferenceException("no Standard shader");
        Material made = new Material(standard) { color = colour };
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.mat");
        return made;
    }

    // A lamp lens. The game lights it by putting _EmissionColor = emissionColor * value on the
    // renderer through a property block (IndicatorEmission), so the material must have
    // emission switched on. Its own emission is a hair above black: the Standard shader's
    // inspector switches the keyword off on a black emission if the material is ever opened.
    private static Material lens(string name, Color dark)
    {
        Shader standard = Shader.Find("Standard") ?? throw new MissingReferenceException("no Standard shader");
        Material made = new Material(standard) { color = dark };
        if (!made.HasProperty("_EmissionColor"))
            throw new MissingFieldException("the Standard shader has no _EmissionColor");
        made.EnableKeyword("_EMISSION");
        made.SetColor("_EmissionColor", new Color(0.002f, 0.002f, 0.002f));
        made.SetFloat("_Glossiness", 0.85f);                                           // glass
        made.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        AssetDatabase.CreateAsset(made, $"{target}/EU07_lens_{name}.mat");
        return made;
    }

    // A dial's face: a picture of our own (tools/dial_faces.py -> model/dials/) as a material.
    private static readonly Dictionary<string, Material> dial_faces = new Dictionary<string, Material>();
    private static readonly Dictionary<int, Mesh> bezels = new Dictionary<int, Mesh>();
    private static Mesh dial_disc;

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

    // A flat disc of diameter 1 in a panel's own plane, facing out of it (-z), with the
    // picture's right to the panel's +x and its top to +y. Both windings: it is seen from one
    // side only, and so cannot come out inside out.
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

    // The black rim of a dial of that radius, one mesh a size.
    // Where the six small meters of the desk's slope stand, from the desk's middle, and how big their dials are.
    // A rim is 12 mm wider than its dial (bezel, below), so 80 mm dials 99 mm apart stand 7 mm clear of each other;
    // between the third and the fourth, 14 mm.
    private const float meter_dial = 0.080f;
    private static readonly float[] meter_x = { 0.100f, 0.199f, 0.298f, 0.404f, 0.503f, 0.602f };

    private static Mesh bezel(float radius)
    {
        int size = Mathf.RoundToInt(radius * 1000.0f);
        if (!bezels.TryGetValue(size, out Mesh made))
            bezels[size] = made = torus("bezel_" + size, radius + 0.002f, 0.004f, 40, 8);
        return made;
    }

    // Where the speed recorder's clock and counter are on its face, as shares of the dial's radius:
    // the same figures as tools/dial_faces.py, which draws the clock's dial and the counter's window.
    private const float recorder_clock_up = 0.27f, recorder_clock_radius = 0.19f;
    private const float recorder_counter_down = 0.53f, recorder_counter_wide = 0.60f, recorder_counter_high = 0.14f;

    // A hand of the recorder's clock: once round for `round` of what its port says (12 hours, 60
    // minutes), clockwise from straight up. Not a lagging one: at the top of the hour it must
    // not swing all the way back.
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

    // A drum of the recorder's counter: the ten figures round it (a picture of ours,
    // tools/dial_faces.py), turning about the panel's x so that the figure its port says faces
    // out of the panel - 3.5 shows it half-way from 3 to 4.
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

    // The drum's skin: a strip round the panel's x. Unturned, the foot tenth of its picture -
    // the figure 0 - faces out of the panel; a tenth of a turn further up it is the next figure.
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

    // One of the two swing bolts over the speed recorder's dial: a bright stud out of a round
    // boss, and through its end the wire loop it is turned by, leaning `lean` degrees off the
    // upright toward the instrument's side.
    private static void recorder_bolt(Transform panel, string name, Vector2 at, float lean)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        solid(root, "Boss", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, 0.0f, -0.003f), new Vector3(0.016f, 0.003f, 0.016f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        solid(root, "Stud", PrimitiveType.Cylinder, chrome, new Vector3(0.0f, 0.0f, -0.011f), new Vector3(0.006f, 0.008f, 0.006f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        if (!shapes.TryGetValue("recorder_loop", out Mesh loop))
            shapes["recorder_loop"] = loop = torus("recorder_loop", 0.0095f, 0.0014f, 20, 6);
        // the ring lies about its own y; turned about the panel's normal, its plane holds that normal and the leaning line
        Vector2 along = new Vector2(-Mathf.Sin(lean * Mathf.Deg2Rad), Mathf.Cos(lean * Mathf.Deg2Rad));
        GameObject ring = shaped(root, "Loop", loop, chrome, new Vector3(along.x * 0.0085f, along.y * 0.0085f, -0.016f));
        ring.transform.localRotation = Quaternion.Euler(0.0f, 0.0f, 90.0f + lean);
    }

    // Throws if a component does not serialise a field by that name: a field the script sets
    // and Unity does not keep would otherwise do nothing, silently.
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

    // A primitive with or without a collider. A cylinder's own CapsuleCollider is replaced by
    // a box (his button has a BoxCollider on its cylinder).
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

    // The inactive SIBLING a control is clicked through (not a child: CCL warns that a static
    // area under a moving control defeats its purpose). Triggers only.
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

    // The feeder must sit on the control itself: the game's InteractablePortFeeder.Init does
    // GetComponent<ControlImplBase>().
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

    // A push-button in "hold" mode: 1 while pressed, 0 when let go (DV.CabControls.ButtonBase,
    // isToggle + isTogglingBack). The mod takes presses from the port's event, not by polling.
    // In the panel frame -Z is out of the panel, so pushed in is +Z.
    private static GameObject make_button(Transform panel, string name, Vector2 at, Material cap,
        string port, string label, string key, bool mushroom = false)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        // The desk's own buttons, MEASURED on the reference cab and made here ("they look like
        // some china lamps and buttons. this is true HCP material", the author, 2026-10-11, of
        // the round domes these replace). A black twelve-sided collar 49 mm across its flats
        // stands 11 mm out of the plate, its outer edge broken, a 29.6 mm bore through it. In
        // the bore sits a FLAT coloured cap, 1.5 mm under the collar's top. The MUSHROOM kind -
        // main breaker off, and wheel slip - has a 30 mm stem out of the same collar and a head
        // 47.8 mm across standing 23.6 mm off the plate, hardly domed. What the hand takes hold
        // of is an unseen disc where the cap is.
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

    // The horn valve's lever: out of a small valve body, sprung to the middle, swinging level.
    // To the game a LeverProxy WITHOUT steps: the mouse moves the spring's aim and letting go
    // puts it back to 0 (LeverNonVR: resetTargetPositionOnUngrab), and a key held does the same
    // through MouseScrollKeyboardInput - each tick moves the aim by scrollWheelHoverScroll
    // degrees, here the whole throw, and the key's release puts it back (LeverBase.Scroll).
    // Value 0.5 at rest; 0 with its end to the left, 1 to the right.
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
        lever.jointAxis = Vector3.down;                          // a rising value brings its end to the right
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

    // THE BREAKERS' BOX ("breakers, batteries and controls it is. this side panel over here is
    // normally under a cover. check reference", the author, 2026-10-11, of a bare plate with the
    // game's two fuse switches on it). LOOKED AT: the reference cab has, let into the assistant's
    // desk top, a box 452 x 235 mm and 129 deep under a hinged LID 506 x 226 that lies flush
    // with the top - a knob at the middle of its near edge, a hasp plate on the desk before
    // it. In the box, toward the side wall, a plate with the "Bateria" switch and two red
    // tumblers (cab heating, sanding); toward the middle, lower, a cover plate through which two
    // rows of miniature breakers show, 18 mm a pole and 84 long, labelled in pairs. Sizes and
    // places are the reference's; what each does is in eu07_low_voltage.cs.
    //   - the lid is hung at its FAR edge and lifts toward the windscreen, to stand all but upright;
    //   - "Bateria" is the game's main-electrics fuse switch, brought here: the master;
    //   - a labelled pair is ONE two-pole breaker, its two levers tied; the eleventh pole of
    //     the far row, unlabelled on the reference too, is a spare with no lever;
    //   - of the red tumblers only the cab heating's is here. The sanding's stood here once and
    //     could not be found; it is on the driver's plate.
    // Desk frame: x to the driver's right, z forward from the desk's near edge.
    private const float well_x0 = -0.7705f, well_x1 = -0.2615f, well_z0 = 0.2575f, well_z1 = 0.4905f, well_deep = 0.158f;
    private const float box_x0 = -0.736f, box_x1 = -0.284f, box_z0 = 0.266f, box_z1 = 0.487f, box_rim = 0.025f, box_divide = -0.5285f;
    private const float battery_plate = 0.070f, breaker_plate = 0.092f;      // how far under the desk top each plate lies

    private static void make_breaker_box(Transform desk, Transform top, Transform old)
    {
        // `top` lies in the desk top: its x the desk's, its y the desk's z, its z DOWN into the desk.
        void lump(string name, Material material, float x0, float x1, float y0, float y1, float z0, float z1) => solid(top, name, PrimitiveType.Cube, material,
            new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, y1 - y0, z1 - z0), Quaternion.identity, false);
        // the box's walls, from its rim down: what is between the well's mouth and the box's inside
        lump("BoxWallLeft" , grey_panel, well_x0, box_x0 , well_z0, well_z1, box_rim, well_deep);
        lump("BoxWallRight", grey_panel, box_x1 , well_x1, well_z0, well_z1, box_rim, well_deep);
        lump("BoxWallNear" , grey_panel, box_x0 , box_x1 , well_z0, box_z0 , box_rim, well_deep);
        lump("BoxWallFar"  , grey_panel, box_x0 , box_x1 , box_z1 , well_z1, box_rim, well_deep);
        lump("BoxBatteryPlate", silver, box_x0, box_divide - 0.0015f, box_z0, box_z1, battery_plate, well_deep);
        lump("BoxDivider"     , grey_panel, box_divide - 0.0015f, box_divide + 0.0015f, box_z0, box_z1, box_rim + 0.010f, well_deep);
        lump("BoxBreakerPlate", silver, box_divide + 0.0015f, box_x1, box_z0, box_z1, breaker_plate, well_deep);

        // "Bateria": his main-electrics fuse switch, lying on the battery's plate.
        Transform battery = carry(child(old, "MainFuse"), top, new Vector3(-0.595f, 0.388f, battery_plate));
        Transform caption = child(battery, "Label");
        caption.localPosition = label_offset;
        LabelLocalizer words = caption.GetComponent<LabelLocalizer>() ?? throw new MissingComponentException("his fuse switch's label is not a LabelLocalizer");
        words.key = "eu07/battery_110";
        serialised(words, "key");
        UnityEngine.Object.DestroyImmediate(child(old, "MotorFuse").gameObject);      // his motor fuse's switch: the fuse is the "control" breaker now

        GameObject heating = make_toggle(top, "CabHeating", new Vector2(-0.690f, 0.440f), "cabHeatingSwitch.EXT_IN", "eu07/cab_heating", null);
        heating.transform.parent.localPosition += new Vector3(0.0f, 0.0f, battery_plate);
        child(heating.transform, "Tumbler").GetComponent<MeshRenderer>().sharedMaterial = red_cap;

        // The breakers: two rows of five pairs, 40 mm a pair, and the spare pole at the far row's end.
        const float first = -0.4875f, pitch = 0.040f, far_row = 0.436f, near_row = 0.339f;
        for (int index = 0; index < 5; ++index)
        {
            make_breaker(top, "BreakerFar" + index , new Vector2(first + pitch * index, far_row ), breakers_far[index].fuse , breakers_far[index].label , null);
            make_breaker(top, "BreakerNear" + index, new Vector2(first + pitch * index, near_row), breakers_near[index].fuse, breakers_near[index].label,
                breakers_near[index].fuse == "fusebox.TM_POWER" ? "TractionMotorFuse" : null);
        }
        Transform spare = node(top, "BreakerSpare", new Vector3(first + pitch * 4.0f + 0.031f, far_row, breaker_plate)).transform;
        breaker_pole(spare, 0.0f, false);

        // THE LID. Its frame: the hinge line at the far edge, in the desk top's plane; the leaf
        // reaches from it toward the driver (-z). Turning about +x lifts the near edge.
        float wide = well_x1 - well_x0 - 0.003f, deep = well_z1 - well_z0 - 0.003f;
        const float thick = 0.006f, opens = 82.0f;
        // (a millimetre and a half proud of the desk top: flush, lid and top were one green and the lid could not be seen)
        Transform root = node(desk, "BreakerFlap", new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0015f, well_z1 - 0.0015f)).transform;
        GameObject control = node(root, "C_BreakerFlap", Vector3.zero);
        Vector3 middle = new Vector3(0.0f, -thick * 0.5f, -deep * 0.5f);
        solid(control.transform, "Leaf", PrimitiveType.Cube, desk_green, middle, new Vector3(wide, thick, deep), Quaternion.identity, false);
        // a folded edge all round, under it, so that open it is a tray and not a sheet
        solid(control.transform, "LeafEdgeNear" , PrimitiveType.Cube, desk_green, new Vector3(0.0f, -0.012f, -deep + 0.002f), new Vector3(wide, 0.024f, 0.004f), Quaternion.identity, false);
        solid(control.transform, "LeafEdgeLeft" , PrimitiveType.Cube, desk_green, new Vector3(-wide * 0.5f + 0.002f, -0.012f, -deep * 0.5f), new Vector3(0.004f, 0.024f, deep), Quaternion.identity, false);
        solid(control.transform, "LeafEdgeRight", PrimitiveType.Cube, desk_green, new Vector3( wide * 0.5f - 0.002f, -0.012f, -deep * 0.5f), new Vector3(0.004f, 0.024f, deep), Quaternion.identity, false);
        shaped(control.transform, "Knob", turned("flap_knob", 8, true, (0.0060f, 0.0f), (0.0060f, 0.0080f), (0.0125f, 0.0120f), (0.0125f, 0.0190f), (0.0080f, 0.0240f), (0.0f, 0.0240f)), black,
            new Vector3(0.0f, 0.0f, -deep + 0.026f)).transform.localRotation = Quaternion.Euler(90.0f, 0.0f, 0.0f);
        foreach (float side in new[] { -0.17f, 0.17f })
            solid(root, "Hinge", PrimitiveType.Cylinder, desk_green, new Vector3(side, 0.002f, 0.0f), new Vector3(0.010f, 0.030f, 0.010f), Quaternion.Euler(0.0f, 0.0f, 90.0f), false);
        // the hasp's plate on the desk before the lid, with its slot
        solid(desk, "FlapHasp", PrimitiveType.Cube, desk_green, new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0015f, well_z0 - 0.018f), new Vector3(0.080f, 0.003f, 0.026f), Quaternion.identity, false);
        solid(desk, "FlapHaspSlot", PrimitiveType.Cube, black, new Vector3((well_x0 + well_x1) * 0.5f, desk_top + 0.0031f, well_z0 - 0.018f), new Vector3(0.016f, 0.0004f, 0.010f), Quaternion.identity, false);

        Vector3 grip_size = new Vector3(wide, 0.026f, deep);
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, new Vector3(0.0f, -0.011f, -deep * 0.5f), grip_size, Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, 0.010f, -deep + 0.026f));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);

        // A notched lever, as the doors (see make_door for why): a stop every ten degrees or so.
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        // NO static area to be taken by: such an area stays where the lid lay SHUT, the game's
        // pointer takes the first thing it meets, areas included, and so the open lid went on
        // answering for everything under it ("the lid overwrites the interactability for the
        // breakers", the author, 2026-10-11). The lid is taken by its own leaf, which moves with it.
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

    // One pole of a miniature breaker as it shows through the cover plate: the nose, light grey,
    // 17.4 mm wide and 44 long, with a dark window; and, if asked, its lever standing up.
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

    // A two-pole breaker of the 110 V, a toggle that feeds a FUSE of the game's and follows it.
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

    // THE DOOR: a sheet leaf on the panel's face over the opening, hinged at its edge away
    // from the cab, swinging out into the passage. A notched lever, as the flap over the
    // breakers and for the same reasons (no static area: it would stay where the door lay
    // shut and answer for the switches behind it). The game's lock holds it shut while the
    // interlock cock stands at P (wire_simulation).
    private static void make_isolator_door(Transform parent, Vector3 at, Quaternion facing, float box_wide, float box_high)
        => make_sheet_door(parent, "IsolatorDoor", at, facing, box_wide, box_high, "isolatorDoor.EXT_IN", "eu07/isolator_door", true);

    // THE CZUWAK / SHP BOX: the two lamps of the vigilance device and of the train stop, in a box of their own that
    // stands on the sill in front of the driver, right of the speedometer. LOOKED AT, not copied: two photographs
    // under free licences (Commons: "LampkaCzuwakSHP.jpg" by Pinoteres, the same box in an ET22, and "Czuwak
    // aktywny.jpg" by Januszppk, the device's own unit), and where and how big the simulator's reference cab has
    // it (175 x 102 x 110 mm, its middle 0.50 m right of the cab's own). What the first shows: a small sheet
    // box painted as the desk, its face leaning
    // back toward the driver's eyes, the top and the cheeks standing out past the face as a hood; in the face two
    // square lamps - the left blue, the right red - and under them a strip with the two names. The left is the
    // vigilance device's (the mod blinks it while the device asks: eu07.LAMP_VIGILANCE); the right is the train stop's, with a
    // port of its own that nothing writes yet.
    // `foot`: the middle of the face's bottom edge, in the desk's frame, on the sill.
    private static void make_shp_box(Transform desk, Vector3 foot, Color czuwak, Color shp)
    {
        const float wide = 0.175f, high = 0.100f, deep = 0.105f, leans = 14.0f, hood = 0.016f, sheet = 0.002f;
        Transform face = panel(desk, "ShpBox", foot, leans);                // -z is out of the face, toward the driver; y up the face
        shaped(face, "Case", blocks("shp_case",
            (new Vector3(-wide * 0.5f, 0.0f, 0.0f), new Vector3(wide * 0.5f, high, deep)),
            (new Vector3(-wide * 0.5f - sheet, high, -hood), new Vector3(wide * 0.5f + sheet, high + sheet, deep)),
            (new Vector3(-wide * 0.5f - sheet, 0.0f, -hood), new Vector3(-wide * 0.5f, high, deep)),
            (new Vector3(wide * 0.5f, 0.0f, -hood), new Vector3(wide * 0.5f + sheet, high, deep)),
            (new Vector3(-wide * 0.5f - sheet, -sheet, -hood * 0.5f), new Vector3(wide * 0.5f + sheet, 0.0f, deep))), desk_green, Vector3.zero);
        solid(face, "Strip", PrimitiveType.Cube, silver, new Vector3(0.0f, 0.025f, -0.0006f), new Vector3(0.150f, 0.014f, 0.0012f), Quaternion.identity, false);
        // THE LAMPS ARE THE DESK'S OWN ("from what i can see the SHP/CA box are the same indicator lamps we use", the
        // author, 2026-10-13, of the flat glasses in wide frames I had drawn from the photograph): the same square
        // black housing and curved cover as every signal lamp on the plate - make_lamp, here on the box's face.
        make_lamp(face, "LampVigilance", new Vector2(-0.036f, 0.060f), czuwak, "eu07.LAMP_VIGILANCE", "eu07/lamp_vigilance");
        make_lamp(face, "LampShp", new Vector2(0.036f, 0.060f), shp, "eu07.LAMP_SHP", "eu07/lamp_shp");
        // (No sound on either lamp: the game's lamp buzzes for as long as it blinks, and this one is to blink for
        // three seconds before anything is heard. The buzzer is the cab's own: mod/eu07_driver.cs, watch_vigilance.)
    }

    // A PLYWOOD DOOR OF THE DESK'S CABINET, hung by its left edge and swinging out into the cab (-z of the desk).
    // As the painted ones it replaces stood: the leaf 12 mm thick and 2 mm proud of the face, a black knob at its
    // free edge, two chrome hinges, a dark seam all round. The whole leaf is its handle.
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
        lever.jointAxis = Vector3.up;                           // about +y what lies along x swings out toward -z
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

    // (the same door for any cabinet: its name, its port, its caption, and whether it carries the high-voltage sign)
    private static void make_sheet_door(Transform parent, string name, Vector3 at, Quaternion facing, float box_wide, float box_high, string port, string label, bool warning,
        float swings = 105.0f)
    {
        const float lap = 0.005f, leaf_thick = 0.006f;
        float leaf_wide = box_wide + 2.0f * lap, leaf_high = box_high + 2.0f * lap;
        Material sheet = body_material("eu07_cabinet");
        // (at: the middle of the opening, on the panel's face; the door's x is toward the cab)
        Transform hinge = node(parent, name, at + facing * new Vector3(-leaf_wide * 0.5f, 0.0f, 0.0f)).transform;
        hinge.localRotation = facing;
        GameObject control = node(hinge, "C_" + name, Vector3.zero);
        Vector3 middle = new Vector3(leaf_wide * 0.5f, 0.0f, -0.001f - leaf_thick * 0.5f);
        solid(control.transform, "Leaf", PrimitiveType.Cube, sheet, middle, new Vector3(leaf_wide, leaf_high, leaf_thick), Quaternion.identity, false);
        // a dark seal round the opening, standing out a little past the leaf: shut, door and panel
        // are one grey and the door could not be seen (as the flap over the breakers once)
        foreach (float edge in new[] { -1.0f, 1.0f })
        {
            solid(hinge, "Seal", PrimitiveType.Cube, black, new Vector3(leaf_wide * 0.5f, edge * (box_high * 0.5f + 0.0055f), -0.0005f), new Vector3(leaf_wide + 0.012f, 0.011f, 0.001f), Quaternion.identity, false);
            solid(hinge, "Seal", PrimitiveType.Cube, black, new Vector3(leaf_wide * 0.5f + edge * (box_wide * 0.5f + 0.0055f), 0.0f, -0.0005f), new Vector3(0.011f, box_high, 0.001f), Quaternion.identity, false);
        }
        // a folded edge all round, inside the opening, so that open it is a door and not a sheet
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
        lever.jointAxis = Vector3.up;                           // about +y what lies along x swings out of the panel
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

    // THE HANDLE OF A COCK IN A PIPE RUN: a hub on the cock's spindle, a flat red lever with a black
    // ball, a quarter turn about the panel's own normal - lying along the panel's x (as built) the
    // cock is open and the port 1, standing down it is shut and 0. The cock's body is the model's.
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

    // A HANDLE THAT IS SHOVED from one end of its travel to the other and stays where it is put: the
    // game's lever with two stops, dragged by the hand against a spring, as the desk's own levers
    // are. ("the instantaneous switch for things that look like they need to be shoved in place...
    // need to be shoved in place with actual bone physics", the author, 2026-10-12, of cocks and
    // knife switches that were toggle switches under their looks and flipped at a click.)
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

    // A KNIFE SWITCH: blades hinged in a row of jaws and clipping into a second row, tied
    // together at their free end by a bar with a handle out of it. `across`: the blades lie
    // along the panel's x and swing out sideways (else they stand up along y and are pulled
    // out and down). `poles`: where each blade is, across the blades' own direction. Shut is 0;
    // pulled open - `opens` degrees out of the panel - is 1.
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
        // A KNIFE SWITCH SOUNDS LIKE ONE ("Could you look for another switch close audio? the door slam audio for our
        // contactors are kinda meh, and the DE6 does do a knife contactor ... its the hand operated contactors",
        // the author, 2026-10-12). It had the cocks' two deep thuds, which is a door. The game's own big
        // breaker - its locomotives' "C_BreakerElectrics" - plays Switch_08_Electric: that, as the blades go
        // home or come out; and the short solid click of its smaller breakers at the stop.
        LeverProxy lever = swing(control, grip, tie_at + new Vector3(0.0f, 0.0f, -0.062f), across ? Vector3.up : Vector3.left, 0.0f, opens,
            sphere_area(root, name, along * (length * 0.6f) + beside * middle + new Vector3(0.0f, 0.0f, -proud - 0.035f), 0.055f),
            "Switch_08_Electric", "Switch_10_Solid");
        feed(control, port);
        lever.OnValidate();
        make_label(root, label_at, label, control_caption);
    }

    // A cock with a lever: two positions, 28 degrees either side of lying along the panel's x,
    // turning about the panel's own normal. A boss on the panel, a flat lever from it with a
    // round grip at its end.
    private static void make_cock(Transform panel, string name, Vector2 at, string port, string label, float dial = 0.0f)
    {
        // With a DIAL: a round plate 4 mm thick under it with a drawn face on it - P beside where
        // the lever stands up, 0 beside where it stands down (tools/dial_faces.py, "interlock":
        // there is one dial, the interlock's) - and the caption under the plate. (The first dial
        // was a disc a millimetre thick a millimetre off the wall, with the caption half under
        // it: "also Z clipping". The second had a mark at each place: "both positions are I".)
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

    // A toggle switch, his Pantograph rebuilt: lever out of the panel, hinged about panel X.
    private static GameObject make_toggle(Transform panel, string name, Vector2 at, string port, string label, string key)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // The desk's own switch, MEASURED on the reference cab and made here. It was a thin round
        // metal bat (ours, then the game's DM3 one): "the switches need switching to something
        // which looks more like railway, since right now they look aircraft" (the author,
        // 2026-10-11). The real one is a TUMBLER: a flat ivory lever from a hub to a rounded
        // end, the end ribbed for the thumb, turning 30 degrees either way; round it a black
        // frame, and a slotted screw above and below. Its sizes are with tumbler(), below. What
        // the hand takes hold of is a box, unseen.
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
        toggle.nonVrStaticInteractionArea = sphere_area(root, name, new Vector3(0.0f, 0.0f, -0.010f), 0.024f);     // the reference's switches stand 50 mm apart
        toggle.jointAxis = new Vector3(1.0f, 0.0f, 0.0f);       // as his
        toggle.jointLimitMin = -30.0f;                           // as the reference's lever leans
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

    // A signal lamp: LampControlProxy and LampPortReaderProxy on the holder (the game's
    // LampPortReader.Init does GetComponent<LampControl>()), the lens a child. The port's
    // value is the lamp state: the mod writes 0 (off) or 1 (on). No LampLogic, so no
    // simulation port reference that could be left unconnected.
    private static void make_lamp(Transform panel, string name, Vector2 at, Color dark, string port, string label)
    {
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // The desk's own signal lamp, MEASURED on the reference cab and made here (the game's
        // domed lamp stood here for a day: "china lamps", the author, 2026-10-11). A square black
        // housing 48 mm a side with rounded corners stands 12.3 mm out of the plate, its top
        // edge broken; on it a CURVED COVER of glass 31.6 mm square in plan ("get a side view
        // of those lamps, those are curved covers", the author, of a lens I had read as a
        // tapered block): an arch along the plate, 4.4 mm high at its crown, the same section
        // all the way across, its two ends flat. One mesh, so that all of it lights together.
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

    // A needle dial. reader: "port:<id>" for a simulation port, or "cylinder" / "pipe" /
    // "reservoir" for the brake system. The reader sits on the same object as the indicator
    // (IndicatorPortReader.Init: GetComponent<Indicator>()).
    private static void make_gauge(Transform panel, string name, Vector2 at, float low, float high,
        string reader, float scale, string label,
        float sweep = 135.0f, float smooth = 0.3f, float diameter = 0.090f, int ticks = 5, string face = null, bool rim = true)
    {
        float radius = diameter * 0.5f;
        Transform root = node(panel, name, new Vector3(at.x, at.y, 0.0f)).transform;
        // In a panel frame -z is OUT of the panel. Each layer stands clear of the one under
        // it by millimetres: the dial used to lie exactly in the panel's own face, and the
        // two flickered through each other (the author's screenshots, 2026-10-10).
        // (ONLY where no face is drawn. Under a drawn face it was a white disc a millimetre behind it, and from the
        // driver's own eye the two fought: figures and marks of the face wiped out in patches - "Z clipping on
        // the main dials", the author, 2026-10-12. My own camera does not show it: "your Unity based looker
        // doesnt show what the player sees".)
        if (face == null)
            solid(root, "Dial", PrimitiveType.Cylinder, white, new Vector3(0.0f, 0.0f, -0.004f),
                new Vector3(diameter, 0.001f, diameter), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);
        if (face != null)
        {
            // numerals and graduations: a drawn face (tools/dial_faces.py) on a disc a millimetre
            // over the white one, and a black rim round it
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
        // A slim pointer reaching the graduations, a short tail and a small boss: the 4 mm bar
        // it was hid the figures ("the pointers need to be smaller", the author, 2026-10-10).
        float needle = Mathf.Max(0.0012f, radius * 0.032f);
        solid(pivot, "Needle", PrimitiveType.Cube, black, new Vector3(0.0f, radius * 0.335f, -0.010f),
            new Vector3(needle, radius * 0.97f, 0.0015f), Quaternion.identity, false);
        solid(pivot, "NeedleBoss", PrimitiveType.Cylinder, black, new Vector3(0.0f, 0.0f, -0.010f),
            new Vector3(radius * 0.16f, 0.0025f, radius * 0.16f), Quaternion.Euler(90.0f, 0.0f, 0.0f), false);     // its face 1.7 mm proud of the pointer's

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

    // A CAB SIDE WINDOW'S OPENING PANE: the model's pane, sliding forward along the wall behind the pane
    // ahead of it. The game's sliding control (a "puller": a body free along its own y between two stops,
    // shut at 0 and open at the far stop, set by the hand or the wheel and held at a tenth of its travel).
    //   - Every pane of the model lies on the OUTSIDE of the wall, proud of it. Two panes in one plane cannot
    //     pass: so the pane that slides runs in a track of its own INSIDE the wall, 25 mm in from where the
    //     model has it (the wall is 30 mm from skin to lining: the pane, 12 thick, has 9 either side of it).
    //     Shut, it stands that much deeper than the fixed pane beside it, as a sliding sash does; its edges,
    //     which lap the opening by 8 mm, are in the wall. Open, it is behind the post and the fixed pane, 13 mm
    //     clear of that pane's inner face.
    //   - How far: the driver's pane, 0.59 m wide with a fixed pane of 0.46 ahead of it, 0.40 m; the other
    //     side's, 0.46 m wide with only a quarter pane of 0.21 ahead before the corner post, 0.18 m.
    //   - Which way is along the wall and which way is in, is taken from the pane itself: the walls draw in
    //     toward the cab's front by 49 mm in the metre.
    //   - What the hand takes is a latch on its rear edge, inside. NOT the pane: one looks out through it.
    // OpenableControlProxy tells the game it is a way out for sound, as a door is.
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
        // (left and right as one sits in that cab: cab A looks along +z, cab B along -z)
        string name = "Window" + (centre.x * toward_end < 0.0f ? "Left" : "Right") + (toward_end > 0.0f ? "A" : "B");
        Debug.Log($"EU07 BUILD: {name} from {part}: centre {centre:F3} slides {travel:F2} m along {open:F3}, in is {inward:F3}; {wide:F3} wide {high:F3} high {thick:F3} thick");

        // its own frame: y against the way it opens (a puller is shut at 0 and goes toward -y), z into the cab, x up or down
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
        // the latch: a black bar upright on the inside face, 35 mm from the rear edge (27 mm clear of the opening's edge)
        Vector3 latch = new Vector3(0.0f, wide * 0.5f - 0.035f, thick * 0.5f);
        solid(control.transform, "Latch", PrimitiveType.Cube, black, latch + new Vector3(0.0f, 0.0f, 0.009f), new Vector3(0.090f, 0.016f, 0.018f), Quaternion.identity, false);
        solid(control.transform, "LatchFoot", PrimitiveType.Cube, black, latch + new Vector3(0.0f, 0.0f, 0.0015f), new Vector3(0.120f, 0.026f, 0.003f), Quaternion.identity, false);
        GameObject grip = solid(control.transform, "Handle", PrimitiveType.Cube, white, latch + new Vector3(0.0f, 0.0f, 0.020f), new Vector3(0.180f, 0.070f, 0.060f), Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<MeshFilter>());
        // the middle of its travel: the stops are half the travel either side of this
        GameObject anchor = node(root, "Anchor", new Vector3(0.0f, -travel * 0.5f, 0.0f));

        PullerProxy slide = control.AddComponent<PullerProxy>();
        slide.colliderGameObjects = new[] { grip };
        slide.rigidbodyMass = 3.0f;
        // NO DRAG. The game holds a notched puller where it was left by putting it back there every frame;
        // drag is against the body's speed over the GROUND, and this body is free along the track - the way
        // the locomotive runs.
        slide.rigidbodyDrag = 0.0f;
        slide.useSteppedPuller = true;
        slide.notches = 10;
        slide.scrollWheelHoverScroll = 0.1f;                                 // a tenth of the travel a tick of the wheel
        slide.useCustomConnectionAnchor = true;
        slide.connectionAnchor = anchor.transform;
        slide.linearLimit = travel * 0.5f;
        serialised(slide, "rigidbodyDrag", "useSteppedPuller", "notches", "scrollWheelHoverScroll", "useCustomConnectionAnchor", "connectionAnchor", "linearLimit");
        control.AddComponent<OpenableControlProxy>().closedAtZero = true;
        sounds(slide, ("drag", "LeverDrag_02_MetalLightDirt"), ("limitHit", "LeverLimit_01_PlasticLight"));
        slide.OnValidate();
    }

    // THE CONTACTORS' MOVING ARMS AND THEIR ARC CHUTES ("contactor animation and overhaul", the author,
    // 2026-10-12). The model has every contactor's standing parts and says where each arm's hinge is
    // (model/eu07_contactors.txt, in its own frame: this vehicle's is (-x, z, -y) of it). For each the build
    // makes, in the hinge's frame - x along the row, y up, z toward the passage:
    //   - "Arm": the copper arm standing up from the hinge with its contact on the cage's side of its head.
    //     Turned about its own x by +24 degrees it has fallen open toward the passage, 34 mm of air between
    //     the contacts. The mod turns it (mod/eu07_driver.cs: watch_contactors) as the circuit's contactor
    //     of that NAME stands;
    //   - "Chute": the arc chute over the contacts, a renderer of its own so that the mod can soot it.
    // WHICH IS WHICH. The circuit has 48 (eu07_contactors.cs): the two line contactors, the four that group
    // the motors, thirty that cut out the starting resistance, twelve that shunt the fields. So has the
    // model. They are named along the rows from the middle of the locomotive toward the cab: at the desk's
    // end (cab A) the upper row, on the cage's top where they are best seen, is SL1 SL3 IS IM IR1 IR2 and
    // SR1-SR8 - the line contactors, the grouping, and the first notches; under it SR9-SR20; at the other
    // end SR21-SR30 SP1 SP2 above and SP3-SP12 below.
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
        // the desk's end (this vehicle's +z) first, an end's upper row before its lower, a row from the middle outward
        places = places.OrderBy(place => place.hinge.z > 0.0f ? 0 : 1).ThenBy(place => -place.shelf).ThenBy(place => Mathf.Abs(place.hinge.z)).ToList();
        if (places.Count != contactor_names.Length)
            throw new InvalidDataException($"{places.Count} contactors in model/eu07_contactors.txt, and the circuit has {contactor_names.Length}");
        // the arm: a bar 32 x 16 mm from 14 mm under the hinge to 92 over it, and on its head a contact standing 18.5 mm toward the cage
        // (two materials of one mesh, bar first: the mod colours the bar by the contactor's heat and the contact by its soot)
        Mesh arm = new Mesh { name = "contactor_arm" };
        arm.CombineMeshes(new[]
        {
            new CombineInstance { mesh = blocks("contactor_arm_bar", (new Vector3(-0.016f, -0.014f, -0.008f), new Vector3(0.016f, 0.092f, 0.008f))), transform = Matrix4x4.identity },
            new CombineInstance { mesh = blocks("contactor_arm_contact", (new Vector3(-0.018f, 0.062f, -0.0265f), new Vector3(0.018f, 0.092f, -0.008f))), transform = Matrix4x4.identity },
        }, false, false);
        arm.RecalculateBounds();
        AssetDatabase.CreateAsset(arm, $"{target}/EU07_contactor_arm.asset");
        // the chute: 108 mm wide, from 92 mm over the hinge (the contacts' height) to 240, its shoulders sloped; 22 to 192 mm behind the hinge
        Mesh chute = slab("contactor_chute", 0.054f, new Vector2(-0.192f, 0.092f), new Vector2(-0.022f, 0.092f), new Vector2(-0.022f, 0.200f),
            new Vector2(-0.045f, 0.240f), new Vector2(-0.169f, 0.240f), new Vector2(-0.192f, 0.200f));
        Material copper = body_material("eu07_contact");
        // THE SOOT IS PAINTED, in six stages from clean to burnt black, and the mod gives a chute the stage it has
        // come to. (The first try was one grey over the whole chute, darker by degrees: at a third of the way it
        // was 15 % darker in a dim room, and nobody could call it soot - "just only the lack of soot", the author.
        // The second was streaks down every face alike, olive brown. Then what it IS was looked up, as far as it
        // can be - mostly switchgear makers' manuals and one study of copper electrodes in air - and reasoned
        // from: it is not lamp-black but COPPER, melted and boiled off the contacts by the arc and settled as a
        // dull grey-black film, brownish where it is thin, on the nearest cold thing; and the blow-out coil
        // drives the arc off the contacts up into the chute. So it lies where the arc's breath goes: round the
        // MOUTH at the foot of the chute's face toward the passage, in a fan widening upward from the gap; round
        // the TOP where the chute lets it out; on the chute's sides only by their front edge and over the
        // contacts. A side away from all that stays cement.)
        // The stages are the materials of one hidden object, ContactorSoot, where the mod finds them.
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

    // A chute's material at one stage of its sooting: a picture made here, 256 by 128. Its LEFT half is for the
    // chute's two sides (across it: from the chute's back to its front, the passage's side; up it: up the chute),
    // its RIGHT half for the faces round its edge - the face toward the passage, the shoulders, the top (across
    // it: across the chute; up it: up the chute). slab() lays the faces out so.
    private const int soot_stages = 6;

    private static Material sooted_chute(int stage)
    {
        const int size = 128;
        // (ramp(), not Mathf.SmoothStep - see ramp(): used for this, "only near the top" was 0.8 to 1 everywhere, and the
        // third stage was black all over. Found by writing the pictures out and looking at them.)
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
                // the cement itself: a little uneven, clean or not
                float grain = Mathf.PerlinNoise(column * 0.19f + 3.1f, row * 0.19f + 7.7f);
                // how unevenly it lies: in streaks that run up the face, and in blotches
                float uneven = 0.55f + 0.55f * Mathf.PerlinNoise(column * 0.085f + 11.3f, row * 0.016f + 2.9f) + 0.40f * Mathf.PerlinNoise(column * 0.035f + 5.5f, row * 0.045f + 9.1f);
                float top = ramp(0.80f, 1.0f, up), lies;
                if (side)
                {
                    // a side: by its front edge, most at the foot where the contacts are; and under the top
                    float by_front = ramp(0.70f, 1.0f, across) * (1.0f - 0.55f * up);
                    float over_contacts = Mathf.Exp(-((1.0f - across) * (1.0f - across) + up * up) / 0.07f);
                    lies = 0.75f * by_front + 0.9f * over_contacts + 0.65f * top;
                }
                else
                {
                    // the face toward the passage: a fan from the gap at the middle of its foot, widening and thinning upward; and the top
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
        // (a copy to look at, beside the model's other generated pictures: nothing reads it)
        File.WriteAllBytes(Path.GetFullPath(Path.Combine(Application.dataPath, $"../../model/paint/chute_soot_{stage}.png")), picture.EncodeToPNG());
        AssetDatabase.CreateAsset(picture, $"{target}/EU07_chute_soot_{stage}.asset");
        Material made = new Material(Shader.Find("Standard")) { color = Color.white, mainTexture = picture };
        made.SetFloat("_Metallic", 0.0f);
        made.SetFloat("_Glossiness", 0.12f);
        AssetDatabase.CreateAsset(made, $"{target}/EU07_chute_soot_{stage}.mat");
        return made;
    }

    // Several blocks as one mesh, every face lit as a flat: each from its least corner to its greatest.
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
                // the two ways across this face
                Vector3 one = Mathf.Abs(way.x) > 0.5f ? Vector3.up : Vector3.right, other = Vector3.Cross(way, one);
                Vector3 centre = middle + Vector3.Scale(way, half);
                Vector3[] corners = { centre - Vector3.Scale(one, half) - Vector3.Scale(other, half), centre + Vector3.Scale(one, half) - Vector3.Scale(other, half),
                                      centre + Vector3.Scale(one, half) + Vector3.Scale(other, half), centre - Vector3.Scale(one, half) + Vector3.Scale(other, half) };
                flat_face(points, normals, triangles, way, corners);
            }
        }
        return keep_shape(name, points, normals, triangles);
    }

    // A SLAB cut to an outline: the outline (which must bulge nowhere inward) is in the slab's own (z, y),
    // and the slab reaches `half` either way along x. Every face lit as a flat.
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
        // on a picture: up it is up the slab, on every face. The picture's LEFT half is the two cut faces', across it
        // being along the outline, back to front; its RIGHT half the faces' round the edge, across it being across the slab.
        // (a hair inside each half, so that neither takes the other's colour at the join)
        float low = outline.Min(at => at.y), high = outline.Max(at => at.y), back = outline.Min(at => at.x), front = outline.Max(at => at.x);
        List<Vector2> laid = points.Select((point, index) => new Vector2(
            Mathf.Abs(normals[index].x) > 0.5f ? Mathf.Lerp(0.004f, 0.496f, Mathf.InverseLerp(back, front, point.z)) : Mathf.Lerp(0.504f, 0.996f, Mathf.InverseLerp(-half, half, point.x)),
            Mathf.InverseLerp(low, high, point.y))).ToList();
        return keep_shape(name, points, normals, triangles, laid);
    }

    // One flat face of a mesh: its corners in order round it, fanned from the first, turned to look along `lit`.
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

    // A SUN VISOR: a sheet of smoked stuff hung by two clips on the model's rail over an outer windscreen
    // pane (model/eu07_blockout.py prints the rails: the model's frame is x across, y along, z up; this
    // vehicle's is (-x, z, -y) of it). The rail is the hinge. Put away (0) the sheet stands straight up from
    // it, 17 mm clear of the face over the windscreen; pulled over (1) it has come round through the cab -
    // out level at head height halfway - and hangs along the glass, 166 degrees on, some 45 mm clear of the
    // pane, across its top 17 cm: which is where the game's eyes are. Seven stops, a notched lever of the
    // game's as the doors are.
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
        Vector3 one = new Vector3(-from.x, from.z, -from.y), other = new Vector3(-to.x, to.z, -to.y);
        Vector3 middle = (one + other) * 0.5f, along = (other - one).normalized;
        float toward_end = Mathf.Sign(middle.z);
        Vector3 to_glass = Vector3.Cross(along, Vector3.up).normalized;
        if (to_glass.z * toward_end < 0.0f)
            to_glass = -to_glass;
        string name = "Visor" + (middle.x * toward_end < 0.0f ? "Left" : "Right") + (toward_end > 0.0f ? "A" : "B");
        // its own frame: x along the rail, y up, z toward the glass
        Transform root = node(parent, name, middle).transform;
        root.localRotation = Quaternion.LookRotation(to_glass, Vector3.up);
        GameObject control = node(root, "C_" + name, Vector3.zero);
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
        // the wheel turned DOWN brings it down ("i think the visor needs its axis reversed. i need to scroll up to
        // bring the visor down", the author, 2026-10-12, the first evening he had one in his hand)
        lever.scrollWheelHoverScroll = -1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 7;
        lever.jointAxis = Vector3.left;                          // about -x what stands up along y comes over toward -z: into the cab
        lever.useSpring = true;
        lever.jointSpring = 40.0f;
        lever.jointDamper = 3.0f;
        lever.useLimits = true;
        lever.jointLimitMin = 0.0f;
        lever.jointLimitMax = comes_over;
        serialised(lever, "interactionPoint", "useSteppedJoint", "steppedValueUpdate", "notches", "jointAxis", "jointLimitMin", "jointLimitMax", "useSpring");
        control.AddComponent<NotchedControlNumberedNamesProxy>();
        sounds(lever, ("notch", "LeverNotch_04_Tick"), ("limitHit", "LeverLimit_01_PlasticLight"));
        lever.OnValidate();
    }

    // A door: the model's leaf on a notched lever, turning about the vertical from shut (0) to
    // a little over a right angle INTO the cab. OpenableControlProxy tells the game it is a door: an open one lets
    // the outside's sound in. What the hand takes hold of is a box the size of the leaf.
    private static readonly Dictionary<string, Matrix4x4> door_frames = new Dictionary<string, Matrix4x4>();

    private static void make_door(Transform parent, GameObject model, string part, string name, Vector3 hinge, bool across = false, float opens = 92.0f)
    {
        if (!door_frames.TryGetValue(part, out Matrix4x4 frame))
            throw new KeyNotFoundException("the body brought no " + part);
        Transform source = child(model.transform, part);
        Mesh mesh = UnityEngine.Object.Instantiate(source.GetComponent<MeshFilter>().sharedMesh);
        mesh.name = name;
        AssetDatabase.CreateAsset(mesh, $"{target}/EU07_{name}.asset");
        // The leaf is hung so that it stays exactly where it stood in the body (`frame`: its
        // mesh's place in the vehicle, with the turns the model file and the template give it).
        Transform root = node(parent, name, hinge).transform;
        GameObject control = node(root, "C_" + name, Vector3.zero);
        GameObject leaf = node(control.transform, "Leaf", (Vector3) frame.GetColumn(3) - hinge);
        leaf.transform.localRotation = frame.rotation;
        leaf.transform.localScale = frame.lossyScale;
        // One object for each of the leaf's materials. The game outlines what the hand is on by
        // a mesh's FIRST material only: as one mesh of many, just the dark band lit up, and
        // nothing at all from inside ("only part of the outer surface lights up as targetable
        // - inside no", the author, 2026-10-10).
        Material[] materials = source.GetComponent<MeshRenderer>().sharedMaterials;
        for (int index = 0; index < mesh.subMeshCount; ++index)
        {
            // only the points this material's faces use: a piece carrying the whole leaf's points
            // has the whole leaf's bounds, and tools/desk_clearance.py took every piece for the leaf
            int[] corners = mesh.GetTriangles(index);
            int[] used = corners.Distinct().ToArray();
            Dictionary<int, int> place = Enumerable.Range(0, used.Length).ToDictionary(at => used[at], at => at);
            Vector3[] points = mesh.vertices, normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] atlas = mesh.uv, world = mesh.uv2;
            Mesh piece = new Mesh { name = $"{name}_{index}" };
            piece.SetVertices(used.Select(at => points[at]).ToList());
            piece.SetNormals(used.Select(at => normals[at]).ToList());
            // (and what the paint goes by: without these a painted door was one grey - every place the picture's corner)
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
        // ITS HANDLES ("comparing to the Derail valley modelling standard, the handles seem
        // blocky ... a nice detail would be to see the handle move down as part of the opening
        // and closing animation", the author, 2026-10-11). The model's were boxes joined into
        // the leaf; the first remake, a round lever on a round rosette, was nobody's handle:
        // "have a look at the ingame models and our reference models for the general shape".
        // LOOKED AT: the game's own cab doors (a flat bar of a lever with a square elbow, on a
        // tall narrow plate with a round lock under it - the DH4's; or on a small boss with the
        // lock on a rose of its own - the shunter's), and the reference cab's (the same flat
        // cranked bar, 139 mm long; on the outside door's inner face a LOCK CASE screwed onto
        // the leaf, a second box under it, the lever out of the case). So, at each place the
        // model gives (model/eu07_door_handles.txt):
        //   - a flat lever, 22 x 14 mm in section and 128 mm long, on a square neck with its
        //     outer corner cut off, every edge broken: door_lever();
        //   - on a plate 40 x 190 mm with a round lock under the handle;
        //   - but INSIDE a cab's outside door a lock case 111 x 88 mm standing 54 mm off the
        //     leaf on a plate 118 x 228, a smaller case under it, and the lever out of the
        //     upper case on a short neck - sizes as the reference's.
        // The turning part is called DoorHandle: the mod finds it by that name and dips it as
        // the leaf leaves and meets its frame (mod/eu07_driver.cs). Its lever lies toward the
        // hinge: the part "Lever" is made lying toward +x and turned over where it must lie
        // the other way, which is also how the mod knows which way is down for it.
        int handle_number = 0;
        foreach ((Vector3 spindle, Vector3 out_of, Vector3 along) in door_handle_places(part))
        {
            Transform seat = node(control.transform, "HandleSeat" + handle_number, spindle - hinge).transform;
            seat.localRotation = Quaternion.LookRotation(-out_of, Vector3.up);                 // -z out of the leaf, as on a panel
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
        // the leaf's box in the hinge's own frame, whichever way round the model file left the mesh
        Bounds box = new Bounds(leaf.transform.localPosition + leaf.transform.localRotation * Vector3.Scale(mesh.bounds.center, leaf.transform.localScale), Vector3.zero);
        foreach (float x in new[] { -1.0f, 1.0f })
            foreach (float y in new[] { -1.0f, 1.0f })
                foreach (float z in new[] { -1.0f, 1.0f })
                    box.Encapsulate(leaf.transform.localPosition + leaf.transform.localRotation
                        * Vector3.Scale(mesh.bounds.center + Vector3.Scale(mesh.bounds.extents, new Vector3(x, y, z)), leaf.transform.localScale));
        Debug.Log($"EU07 BUILD: {name} hinge {hinge:F3} leaf centre {box.center:F3} size {box.size:F3}");
        // The leaf must start AT its hinge's foot and reach from it toward its own cab's front,
        // the wall's thickness to the cab's side of it: else the model and these figures disagree.
        float toward_front = Mathf.Sign(hinge.z), into_cab = -Mathf.Sign(hinge.x);
        // A door ACROSS the vehicle (a cab's back wall) starts at its hinge's foot too and reaches
        // from it toward that cab's right, which is the vehicle's +x in cab A and -x in cab B;
        // it stands in the wall, just to the room's side of the hinge.
        if (across
            ? Mathf.Abs(box.min.y) > 0.01f || box.size.y < 1.8f || box.size.y > 1.9f || box.size.x < 0.62f || box.size.x > 0.76f
              || Mathf.Abs((toward_front > 0.0f ? box.min.x : -box.max.x)) > 0.01f || Mathf.Abs(box.center.z) > 0.06f || box.center.z * toward_front > 0.001f
            : Mathf.Abs(box.min.y) > 0.005f || box.size.y < 1.6f || box.size.y > 1.8f || box.size.z < 0.45f || box.size.z > 0.56f
              || Mathf.Abs((toward_front > 0.0f ? box.min.z : -box.max.z)) > 0.01f || Mathf.Abs(box.center.x) > 0.06f || box.center.x * into_cab < -0.001f)
            throw new InvalidDataException($"door {name} came in wrong: centre {box.center:F3} size {box.size:F3}");
        GameObject handle = solid(control.transform, "Handle", PrimitiveType.Cube, white, box.center, box.size, Quaternion.identity, true);
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshRenderer>());
        UnityEngine.Object.DestroyImmediate(handle.GetComponent<MeshFilter>());
        // A shut door must STOP whoever walks at it ("i can phase through doors", the author,
        // 2026-10-11): the box above is only what the hand takes hold of, and the player goes
        // through that. So the leaf carries a second box the player cannot enter, which turns
        // with it - shut it fills the doorway, open it stands where the leaf stands. An interior's
        // parts are all put on the game's layer for things one handles; this one is kept on the
        // layer one walks on and into. A little smaller than the leaf, to clear the doorway's edges.
        GameObject stop = node(control.transform, "Stop", box.center);
        stop.AddComponent<BoxCollider>().size = box.size - (across ? new Vector3(0.03f, 0.03f, 0.0f) : new Vector3(0.0f, 0.03f, 0.03f));
        InteriorNonStandardLayerProxy solid_to_walk_into = stop.AddComponent<InteriorNonStandardLayerProxy>();
        solid_to_walk_into.Layer = DVLayer.Train_Walkable;
        solid_to_walk_into.includeChildren = false;
        // taken hold of by its free edge, at the handle's height
        Vector3 grip = across
            ? new Vector3(box.center.x + Mathf.Sign(box.center.x) * (box.extents.x - 0.07f), 1.00f, box.center.z)
            : new Vector3(box.center.x, 0.77f, box.center.z + Mathf.Sign(box.center.z) * (box.extents.z - 0.07f));
        GameObject point = node(control.transform, "Interaction Point", grip);
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);

        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = new[] { handle };
        // NO STANDING AREA. It had one, a box where the leaf stands SHUT, which does not turn with it: open, the
        // doorway still answered for the door - the hand could not reach what lay beyond it, and the game's
        // "go there" would not pass ("the machine room doors have that glitch where the interaction collider
        // doesnt move with the mesh, blocking interactivity with HV isolated ... also i cant F move into the
        // cabin", the author, 2026-10-12; the isolator box's door and the breakers' flap were rid of theirs on
        // the 11th for the same). The leaf's own box, which turns with it, is what the hand takes.
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 30.0f;
        lever.rigidbodyDrag = 15.0f;
        // The game moves a lever under the mouse by moving its SPRING's target
        // (GrabHandlerHingeJoint.FixedUpdate): a hinge without a spring cannot be dragged
        // ("the lack of draggability", the author, 2026-10-10, of a door that had none). And a
        // lever WITHOUT notches is sent back to 0 when it is let go (resetTargetPositionOnUngrab).
        // So the door is a notched lever like the desk's: thirteen stops from shut to open, a
        // spring to carry it to the hand and hold it at a stop, a wheel tick a stop.
        lever.scrollWheelHoverScroll = 1.0f;
        lever.useSteppedJoint = true;
        lever.steppedValueUpdate = true;
        lever.notches = 13;
        // a door across the vehicle swings into its cab by turning the OTHER way about the vertical
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
        // Its sound is the GAME's own ("there is a door slam audio from the game iirc", the
        // author, 2026-10-11): the clip the DH4's cab doors - and the DE2's, DE6's, DM3's and the
        // shunter's - give their hinges for the moment the leaf meets a stop, read out of the
        // game's own door controls and picked by ear from eight ("correct. 1 it is"). The
        // caboose's door clip stood here for an hour. NOT the grinding those doors also carry
        // for the leaf's moving: "not something ive heard in the game".
        sounds(lever, ("limitHit", "ItemCollision_MetalSheet_03"));
        lever.OnValidate();
    }

    // A door's lever handle, in its spindle's frame (x along the lever, y up, -z out of the door):
    // a square neck from the door to `neck` out, its outer corner cut off at 45 degrees, and from
    // it a flat bar 14 mm thick and 22 high to 128 mm from the spindle, its end's corners cut
    // too; every long edge broken by a millimetre and a half. Two convex pieces in one mesh,
    // lit as flats. The shape is the game's own doors' and the reference cab's (see make_door).
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

    // Where the model says the hinged doors' handles are: its own frame is x across, y along, z
    // up; this vehicle's is (-x, z, -y) of it.
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

    // Both doors swing INTO their cabs by turning the same way about the vertical (cab A's is on
    // the left with its hinge behind it, cab B's the same thing turned half round). ASSUMED
    // SIGN, as the controller's: if they open outward in the game, make this Vector3.down.
    private static readonly Vector3 door_axis = Vector3.up;

    // An end's light switches on a slope panel: the three whites below, the two reds above. With
    // `dimming` the three dimming switches too, in a row from there (for the stand that has no
    // desk top to put them on).
    private static void light_switches(Transform slope, string cab, float middle, Vector2? dimming)
    {
        solid(slope, "PlateLights", PrimitiveType.Cube, silver, new Vector3(middle, 0.0f, -0.0005f), new Vector3(0.24f, 0.225f, 0.003f), Quaternion.identity, false);
        List<(string name, float x, float y, string label)> switches = new List<(string name, float x, float y, string label)>
        {
            ("lightLeft" , middle - 0.062f, -0.055f, "eu07/light_left"), ("lightTop", middle, -0.055f, "eu07/light_top"), ("lightRight", middle + 0.062f, -0.055f, "eu07/light_right"),
            ("redLeft"   , middle - 0.062f,  0.052f, "eu07/red_left")  , ("redRight", middle + 0.062f,  0.052f, "eu07/red_right"),
        };
        if (dimming is Vector2 from)
        {
            solid(slope, "PlateDimming", PrimitiveType.Cube, silver, new Vector3(from.x + 0.07f, 0.0f, -0.0005f), new Vector3(0.24f, 0.225f, 0.003f), Quaternion.identity, false);
            switches.Add(("lightDimLeft", from.x, from.y, "eu07/dim_left"));
            switches.Add(("lightDimTop", from.x + 0.07f, from.y, "eu07/dim_top"));
            switches.Add(("lightDimRight", from.x + 0.14f, from.y, "eu07/dim_right"));
        }
        foreach ((string name, float x, float y, string label) in switches)
            make_toggle(slope, char.ToUpper(name[0]) + name.Substring(1) + cab, new Vector2(x, y), $"{name}{cab}.EXT_IN", label, null);
    }

    // A lever, his Throttle / Reverser with the axis a parameter, built in the DESK frame.
    // With notches its value is notch / (notches - 1).
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
            // the caller hangs a mesh of its own on the control: the box stays as what the hand takes hold of, unseen
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
        // What the mouse points at: a box round the handle as built - or, for a handle that
        // swings far from where it is built, a ball round the pivot.
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

    // The controller wheel: to the game a LeverProxy about the vertical (see below why not a
    // RotaryProxy; the game's WheelProxy has no notches at all, docs/desk/design_desk.md b4). 44 positions over 300 degrees about the vertical; 0 at the
    // anticlockwise end, 43 at the clockwise end: the wheel turns CLOCKWISE to add power
    // (the author). The value is position / 43, as the mod reads it.
    //
    // The hinge's zero is the orientation it is built in, the MIDDLE of the travel; the game
    // turns it to the port's value when the feeder starts. So the white mark on the rim is
    // built at 12 o'clock and stands at 7 o'clock at position 0, at 5 o'clock at 43, over
    // the graduated disc on the pedestal, which is ticked at the same 44 angles.
    private static GameObject make_controller(Transform desk, Vector3 hub, string port)
    {
        const float radius = 0.1845f;                // the rim's own circle: 397 mm across over all
        Transform root = node(desk, "Controller", hub).transform;
        GameObject control = node(root, "C_Controller", Vector3.zero);

        // The wheel is the model's: a round rim, two spokes straight across, a domed cap, made
        // as it stands at position 0 - so it is hung turned by the half travel that the hinge,
        // built at the middle, takes back at 0. What the hand takes hold of is unseen: a box at
        // the hub and a ring of boxes along the rim. Nothing between them, so that the key and
        // the field lever under the wheel can still be pointed at.
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

        // To the game the wheel is a LEVER, not a rotary. Both are a hinge with a stepped joint,
        // but only the game's LeverBase stops the stepped joint re-snapping while the keys or
        // the mouse wheel scroll it (steppedJoint.isSpringActive = false, true again on
        // Release). RotaryBase does not: a stepped rotary scrolled by spring target is put back
        // on its old notch a frame later, which is what the author saw - "it moves the wheel
        // slightly but not into notch" (2026-10-10; read in the decompiled game, twice).
        GameObject point = node(control.transform, "Interaction Point", new Vector3(0.0f, 0.0f, radius));
        point.transform.localRotation = new Quaternion(1.0f, 0.0f, 0.0f, 0.0f);     // as his
        LeverProxy lever = control.AddComponent<LeverProxy>();
        lever.colliderGameObjects = grips.ToArray();
        // What the mouse points at without aiming for the rim: the hub only. A slab over the
        // whole wheel would lie between the eye and the reverser's key and the field lever,
        // which are reached through the open wheel.
        lever.nonVrStaticInteractionArea = box_area(root, "Controller", new Vector3(0.0f, -0.02f, 0.0f), new Vector3(0.13f, 0.07f, 0.13f));
        lever.interactionPoint = point.transform;
        lever.rigidbodyMass = 5.0f;                  // about 0.16 kg m2 about the hinge, as the field lever that is known to step
        lever.rigidbodyDrag = 15.0f;
        lever.rigidbodyAngularDrag = 0.0f;
        lever.scrollWheelHoverScroll = 1.0f;         // one position a key tap or scroll tick
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

    // A control's own sounds are fields of its proxy, empty in a control made here; this fills
    // them with clips of the GAME's, by name, when the game loads the vehicle. Without it a
    // lever or a button of ours moved in silence.
    private static void sounds(Component control, params (string field, string clip)[] clips)
    {
        SoundGrabber grabber = control.gameObject.AddComponent<SoundGrabber>();
        grabber.ScriptToAffect = control;
        grabber.Replacements = clips.Select(one => new ResourceReplacement { FieldName = one.field, ReplacementName = one.clip }).ToArray();
        grabber.OnValidate();
    }

    // A part that is the GAME's own: nothing of it is in this vehicle but its name. Custom Car
    // Loader puts the game's mesh and material of those names on it when the vehicle loads
    // (MeshGrabberFilter, MaterialGrabberRenderer), so it looks as the game's own locomotives'
    // do. In the editor, and to tools/desk_clearance.py, it is an empty object.
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

    // Shapes the primitives do not give, each made once and kept as an asset.
    private static readonly Dictionary<string, Mesh> shapes = new Dictionary<string, Mesh>();

    private static GameObject shaped(Transform parent, string name, Mesh mesh, Material material, Vector3 at)
    {
        GameObject made = node(parent, name, at);
        made.AddComponent<MeshFilter>().sharedMesh = mesh;
        made.AddComponent<MeshRenderer>().sharedMaterial = material;
        return made;
    }

    // A straight-sided or tapered prism standing OUT of a panel: its foot (radius `foot`, to the
    // corners) in the panel's plane z = 0, its top (radius `top`) at z = -height, closed at both
    // ends, every flat lit as a flat. `turn` turns the corners about the axis.
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
        void face(Vector3 outward, Vector3[] ring)          // a flat; whichever way round its corners come, it looks `outward`
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
        face(Vector3.back, Enumerable.Range(0, sides).Select(index => corner(index, true)).ToArray());        // the top: out of the panel
        face(Vector3.forward, Enumerable.Range(0, sides).Select(index => corner(index, false)).ToArray());    // the foot
        Mesh made = new Mesh { name = name };
        made.SetVertices(points);
        made.SetNormals(normals);
        made.SetTriangles(triangles, 0);
        made.RecalculateBounds();
        AssetDatabase.CreateAsset(made, $"{target}/EU07_{name}.asset");
        shapes[name] = made;
        return made;
    }

    // A shape drawn OUT of a panel through a row of outlines, one for each level: every outline
    // the same number of points, each point with the way that is "away" from the shape there
    // (in the panel's plane). `proud` is how far out of the panel a level stands. Between two
    // levels the skin is lit round its corners as the outlines say and across the levels as a
    // flat band - unless `faceted`, when every face is a flat of its own. An outline drawn in
    // to one point closes the shape.
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
                return;                                                     // two corners in one place: no face
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
            // which way this band looks, in (away from the axis, out of the panel): square to its own run
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

    // A DOME standing out of a panel: half of a flattened ball, `radius` at its foot - which is `foot` out of
    // the panel - and `deep` from there to its crown; lit as the round thing it is, every point by its own
    // way out of the ball (a turned shape is lit band by band, and a dome of bands is a stack of cones).
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
        // (each face turned to look the way its corners are lit: outward)
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

    // A TURNED shape standing out of a panel: round (or, `faceted`, many-sided with every flat
    // lit as a flat) about the panel's normal, through (radius, how far out) pairs. A pair of
    // radius 0 closes it; a run back toward the panel on a smaller radius is a bore.
    private static Mesh turned(string name, int sides, bool faceted, params (float radius, float proud)[] profile)
    {
        if (shapes.TryGetValue(name, out Mesh known))
            return known;
        Vector2[] round = Enumerable.Range(0, sides).Select(index => new Vector2(Mathf.Cos(2.0f * Mathf.PI * index / sides), Mathf.Sin(2.0f * Mathf.PI * index / sides))).ToArray();
        return loft(name, profile.Select(level => round.Select(way => (way * level.radius, way)).ToArray()).ToList(), profile.Select(level => level.proud).ToList(), faceted);
    }

    // The tumbler switch's lever, about its hub's axis (the panel's x): a hub of radius 4 mm,
    // a flat body narrowing to a rounded end of radius 2.6 mm 20 mm out, 8.5 mm wide, with five
    // ribs across each face of its outer third. One mesh. Upright it stands straight out of
    // the panel (-z).
    // (Made first to the reference's own size - 10 mm wide, 26 mm of reach, the hub 2.6 mm out of the
    // plate, 28 mm tall in all - and in the cab that stood too high: "i think they need to sit a bit
    // lower and be a bit smaller", the author, 2026-10-11. Now 8.5 mm wide with 20 mm of reach and the
    // hub's middle half a millimetre out, 20 mm tall in all: the lever comes up through the frame.)
    private const float tumbler_hub_out = 0.0005f;
    private static Mesh tumbler()
    {
        if (shapes.TryGetValue("tumbler", out Mesh known))
            return known;
        const float wide = 0.0085f, hub = 0.0040f, tip = 0.0026f, reach = 0.0200f;
        const int round = 8;
        float taper = Mathf.Asin((hub - tip) / reach);
        // the outline in (y, how far out), anticlockwise: round the end, then round the hub; each point with the way out of the shape
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
        foreach (float x in new[] { -wide * 0.5f, wide * 0.5f })               // the two flat sides
        {
            Vector3 side = new Vector3(Mathf.Sign(x), 0.0f, 0.0f), middle = new Vector3(x, 0.0f, -reach * 0.5f);
            for (int index = 0; index < outline.Count; ++index)
                corner(middle, place(outline[index], x), place(outline[(index + 1) % outline.Count], x), side, side, side);
        }
        Mesh body = new Mesh { name = "tumbler_body" };
        body.SetVertices(points);
        body.SetNormals(normals);
        body.SetTriangles(triangles, 0);
        // the ribs: five across each face, on the outer third, square to the face
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

    // A BLOCK standing out of a panel: square, `half` its side, its corners rounded to `corner`,
    // at each (half, corner, how far out) level. A level of half 0 closes it.
    private static Mesh block(string name, params (float half, float corner, float proud)[] levels)
    {
        return slab(name, levels.Select(level => (level.half, level.half, level.corner, level.proud)).ToArray());
    }

    // The same, not square: half its width (x) and half its height (y) at each level. Levels
    // BEHIND the panel's plane have a negative `proud`.
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

    // A signal lamp's cover: curved glass, square in plan. An arch ALONG the panel (its y), of
    // one section all the way ACROSS it (its x), closed flat at both ends; the arch's feet are
    // in z = 0 and its crown `rise` out of it. Lit round along the arch.
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
        foreach (float x in new[] { -half, half })                    // the two flat ends
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

    // A ring lying flat (about y), as a mesh of its own: Unity has no torus primitive.
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

    // ---- export ----------------------------------------------------------------------------
    // CCL's own export, which lives behind an editor window: validate, build the bundle,
    // write Info.json.
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
    }
}
