# The Unity project

Only `Assets/Editor/eu07_build.cs` is in this repository. The rest of the project is
other people's and comes from their releases:

1. Unity **2019.4.40** (or .41), activated with a licence. Open it once by hand and sign
   in: batch mode cannot activate.
2. Unpack `example-railcar.zip` from
   [Knsgf/CCL-OCS v1.0.0](https://github.com/Knsgf/CCL-OCS/releases/tag/v1.0.0) into this
   folder. It is a whole project: Custom Car Loader's creator tools (`Assets/CarCreator`),
   his catenary add-on (`Assets/CatenaryAddon`) and his example railcar
   (`Assets/_CCL_CARS/WE4PT`), which the EU07 is cut from.
3. Export with `Unity.exe -projectPath unity -executeMethod eu07_build.run -eu07out <folder>`.
   The paint textures are generated and not in this repository.

The game also needs his **CatenaryForCCL** mod (same release) installed beside Custom
Car Loader: the pantographs and the bus bar are its components.
