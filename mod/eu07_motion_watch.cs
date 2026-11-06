// debug "watch": motion recorder for rocking cars -> debug/motion_trace.csv (read-only)
// interior follows the car only while moving (TrainCarInteriorPhysics, LateUpdate)
// stationary + sleep: within 2 mm (1 cm, no interior) for 1 s; then unchecked, interior left behind

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

    // after the game's LateUpdate: what is drawn
    private void LateUpdate() => sample("frame");

    private void OnDestroy() => flush();
}
