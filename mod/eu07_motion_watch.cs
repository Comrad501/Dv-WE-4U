// EU07 for Derail Valley - a watcher for cars that rock back and forth (debug hook: "watch").
//
// "locos and cars for some reason oscillate back and fourth, detaching the body from the functioning model.
// make a watcher, which polls velocity and youll see the weird oscillation" (the author, 2026-10-14, with a
// picture of the cab's cocks standing off their pipes).
//
// What the game does that this looks at (read in it): a car's INTERIOR - the cab, and with it the external
// interactables, our desk, cupboards and cocks - is a separate object, not a child of the car. A component on
// the car (TrainCarInteriorPhysics) puts it where the car is in every LateUpdate, but ONLY while the game counts
// the car as moving. Whether it is moving is decided by an ECS system (TrainCarPositionMonitorSystem): a car that
// has stayed within 2 mm (1 cm with no interior loaded) of where it last moved, for a second, is STATIONARY and
// eligible for sleep - and once it is both, the system stops looking at it until something wakes it. A body that
// goes on creeping or rocking after that leaves its interior where it was.
//
// So this records, every physics step and every frame, for each car of the train: the speed along the car, how
// far it has gone, how far its interior stands from where the body is, the game's two flags, and its brakes
// (pipe and cylinder pressure, handbrake): a train whose brakes keep changing never stands still enough to freeze. Read-only: it
// moves nothing, presses nothing, and may watch the player's own train. Written to debug/motion_trace.csv.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

using UnityEngine;

namespace electric_sim.unit_A;

internal class eu07_motion_watch : MonoBehaviour
{
    private static readonly FieldInfo? _interior_field = typeof(TrainCarInteriorPhysics).GetField("interior", BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly List<TrainCar> _cars = new();
    private readonly Dictionary<TrainCar, Vector3> _start = new();
    private readonly StringBuilder _lines = new();
    private string _path = "";
    private float _until;
    private int _rows;

    public static string start(string folder, TrainCar around, float seconds)
    {
        GameObject host = new("[EU07 motion watch]");
        DontDestroyOnLoad(host);
        eu07_motion_watch watch = host.AddComponent<eu07_motion_watch>();
        IEnumerable<TrainCar> cars = around.trainset != null ? around.trainset.cars : new List<TrainCar> { around };
        watch._cars.AddRange(cars.Where(car => car != null));
        foreach (TrainCar car in watch._cars)
            watch._start[car] = car.transform.position;
        watch._until = Time.time + seconds;
        watch._path = Path.Combine(folder, "motion_trace.csv");
        File.WriteAllText(watch._path, "time,step,car,v_along_mps,speed_mps,angular_rps,moved_along_m,interior_off_m,interior_off_along_m,stationary,sleep_eligible,interior_follows,interpolation,pipe_bar,cylinder_bar,handbrake\n");
        return $"watching {watch._cars.Count} car(s) around {around.ID} for {seconds:F0} s into {watch._path}";
    }

    private Transform? interior_of(TrainCar car)
    {
        if (car.loadedInterior != null)
            return car.loadedInterior.transform.parent;
        if (car.loadedExternalInteractables != null)
            return car.loadedExternalInteractables.transform.parent;
        TrainCarInteriorPhysics? physics = car.GetComponent<TrainCarInteriorPhysics>();
        return physics != null ? _interior_field?.GetValue(physics) as Transform : null;
    }

    private void sample(string step)
    {
        foreach (TrainCar car in _cars)
        {
            if (car == null || car.rb == null)
                continue;
            Transform body = car.transform;
            Vector3 v = car.rb.velocity;
            float along = Vector3.Dot(v, body.forward);
            float moved = Vector3.Dot(body.position - _start[car], body.forward);
            Transform? interior = interior_of(car);
            Vector3 off = interior != null ? interior.position - body.position : Vector3.zero;
            TrainCarInteriorPhysics? follows = car.GetComponent<TrainCarInteriorPhysics>();
            _lines.Append(FormattableString.Invariant(
                $"{Time.time:F4},{step},{car.ID},{along:F5},{v.magnitude:F5},{car.rb.angularVelocity.magnitude:F5},{moved:F4},{(interior != null ? off.magnitude : -1.0f):F4},{Vector3.Dot(off, body.forward):F4},{(car.isStationary ? 1 : 0)},{(car.isEligibleForSleep ? 1 : 0)},{(follows != null && follows.enabled ? 1 : 0)},{car.rb.interpolation},") + FormattableString.Invariant(
                $"{(car.brakeSystem != null ? car.brakeSystem.brakePipePressure : -1.0f):F4},{(car.brakeSystem != null ? car.brakeSystem.brakeCylinderPressure : -1.0f):F4},{(car.brakeSystem != null ? car.brakeSystem.handbrakePosition : -1.0f):F2}\n"));
            ++_rows;
        }
        if (_lines.Length > 64 * 1024)
            flush();
    }

    private void flush()
    {
        try
        {
            File.AppendAllText(_path, _lines.ToString());
        }
        catch (Exception)
        {
        }
        _lines.Clear();
    }

    private void FixedUpdate()
    {
        if (Time.time > _until)
        {
            flush();
            Destroy(gameObject);
            return;
        }
        sample("physics");
    }

    // after the game's own LateUpdate has put the interior where it puts it: what is drawn
    private void LateUpdate() => sample("frame");

    private void OnDestroy() => flush();
}
