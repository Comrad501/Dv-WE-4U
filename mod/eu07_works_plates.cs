// works plates' number line: car id + year, brass, game's Barlow
// plates drawn by tools/decals.py, line left blank; position must match its NUMBER_LINE

using System;
using System.Linq;

using TMPro;
using UnityEngine;

namespace electric_sim.unit_A;

internal static class eu07_works_plates
{
    /// <summary>Plate year, same for all.</summary>
    public const string year = "1975";

    /// <summary>Number line per plate: left, top, right, bottom; 0..1 from top left.</summary>
    private static readonly (string prefix, float x0, float y0, float x1, float y1)[] lines =
    {
        ("Decal_works_oval", 0.27f, 0.735f, 0.73f, 0.850f),
        ("Decal_works_plate", 0.42f, 0.67f, 0.94f, 0.87f),
    };

    private static readonly Color brass = new(176 / 255.0f, 140 / 255.0f, 64 / 255.0f);
    private static TMP_FontAsset? _font;
    private static Shader? _lit;

    public static string text(TrainCar car) => $"NR {car.ID}  ·  {year}";

    /// <summary>Writes (or rewrites) the number on each works plate.</summary>
    public static int number(TrainCar car)
    {
        _font ??= Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(found => found.name.StartsWith("Barlow", StringComparison.OrdinalIgnoreCase))
                  ?? TMP_Settings.defaultFontAsset;
        int written = 0;
        foreach (MeshFilter plate in car.GetComponentsInChildren<MeshFilter>(true))
        {
            var line = lines.FirstOrDefault(each => plate.name.StartsWith(each.prefix, StringComparison.Ordinal));
            if (line.prefix == null || plate.sharedMesh == null)
                continue;
            Transform old = plate.transform.Find("WorksNumber");
            if (old != null)
                UnityEngine.Object.Destroy(old.gameObject);

            // plate in car frame: centre, outward normal (thin axis), width, height
            Transform frame = car.transform;
            Bounds own = plate.sharedMesh.bounds;
            Vector3 centre = frame.InverseTransformPoint(plate.transform.TransformPoint(own.center));
            Vector3 size = frame.InverseTransformVector(plate.transform.TransformVector(own.size));
            size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            Vector3 outward = size.x < size.z ? new Vector3(Mathf.Sign(centre.x), 0.0f, 0.0f) : new Vector3(0.0f, 0.0f, Mathf.Sign(centre.z));
            Vector3 up = Vector3.up;
            Vector3 right = Vector3.Cross(up, -outward);                    // reader's right
            float wide = Mathf.Abs(Vector3.Dot(size, right)), tall = size.y;

            float across = ((line.x0 + line.x1) / 2.0f - 0.5f) * wide;
            float down = ((line.y0 + line.y1) / 2.0f - 0.5f) * tall;
            Vector3 at = centre + right * across - up * down + outward * 0.0012f;

            GameObject made = new GameObject("WorksNumber");
            made.transform.SetParent(frame, false);
            made.transform.localPosition = at;
            made.transform.localRotation = Quaternion.LookRotation(-outward, up);
            made.transform.SetParent(plate.transform, true);
            TextMeshPro words = made.AddComponent<TextMeshPro>();
            words.font = _font;
            words.text = text(car);
            words.color = brass;
            words.alignment = TextAlignmentOptions.Center;
            words.enableWordWrapping = false;
            words.enableAutoSizing = true;
            words.fontSizeMin = 0.01f;
            words.fontSizeMax = 10.0f;
            words.characterSpacing = 6.0f;
            words.fontStyle = FontStyles.Bold;
            words.rectTransform.sizeDelta = new Vector2((line.x1 - line.x0) * wide, (line.y1 - line.y0) * tall);
            // surface-shaded TMP: lit like the plate, no glow at dusk
            _lit ??= Resources.FindObjectsOfTypeAll<Shader>().FirstOrDefault(shader => shader.name.Contains("Distance Field (Surface)"));
            if (_lit != null)
            {
                Material lit = new Material(words.fontSharedMaterial) { shader = _lit };
                lit.SetFloat("_Glossiness", 0.45f);
                words.fontSharedMaterial = lit;
            }
            ++written;
        }
        return written;
    }
}
