// THE RAIN'S ROOF (#13, 2026-11-27), after the WE-6F's Rain.cs and the EN43's en43_rain.cs (their R1 6.1 diagnostic).
// DV culls rain particles under the hits of DV.VFX.CeilingDetection: a 34 x 34 grid of rays 3.5 m apart, straight down
// every 1/3 s, against Default | Terrain | Train_Big_Collider. With the camera between four rays up to 1.75 m off each
// axis, a 2.90 m body on track along a world axis is missed by every nearby ray, and the rain falls in the cab and the
// corridor. While the camera is inside a WE-4U's body, every grid point whose nearest-point square overlaps a WE-4U near
// the camera has its hit RAISED to the roof (never lowered: a bridge or a tunnel stays). Runs after
// CeilingDetection.Update's copy, before the particle jobs read copiedResults. The wet look on the floors is the other
// half: the Unity build's dry volumes (eu07_build.cs, DRY INSIDE).

using System;
using System.Linq;
using System.Text;

using DV.Utils;
using DV.VFX;
using UnityEngine;

namespace electric_sim.unit_A;

internal static class eu07_rain
{
    // the body (eu07_build.cs: body_width, roof_top, body_half)
    public const float half_width = 1.45f;
    public const float roof_y = 3.90f;
    public const float half_length = 7.354f;
    private const float inside_margin = 0.10f;
    private const float near_m = 40.0f;
    public static bool roof_on = true;      // debug "rain_roof off": DV's own rays only, to compare

    private static bool inside(TrainCar car, Vector3 eye)
    {
        Vector3 l = car.transform.InverseTransformPoint(eye);
        return Mathf.Abs(l.x) <= half_width + inside_margin && l.y >= 0.80f && l.y <= roof_y + 0.15f && Mathf.Abs(l.z) <= half_length + inside_margin;
    }

    private static System.Collections.Generic.IEnumerable<TrainCar> cars() =>
        eu07_driver.all.Where(driver => driver != null && driver.car != null).Select(driver => driver.car!);

    internal static void patch_ceiling(CeilingDetection inst)
    {
        if (!roof_on || !inst.copiedResults.IsCreated)
            return;
        Camera? camera = PlayerManager.ActiveCamera;
        if (camera == null)
            return;
        Vector3 eye = camera.transform.position;
        if (!cars().Any(car => inside(car, eye)))
            return;
        var grid = inst.worldPositionedArray;
        int nx = grid.arraySize.x, ny = grid.arraySize.y;
        if (nx < 2 || ny < 2 || inst.copiedResults.Length < nx * ny)
            return;
        float sx = (grid.endCorner.x - grid.startCorner.x) / (nx - 1), sz = (grid.endCorner.y - grid.startCorner.y) / (ny - 1);
        float sep = inst.gridSeparation;
        foreach (TrainCar car in cars())
        {
            if ((car.transform.position - eye).sqrMagnitude > near_m * near_m)
                continue;
            Transform t = car.transform;
            Vector3 fwd = t.forward;
            float yaw = Mathf.Atan2(fwd.x, fwd.z);
            float e = 0.5f * sep * (Mathf.Abs(Mathf.Cos(yaw)) + Mathf.Abs(Mathf.Sin(yaw)));    // the nearest-point rule
            float minx = float.MaxValue, maxx = float.MinValue, minz = float.MaxValue, maxz = float.MinValue;
            for (int c = 0; c < 4; ++c)
            {
                Vector3 w = t.TransformPoint((c & 1) == 0 ? -half_width : half_width, 0.0f, (c & 2) == 0 ? -half_length : half_length);
                minx = Mathf.Min(minx, w.x); maxx = Mathf.Max(maxx, w.x); minz = Mathf.Min(minz, w.z); maxz = Mathf.Max(maxz, w.z);
            }
            int i0 = Mathf.Clamp(Mathf.FloorToInt((minx - e - grid.startCorner.x) / sx), 0, nx - 1);
            int i1 = Mathf.Clamp(Mathf.CeilToInt((maxx + e - grid.startCorner.x) / sx), 0, nx - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt((minz - e - grid.startCorner.y) / sz), 0, ny - 1);
            int j1 = Mathf.Clamp(Mathf.CeilToInt((maxz + e - grid.startCorner.y) / sz), 0, ny - 1);
            for (int j = j0; j <= j1; ++j)
                for (int i = i0; i <= i1; ++i)
                {
                    float px = grid.startCorner.x + i * sx, pz = grid.startCorner.y + j * sz;
                    Vector3 local = t.InverseTransformPoint(new Vector3(px, t.position.y, pz));
                    if (Mathf.Abs(local.x) > half_width + e || Mathf.Abs(local.z) > half_length + e)
                        continue;
                    float roof = t.TransformPoint(local.x, roof_y, local.z).y;
                    int k = i + j * nx;
                    RaycastHit hit = inst.copiedResults[k];
                    if (hit.point.y >= roof)
                        continue;
                    hit.point = new Vector3(px, roof, pz);
                    inst.copiedResults[k] = hit;
                }
        }
    }

