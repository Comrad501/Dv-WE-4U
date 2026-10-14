// EU07 for Derail Valley - a back door for testing with nobody at the keyboard.
//
// It exists only when there is a folder called "debug" beside the mod's DLL. Then, twice a
// second, it
//
//   - reads debug/command.txt, obeys each line, deletes the file, and appends what
//     happened to debug/log.txt
//   - writes debug/status.txt: the first EU07's speed, position, every port, the relays
//
// Commands:
//
//   spawn [east north]         an EU07 on the track nearest the player, or nearest a point
//                              that many metres east and north of him (so as not to land
//                              against whatever he is standing in)
//   ws 1 | ws 0                fast breaker
//   reset                      OPN and WJ1
//   field N                    field lever
//   high 1 | high 0            high start
//   handbrake X                0 off, 1 on
//   port <id> <value>          set any port, e.g.  port throttle.EXT_IN 0.1
//   fuse <id> 1 | 0            a fuse, e.g.  fuse fusebox.ELECTRICS_MAIN 1
//   delete                     remove the EU07s this spawned
//   gadget <item> [r u f]      a gadget made, mounted in the instrument cupboard and soldered
//   shove <m/s>                the chosen one set rolling at that speed
//   trip                       as if its SHP box had just passed a track magnet
//   show <item> [up] [turned]  an item's shapes hung over its roof, for the camera
//                              (those four: only on an EU07 this spawned)
//   watch [seconds] [car id]   every physics step and frame, each car of the train: speed, travel, how far
//                              the interior stands off the body, the moving/sleep flags -> debug/motion_trace.csv
//                              (read-only: any train, the player's own included)
//
// The controller wheel and reverser are ports like the rest: throttle.EXT_IN (0-1 over 44
// positions) and reverser.CONTROL_EXT_IN.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEngine;

using DV.ThingTypes;

namespace electric_sim.unit_A;

internal class eu07_debug: MonoBehaviour
{
    private static string folder => Path.Combine(eu07_mod.folder, "debug");
    public  static bool   wanted => Directory.Exists(folder);

    private readonly List<TrainCar> _spawned = [];
    private float _next = 0.0f;

