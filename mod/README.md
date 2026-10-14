# The mod

`dv_eu07.dll`, a Unity Mod Manager mod: it gives every Custom Car Loader vehicle whose
livery id begins with `EU07` its electrics.

- `Main.cs` - entry and the car spawner hook
- `eu07_driver.cs` - one locomotive's power circuit bound to the prefab's ports; the
  keypad controls and the on-screen panel of the crude version
- `../eu07_*.cs` - the circuit, contactors, motor and power circuit themselves
- `knsgf/` - the circuit compiler and solver from Knsgf's
  [Derail_Valley_electric_concept](https://github.com/Knsgf/Derail_Valley_electric_concept)
  (`WE6SIM/`, commit 3c40c05), unchanged, under CC0: see `knsgf/LICENSE_CC0.txt`

Build with the .NET 10 SDK after writing `Directory.Build.props` here:

    <Project><PropertyGroup>
      <GameManaged>...\Derail Valley\DerailValley_Data\Managed</GameManaged>
    </PropertyGroup></Project>

Debug carries the bench; Release is what goes in `Mods/EU07Electrics/` with `Info.json`.
