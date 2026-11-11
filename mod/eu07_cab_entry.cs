// enter the cab by looking at its windows and pressing the teleport key (F), as on the stock locos.
// The game finds a "[cab]" with a CabTeleportDestination at spawn; the vehicle has none, so each
// cab gets one here: a trigger box round the cab's window band (a little wider than the body, so
// a look from outside meets it before the body's own collider) on the layer of a stock loco's [cab],
// and the destination itself standing behind that cab's desk, facing forward.
using System.Linq;
using UnityEngine;

namespace electric_sim.unit_A;

internal static class eu07_cab_entry
{
    // car frame (eu07_build.cs: cab_back 5.20, cab_front 7.25, floor_top 1.66, desk_z 6.199);
    // cab A at +z, cab B its mirror through the middle
    private static readonly Vector3 stand_a = new Vector3(0.35f, 1.72f, 6.0f);
    private static readonly Vector3 box_min = new Vector3(-1.60f, 1.90f, 5.20f);
    private static readonly Vector3 box_max = new Vector3( 1.60f, 3.90f, 7.45f);

    public static void attach(TrainCar car)
    {
        Transform parent = car.interior != null ? car.interior : car.transform;
        int layer = stock_cab_layer();
        if (layer < 0)
        {
            Debug.Log($"EU07 {car.ID}: no stock cab to copy the layer from; cab entry left out");
            return;
        }
        CabTeleportDestination? a = make(car, parent, "[cab]", stand_a, Quaternion.identity, layer);
        make(car, parent, "[cab B]", new Vector3(-stand_a.x, stand_a.y, -stand_a.z), Quaternion.Euler(0.0f, 180.0f, 0.0f), layer);
        if (car.cabTeleportDestination == null && a != null)
            car.cabTeleportDestination = a;            // the game's own "into the cab" (cab A) uses it too
    }

    // the layer the game's own cab triggers are on, read off a stock loco's prefab: the pointer's
    // ray mask is set in the game's prefab, and Teleport_Destination (the first guess) is not it
    private static int _stock_layer = int.MinValue;
    private static GameObject? _stock_highlight;           // the stock cab's glow, copied onto ours
    private static int stock_cab_layer()
    {
        if (_stock_layer != int.MinValue)
            return _stock_layer;
        _stock_layer = -1;
        foreach (DV.ThingTypes.TrainCarLivery livery in DV.Globals.G.Types.Liveries)
        {
            Transform? cab = livery.prefab != null ? livery.prefab.transform.Find("[cab]") : null;
            Collider? trigger = cab != null ? cab.GetComponentsInChildren<Collider>(true).FirstOrDefault(c => c.isTrigger) : null;
            if (trigger != null)
            {
                _stock_layer = trigger.gameObject.layer;
                DV.TeleportHoverGlow? glow = cab!.GetComponentInChildren<DV.TeleportHoverGlow>(true);
                _stock_highlight = glow != null ? glow.highlight : null;
                Debug.Log($"EU07: cab entry on layer {_stock_layer} ({LayerMask.LayerToName(_stock_layer)}), as {livery.id}'s [cab]");
                break;
            }
        }
        return _stock_layer;
    }

    private static CabTeleportDestination? make(TrainCar car, Transform parent, string name, Vector3 stand, Quaternion facing, int layer)
    {
        if (parent.Find(name) != null)
            return parent.Find(name).GetComponent<CabTeleportDestination>();
        GameObject go = new GameObject(name);
        // built inactive: TeleportHoverGlow destroys itself in Awake unless its highlight is set first
        go.SetActive(false);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = stand;
        go.transform.localRotation = facing;
        // the window band, in this object's frame (cab B's box is cab A's turned with it)
        Vector3 centre = (box_min + box_max) * 0.5f - stand_a;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.center = centre;
        box.size = box_max - box_min;
        CabTeleportDestination destination = go.AddComponent<CabTeleportDestination>();
        // the characteristic glow: the stock cab's own highlight, copied to the middle of the window band
        if (_stock_highlight != null)
        {
            GameObject highlight = Object.Instantiate(_stock_highlight, go.transform, false);
            highlight.name = "highlight";
            highlight.transform.localPosition = centre;
            highlight.SetActive(false);
            DV.TeleportHoverGlow glow = go.AddComponent<DV.TeleportHoverGlow>();
            glow.highlight = highlight;
            destination.hoverGlow = glow;
        }
        // on last: Awake resolves the car from where it now hangs, and finds its highlight set
        go.SetActive(true);
        return destination;
    }
}