    void Update()
    {
        if (Time.unscaledTime < _next)
            return;
        _next = Time.unscaledTime + 0.5f;
        try
        {
            string commands = Path.Combine(folder, "command.txt");
            if (File.Exists(commands))
            {
                string[] lines = File.ReadAllLines(commands);
                File.Delete(commands);
                foreach (string line in lines)
                {
                    string[] words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length > 0)
                        File.AppendAllText(Path.Combine(folder, "log.txt"), $"{DateTime.Now:HH:mm:ss} {line} -> {obey(words)}\n");
                }
            }
            eu07_driver.all.RemoveAll(driver => driver == null);
            File.WriteAllText(Path.Combine(folder, "status.txt"),
                $"{DateTime.Now:HH:mm:ss} locomotives {eu07_driver.all.Count}\n" + (chosen()?.status() ?? ""));
        }
        catch (Exception problem)
        {
            Main.release_log("EU07 debug: " + problem);
        }
    }

    // Which locomotive the status is of and the commands are for: the one the player is aboard,
    // if it is one of ours; else the one he was last aboard; else the first. (It was always the
    // first - and a second one, spawned to try a cold start, could not be looked at:
    // "Now whats L-073's problem?", the author, 2026-10-12.)
    private eu07_driver? _last_chosen, _named;       // _named: chosen by the "choose" command, which overrules where the player is
    private eu07_driver? chosen()
    {
        if (_named != null && eu07_driver.all.Contains(_named))
            return _named;
        _named = null;
        eu07_driver? aboard = eu07_driver.all.Find(driver => driver != null && driver.car != null && driver.car == PlayerManager.Car);
        if (aboard != null)
            _last_chosen = aboard;
        if (_last_chosen == null || !eu07_driver.all.Contains(_last_chosen))
            _last_chosen = eu07_driver.all.Count > 0 ? eu07_driver.all[0] : null;
        return _last_chosen;
    }

    // (An item is not whole until a frame after it is made - what it is to the inventory is found in its Start -
    // and the game's own mounting asks for that. So: made, a frame waited, mounted.)
    private System.Collections.IEnumerator mount(TrainCar car, GameObject prefab, string name, Vector3 at)
    {
        GameObject made = Instantiate(prefab, car.transform.position + Vector3.up * 3.0f, Quaternion.identity);
        made.name = name;
        yield return null;
        yield return null;
        string outcome;
        try
        {
            DV.Customization.Gadgets.GadgetItem? item = made.GetComponent<DV.Customization.Gadgets.GadgetItem>();
            if (item == null)
            {
                Destroy(made);
                outcome = name + " is not a gadget";
            }
            else
            {
                Transform parent = car.Customization.GetParentingTransform();
                Vector3 local = parent.InverseTransformPoint(car.transform.TransformPoint(at));
                Quaternion turned = Quaternion.Inverse(parent.rotation) * car.transform.rotation * Quaternion.LookRotation(Vector3.back, Vector3.up);
                DV.Customization.Gadgets.GadgetBase mounted = DV.Customization.Gadgets.GadgetItem.Place(car.Customization, local, turned, item);
                mounted.MakeSoldered();                         // (the game's own: it is this that joins the gadget to the vehicle's fuse)
                outcome = $"mounted {mounted.name} at {at} of {car.ID}, soldered {mounted.IsSoldered}; fed {mounted.PowerState}";
            }
        }
        catch (Exception problem)
        {
            outcome = "could not be mounted: " + problem;
        }
        File.AppendAllText(Path.Combine(folder, "log.txt"), $"{DateTime.Now:HH:mm:ss} gadget {name} -> {outcome}\n");
    }

    private string obey(string[] words)
    {
        switch (words[0])
        {
            case "watch":
            {
                // watch [seconds] [car id]: every physics step and frame, each car of that car's train - its speed,
                // how far it has gone, how far its interior stands off the body, the game's moving/sleep flags - into
                // debug/motion_trace.csv (eu07_motion_watch.cs). Read-only: on any train, the player's own included.
                float seconds = words.Length > 1 && float.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float asked) ? Mathf.Clamp(asked, 1.0f, 600.0f) : 60.0f;
                TrainCar? around = words.Length > 2
                    ? CarSpawner.Instance.AllCars.FirstOrDefault(car => car != null && car.ID == words[2])
                    : PlayerManager.Car ?? PlayerManager.LastLoco ?? chosen()?.car;
                if (around == null)
                    return words.Length > 2 ? "no car " + words[2] : "no train to watch: board one, or name a car";
                return eu07_motion_watch.start(folder, around, seconds);
            }
            case "spawn":
            {
                // spawn [livery id] [east north]: one of ours, or - to try ours with one of the game's - any livery ("spawn ?" lists the locomotives')
                string wanted_livery = eu07_mod.livery_prefix;
                int offset_at = 1;
                if (words.Length > 1 && !float.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                {
                    wanted_livery = words[1];
                    offset_at = 2;
                }
                if (wanted_livery == "?")
                    return "liveries with a multiple-unit cable: " + string.Join(" ", DV.Globals.G.Types.Liveries.Where(candidate => candidate.prefab != null && candidate.prefab.GetComponentInChildren<DV.MultipleUnit.MultipleUnitModule>(true) != null).Select(candidate => candidate.id));
                TrainCarLivery? livery = DV.Globals.G.Types.Liveries.Find(candidate => candidate.id == wanted_livery);
                if (livery == null)
                    return "no livery called " + wanted_livery;
                Transform? player = PlayerManager.PlayerTransform;
                if (player == null)
                    return "no player";
                Vector3 where = player.position;
                if (words.Length == offset_at + 2 && float.TryParse(words[offset_at], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float east)
                                                  && float.TryParse(words[offset_at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float north))
                    where += new Vector3(east, 0.0f, north);
                TrainCar car = CarSpawner.Instance.SpawnCarOnClosestTrack(where, livery, flipRotation: false, playerSpawnedCar: true, uniqueCar: false);
                if (car == null)
                    return "the spawner gave nothing";
                _spawned.Add(car);
                return $"spawned {car.ID} at {car.transform.position}";
            }
            case "delete":
            {
                int count = 0;
                foreach (TrainCar car in _spawned)
                {
                    if (car != null)
                    {
                        CarSpawner.Instance.DeleteCar(car);
                        ++count;
                    }
                }
                _spawned.Clear();
                return $"deleted {count}";
            }
            case "pair":
            {
                // OURS WITH ONE OF THE GAME'S ("Could you dry test the stock to ours test both ways (slave and
                // master?)", the author, 2026-10-12) - on locomotives THIS HOOK SPAWNED, and on no others:
                // pair                              : every spawned one - how it is joined, and where each shared control stands
                // pair cable <a> <b> [reversed]     : a's rear cable into b's front (reversed: into b's rear - back to back)
                // pair radio <leader> <other> ...   : one radio channel, the first transmitting
                // pair free                         : every cable out, everybody off the radio
                // pair set <car> <control> <0-1>    : throttle reverser brake indbrake dynbrake sander lights_front lights_rear, as that car's own handle would
                TrainCar? spawned(string id) => _spawned.Find(car => car != null && car.ID == id && car.muModule != null);
                DV.Simulation.Controllers.OverridableBaseControl? handle(TrainCar car, string name)
                {
                    DV.Simulation.Cars.BaseControlsOverrider? controls = car.muModule.controlsOverrider;
                    if (controls == null)
                        return null;
                    switch (name)
                    {
                        case "throttle"    : return controls.Throttle;
                        case "reverser"    : return controls.Reverser;
                        case "brake"       : return controls.Brake;
                        case "indbrake"    : return controls.IndependentBrake;
                        case "dynbrake"    : return controls.DynamicBrake;
                        case "sander"      : return controls.Sander;
                        case "lights_front": return controls.HeadlightsFront;
                        case "lights_rear" : return controls.HeadlightsRear;
                        default            : return null;
                    }
                }
                string[] names = ["throttle", "reverser", "brake", "indbrake", "dynbrake", "sander", "lights_front", "lights_rear"];
                if (words.Length >= 4 && words[1] == "cable")
                {
                    TrainCar? first = spawned(words[2]), second = spawned(words[3]);
                    if (first == null || second == null || first == second)
                        return "pair cable <a> <b> [reversed]: both spawned by this hook, both with a multiple-unit module";
                    first.muModule.RearCable.Connect(words.Length > 4 && words[4] == "reversed" ? second.muModule.RearCable : second.muModule.FrontCable);
                }
                else if (words.Length >= 4 && words[1] == "radio")
                {
                    DV.MultipleUnit.MultipleUnitRemoteChannel channel = new();
                    for (int index = 2; index < words.Length; ++index)
                    {
                        TrainCar? member = spawned(words[index]);
                        if (member == null)
                            return "pair radio <leader> <other> ...: all spawned by this hook; no such one as " + words[index];
                        member.muModule.SetRadioChannel(channel, index == 2);
                    }
                }
                else if (words.Length == 2 && words[1] == "free")
                {
                    foreach (TrainCar car in _spawned)
                    {
                        if (car == null || car.muModule == null)
                            continue;
                        if (car.muModule.FrontCable.IsConnected)
                            car.muModule.FrontCable.Disconnect();
                        if (car.muModule.RearCable.IsConnected)
                            car.muModule.RearCable.Disconnect();
                        car.muModule.SetRadioChannel(null, false);
                    }
                }
                else if (words.Length == 5 && words[1] == "set")
                {
                    TrainCar? car = spawned(words[2]);
                    DV.Simulation.Controllers.OverridableBaseControl? control = car == null ? null : handle(car, words[3]);
                    if (control == null)
                        return "pair set <spawned car> <" + string.Join("|", names) + "> <0-1>: no such car, or it has no such control";
                    control.Set(float.Parse(words[4], System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (words.Length != 1)
                    return "pair | pair cable <a> <b> [reversed] | pair radio <leader> <other> ... | pair free | pair set <car> <control> <0-1>";
                System.Text.StringBuilder text = new();
                foreach (TrainCar car in _spawned)
                {
                    if (car == null || car.muModule == null)
                        continue;
                    DV.MultipleUnit.MultipleUnitModule module = car.muModule;
                    DV.Simulation.Controllers.ControlsBlockController? blocks = car.SimController != null ? car.SimController.controlsBlocker : null;
                    int held = 0, locks = 0;
                    if (blocks != null)
                    {
                        foreach (DV.Simulation.Controllers.ControlBlocker one in blocks.GetComponentsInChildren<DV.Simulation.Controllers.ControlBlocker>(true))
                        {
                            ++locks;
                            if (one.MUSlaveBlock)
                                ++held;
                        }
                    }
                    text.Append($"\n    {car.ID} {car.carLivery.id} speed {car.GetForwardSpeed() * 3.6f:F1}: cable front {(module.FrontCable.IsConnected ? module.FrontCable.connectedTo.muModule.train.ID : "-")} rear {(module.RearCable.IsConnected ? module.RearCable.connectedTo.muModule.train.ID : "-")};" +
                        $" radio {(module.UseWireless ? module.IsTransmitter ? "leads" : "follows" : "-")}; held {held} of {locks};");
                    text.Append(car.TryGetComponent(out DV.Simulation.Cars.MultipleUnitStateObserver says) ? $" says {says.CarTemperatureState}, train {says.MUChainTemperatureState}{(says.enabled ? "" : " (not watching)")};" : " says nothing;");
                    foreach (string name in names)
                    {
                        DV.Simulation.Controllers.OverridableBaseControl? control = handle(car, name);
                        text.Append(control == null ? $" {name} none" : $" {name} {control.Value:F3}");
                    }
                }
                return "pair:" + text;
            }
            case "mu":
            {
                // mu                         : how the chosen locomotive is joined to others
                // mu link <car id>           : by radio, the chosen one leading (as the game's wireless controller would: no coupling needed)
                // mu unlink                  : every one of ours off the radio
                // mu set <control> <0-1>     : throttle, reverser, brake, indbrake, sander - as the desk's handle would set it
                eu07_driver? own = chosen();
                if (own == null || own.car == null)
                    return "no EU07";
                DV.MultipleUnit.MultipleUnitModule? module = own.car.muModule;
                if (module == null)
                    return own.car.ID + " has no multiple-unit module";
                if (words.Length == 3 && words[1] == "link")
                {
                    eu07_driver? other = eu07_driver.all.Find(driver => driver != null && driver.car != null && driver.car.ID == words[2]);
                    if (other == null || other.car == null || other.car.muModule == null || other == own)
                        return "mu link <car id>: no such other locomotive of ours with a module";
                    DV.MultipleUnit.MultipleUnitRemoteChannel channel = new();
                    module.SetRadioChannel(channel, true);
                    other.car.muModule.SetRadioChannel(channel, false);
                }
                else if (words.Length == 2 && words[1] == "unlink")
                {
                    foreach (eu07_driver driver in eu07_driver.all)
                    {
                        if (driver != null && driver.car != null && driver.car.muModule != null)
                            driver.car.muModule.SetRadioChannel(null, false);
                    }
                }
                else if (words.Length == 4 && words[1] == "set")
                {
                    var controls = module.controlsOverrider;
                    DV.Simulation.Controllers.OverridableBaseControl? control = null;
                    if (controls != null)
                    {
                        switch (words[2])
                        {
                            case "throttle": control = controls.Throttle; break;
                            case "reverser": control = controls.Reverser; break;
                            case "brake"   : control = controls.Brake; break;
                            case "indbrake": control = controls.IndependentBrake; break;
                            case "sander"  : control = controls.Sander; break;
                        }
                    }
                    if (control == null)
                        return "mu set <throttle|reverser|brake|indbrake|sander> <0-1>";
                    control.Set(float.Parse(words[3], System.Globalization.CultureInfo.InvariantCulture));
                }
                return $"mu {own.car.ID}: cable front {module.ConnectedFront} rear {module.ConnectedRear}; radio {module.UseWireless} leading {module.IsTransmitter}" +
                    (module.controlsOverrider == null ? "; NO controls" : $"; throttle {module.controlsOverrider.Throttle?.Value:F3} reverser {module.controlsOverrider.Reverser?.Value:F2} brake {module.controlsOverrider.Brake?.Value:F2}");
            }
            case "shove":
            {
                // shove <m/s>: the CHOSEN locomotive set rolling at that speed, forward of itself (negative: backward),
                // by the game's own way of giving a vehicle a speed - for trying what only happens on the move with
                // nobody to drive. On locomotives THIS HOOK SPAWNED, and on no others.
                eu07_driver? rolled = chosen();
                if (rolled == null || rolled.car == null || !_spawned.Contains(rolled.car))
                    return "only on a locomotive this hook spawned (choose it first)";
                if (words.Length != 2 || !float.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float speed) || Mathf.Abs(speed) > 15.0f)
                    return "shove <m/s, 15 at most>";
                System.Reflection.MethodInfo? set = HarmonyLib.AccessTools.Method(typeof(TrainCar), "SetForwardSpeed");
                if (set == null)
                    return "the game has no SetForwardSpeed";
                rolled.car.ForceOptimizationState(false);
                set.Invoke(rolled.car, [speed]);
                return $"shoved {rolled.car.ID} at {speed:F1} m/s";
            }
            case "show":
            {
                // show <item prefab name> [up] [turned degrees]: what an item LOOKS like - its shapes alone, nothing
                // that works - hung `up` metres over the middle of the CHOSEN locomotive, its face to the rear, for a
                // camera to look at. (A gadget mounted on a locomotive nobody is near is not drawn at all.)
                // On locomotives THIS HOOK SPAWNED, and on no others.
                eu07_driver? under = chosen();
                if (under == null || under.car == null || !_spawned.Contains(under.car))
                    return "only on a locomotive this hook spawned (choose it first)";
                if (words.Length < 2)
                    return "show <item prefab name> [up] [turned degrees]";
                GameObject? looked_at = Resources.Load(words[1]) as GameObject;
                if (looked_at == null)
                    return "the game has no item called " + words[1];
                float up = words.Length > 2 && float.TryParse(words[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float high) ? high : 6.0f;
                float turned = words.Length > 3 && float.TryParse(words[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float degrees) ? degrees : 0.0f;
                Transform old = under.car.transform.Find("[shown]");
                if (old != null)
                    Destroy(old.gameObject);
                GameObject shown = new("[shown]");
                shown.transform.SetParent(under.car.transform, false);
                shown.transform.localPosition = new Vector3(0.0f, up, 0.0f);
                shown.transform.localRotation = Quaternion.Euler(0.0f, 180.0f + turned, 0.0f);
                int parts = 0;
                foreach (Transform child in looked_at.transform)
                {
                    GameObject copy = Instantiate(child.gameObject, shown.transform, false);
                    foreach (Collider touched in copy.GetComponentsInChildren<Collider>(true))
                        touched.enabled = false;
                    ++parts;
                }
                return $"showing {words[1]} ({parts} parts) {up:F1} m over {under.car.ID}";
            }
            case "trip":
            {
                // trip: as if the CHOSEN locomotive's SHP box had just passed over a track magnet (it must be fed, and
                // the locomotive rolling, as for a real one). On locomotives THIS HOOK SPAWNED, and on no others.
                eu07_driver? tripped = chosen();
                if (tripped == null || tripped.car == null || !_spawned.Contains(tripped.car) || tripped.car.Customization == null)
                    return "only on a locomotive this hook spawned (choose it first)";
                DV.Customization.Gadgets.Implementations.GadgetATS? box = tripped.car.Customization.Customizers
                    .OfType<DV.Customization.Gadgets.Implementations.GadgetATS>().FirstOrDefault(one => one.name.StartsWith(eu07_driver.shp_gadget_name, StringComparison.Ordinal));
                if (box == null)
                    return "no SHP box aboard";
                if (!box.IsActive)
                    return $"the box is not at work: fed {box.PowerState} on {box.IsOn} rolling {!box.IsLocoConsideredStopped}";
                box.currentTimer = -0.0001f;
                return "tripped";
            }
            case "gadget":
            {
                // gadget <item prefab name> [right up forward]: one of the game's gadgets - or a mod's - made, mounted
                // on the instrument cupboard's back board of the CHOSEN locomotive and soldered, as a player would
                // with the item, the drill and the iron; for trying what a gadget does aboard with nobody there.
                // On locomotives THIS HOOK SPAWNED, and on no others.
                eu07_driver? on = chosen();
                if (on == null || on.car == null || !_spawned.Contains(on.car))
                    return "only on a locomotive this hook spawned (choose it first)";
                if (words.Length < 2)
                    return "gadget <item prefab name> [right up forward]";
                GameObject? prefab = Resources.Load(words[1]) as GameObject;
                if (prefab == null)
                    return "the game has no item called " + words[1];
                if (on.car.Customization == null)
                    return "the locomotive takes no gadgets";
                Vector3 at = new(0.18f, 2.03f, 6.62f);          // the right-hand cupboard's back board, about its middle
                if (words.Length == 5 && float.TryParse(words[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float right)
                                      && float.TryParse(words[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float up)
                                      && float.TryParse(words[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float forward))
                    at = new Vector3(right, up, forward);
                StartCoroutine(mount(on.car, prefab, words[1], at));
                return "mounting " + words[1] + " (the outcome follows in this log)";
            }
            case "choose":
            {
                // choose <car id>: the status and the commands are for that one, wherever the player is;
                // choose alone: back to the one he is aboard
                eu07_driver? named = words.Length == 2 ? eu07_driver.all.Find(driver => driver != null && driver.car != null && driver.car.ID == words[1]) : null;
                _named = named;
                if (named == null)
                    return "following the player; there are: " + string.Join(" ", eu07_driver.all.Where(driver => driver != null && driver.car != null).Select(driver => driver.car!.ID));
                return "chosen " + words[1];
            }
            default:
                return chosen()?.command(words) ?? "no EU07";
        }
    }
}