    // debug "rain_probe": the camera in the nearest WE-4U's frame and the four grid points round it, what each hit and
    // whether that is over the roof (covered) or not (OPEN)
    public static string probe()
    {
        CeilingDetection? detection = SingletonBehaviour<CeilingDetection>.Instance;
        Camera? camera = PlayerManager.ActiveCamera;
        if (detection == null)
            return "no CeilingDetection in this scene";
        if (camera == null)
            return "no active camera";
        if (!detection.copiedResults.IsCreated)
            return "CeilingDetection's results not made yet";
        Vector3 eye = camera.transform.position;
        TrainCar? car = cars().OrderBy(c => Vector3.Distance(c.transform.position, eye)).FirstOrDefault();
        var grid = detection.worldPositionedArray;
        int nx = grid.arraySize.x, ny = grid.arraySize.y;
        if (nx < 2 || ny < 2)
            return "the grid is not set up yet";
        float fx = Mathf.InverseLerp(grid.startCorner.x, grid.endCorner.x, eye.x) * (nx - 1), fz = Mathf.InverseLerp(grid.startCorner.y, grid.endCorner.y, eye.z) * (ny - 1);
        int i0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, nx - 2), j0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, ny - 2);
        var text = new StringBuilder($"mask 0x{detection.layerMask.value:X}, grid {nx} x {ny} at {detection.gridSeparation:F1} m; ");
        text.Append(car != null ? $"camera in {car.ID}'s frame {car.transform.InverseTransformPoint(eye):F2} (inside {inside(car, eye)}); " : $"camera at {eye:F1} (no WE-4U); ");
        for (int dj = 0; dj < 2; ++dj)
            for (int di = 0; di < 2; ++di)
            {
                int i = i0 + di, j = j0 + dj, k = i + j * nx;
                float px = Mathf.Lerp(grid.startCorner.x, grid.endCorner.x, i / (nx - 1.0f)), pz = Mathf.Lerp(grid.startCorner.y, grid.endCorner.y, j / (ny - 1.0f));
                RaycastHit hit = detection.copiedResults[k];
                string what = hit.collider != null ? $"{hit.collider.name}/{LayerMask.LayerToName(hit.collider.gameObject.layer)}" : "nothing";
                string local = car != null ? $"{car.transform.InverseTransformPoint(new Vector3(px, car.transform.position.y, pz)):F2}" : $"({px:F1}, {pz:F1})";
                float roof = car != null ? car.transform.TransformPoint(0.0f, roof_y, 0.0f).y : float.NaN;
                text.Append($"point {local} hit {what} y {hit.point.y:F2}" + (car != null ? $" roof {roof:F2} {(hit.point.y >= roof - 0.05f ? "covered" : "OPEN")}" : "") + "; ");
            }
        return text.ToString();
    }

    // debug "rain <0-1> | off": the weather's rain and wetness overridden (both: rain alone leaves surfaces dry a while)
    public static string force(string amount)
    {
        DV.WeatherSystem.WeatherDriver? weather = SingletonBehaviour<DV.WeatherSystem.WeatherDriver>.Instance;
        if (weather == null)
            return "no weather driver";
        if (amount == "off")
        {
            weather.RainValue.ClearOverride();
            weather.WetnessValue.ClearOverride();
            return $"rain and wetness back to the weather's: rain {weather.RainValue.CurrentValue:F2}, wetness {weather.WetnessValue.CurrentValue:F2}";
        }
        float value = Mathf.Clamp01(float.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
        weather.RainValue.EngageOverride(value);
        weather.WetnessValue.EngageOverride(value);
        return $"rain and wetness held at {value:F2} (rain off before quitting)";
    }
}

[HarmonyLib.HarmonyPatch(typeof(CeilingDetection), "Update")]
internal static class eu07_ceiling_patch
{
    private static bool _failed;

    private static void Postfix(CeilingDetection __instance)
    {
        if (_failed)
            return;
        try
        {
            eu07_rain.patch_ceiling(__instance);
        }
        catch (Exception problem)
        {
            _failed = true;          // once: the patch stands down, DV's own roof stays
            Main.release_log("EU07 rain: the ceiling patch failed and stands down: " + problem.Message);
        }
    }
}
