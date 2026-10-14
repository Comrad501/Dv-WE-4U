# The EU07 model

The locomotive's outside, and the desk's right-hand group, built from dimensions in Blender.
Both are in the game build: `unity/Assets/Editor/eu07_build.cs` copies the FBX files in.

| file | what it is |
|---|---|
| `eu07_blockout.fbx` | the body: hull, cab windows, sides, cab ends, roof gear, underframe |
| `eu07_desk_parts.fbx`, `.json` | the controller's wheel and pedestal, the reverser's key, the field lever, the driver's brake valve; and each part as plain solids |

Measured against the DTR, all eight agree to within 5 mm:

| | mm |
|---|---|
| over buffers | 16 315 |
| width | 2 903 |
| height | 3 901.5 |
| height, pantograph lowered | 4 343 |
| pivots | 8 550 |
| wheelbase | 3 050 |
| outermost axles | 11 600 |
| wheel diameter | 1 250 |

## Where the shapes come from

Principal dimensions: the 303E DTR. The hull's shape and the place and size of
what is on it were MEASURED on a private reference, the 303E of the MaSzyna simulator, set
beside this model in one scene and cut the same way. That simulator's licence keeps its files
to the simulator, so nothing of it is here - no mesh, no texture, no vertex list: only
dimensions, from which everything is built as geometry of our own. What the
reference only paints (ribs, doors, grilles) is shallow relief here, its depth a guess.

The first block-out was checked against komptech.pl's "EU 07 PKP" (Sketchfab, CC BY 4.0) as a
shape reference; no mesh of it was used either.

The two sides are not mirror images: the left side is the right side turned end for end.
Cab A is +Y in Blender and the game's front.
