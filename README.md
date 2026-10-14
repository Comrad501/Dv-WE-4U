# WE-4U (EU07) for Derail Valley

A Polish EU07 (303E) electric locomotive for Derail Valley, as the WE-4U: a 1500 V DC machine with the
real one's main circuit - 44 controller notches over a starting rheostat, series and parallel, six
field-weakening steps - its desk, lamps and switchgear, a machine room you can walk through, and its own
sounds. **Indev:** it drives, and it is far from finished.

It is built on [Knsgf's 1500 V DC catenary and electric simulation](https://github.com/Knsgf/Derail_Valley_electric_concept)
(CC0 / MIT) and his [electric vehicle add-on for Custom Car Loader](https://github.com/Knsgf/CCL-OCS) (MIT).

## Install

Needs, besides Unity Mod Manager (0.27.3 or later):

| mod | version tried | where |
|---|---|---|
| Custom Car Loader | 3.1.10 | [Nexus Mods](https://www.nexusmods.com/derailvalley/mods/324) |
| DV Language Helper (Custom Car Loader needs it) | 1.2.1 | with Custom Car Loader |
| 1500 V DC catenary | 2.5.3 | [Knsgf/Derail_Valley_electric_concept](https://github.com/Knsgf/Derail_Valley_electric_concept) |
| Custom Car Loader Catenary Support | 1.0.0 | [Knsgf/CCL-OCS](https://github.com/Knsgf/CCL-OCS/releases/tag/v1.0.0) |

Put both folders of the release, `EU07` and `EU07Electrics`, into the game's `Mods` folder. The
locomotive is the **WE-4U** in the game's spawners and shops; it runs under the wire.

Its vigilance device is the game's own: mount one in the cupboard under the desk (the doors open) and
the desk's yellow button answers it, or a key: keypad Enter, changed on the mod's page in the mod manager (Ctrl+F10).

## Known issues

- It is an indev build: controls, ports and saved state may change, and a save made with it may not
  load cleanly in a later one. Keep a save without it.
- Cab B has no desk yet; drive from cab A. No seats.
- The bogies and the pantographs are still flat colours.
- The SHP train stop (and its lamp in the box on the sill) comes with the signalling mod, which is not
  released yet.
- The starting resistors are designed, not measured: no document with their ohms was found. Notch on
  the ammeter - and wait for the needle before the last notches in series and in parallel; take them
  too soon and the overload relays go.
- Standing trains can rock back and forth for minutes. That is the game's own physics (a stock DH4
  does it too).

## What is here

| | |
|---|---|
| `mod/` | the game's side: a Unity Mod Manager mod that gives a spawned WE-4U its electrics, sounds and protections. Carries Knsgf's circuit compiler and solver (CC0) |
| `unity/` | the editor script that builds the vehicle from Knsgf's example railcar and exports it through Custom Car Loader, no window needed (`unity/README.md`) |
| `model/` | the body, bogies, pantographs and desk parts as FBX, and the dial faces, finishes and plates |
| `audio/` | the sounds: the clips, and the table of what each follows |
| `contactor_programme.json`, `eu07_contactors.cs` | which contactor is closed on each notch and field step, and the tables made from it |
| `eu07_traction_circuit.cs` | the main circuit, as a diagram for Knsgf's compiler |
| `starting_resistors.json` | the starting resistors, designed from the wiring, the motor and the documentation's figures |
| `eu07_*.cs` | the motor, the power circuit with its breakers, relays and auxiliaries, the 110 V system, the automatic notching |
| `bench/` | runs all of it on Knsgf's real compiler and solver, outside the game |

## Status

- Wiring of the main circuit and the full contactor programme: done, and cross-checked against each other.
- Component values: starting resistors designed from the real wiring, the motor fitted to the documentation and its mean starting effort, relays and fan ratings, scaled for 1500 kW on 1500 V; field shunts worked out from the documentation's percentages.
- The C# compiles against Knsgf's simulation and runs on his solver on a bench: all 44 controller positions and 7 field steps, and the motor reproduces the documented continuous rating at full and at minimum field to within about 2%.
- The fast breaker, relays and meters are in, and on the bench a driver starts 600 t and 2000 t trains with it without tripping anything.
- In the game it is a vehicle of its own (the WE-4U): a modelled body with a machine room one can walk through, a desk in cab A with the real one's lamps, buttons and switches, working doors, wipers, horn and sanding, and its own sounds.
- Besides the traction circuit: a 110 V battery with its switch and breakers under a flap in the assistant's desk, pantograph air with the small compressor and three-way cock in the machine room, heat in the starting resistors and the motors, a last-mile traction battery (not on the real locomotive).
- Running on the resistors has a limit, by the ammeter and the clock: the grids heat with the square of the motors' current (on the desk's ammeters about 5 minutes at 750 A and 10 at 600 A from cold; 450 A can be held). The fault lamp is steady from 400 C and blinks from 600 C, where they wear the drivetrain's health; at 800 C one goes: an earth fault, and a fifth of that health. The figures are ours, not an instruction's.
- Behind one of the game's own locomotives (its multiple-unit cable with this cab cut out, or its radio) the throttle that arrives is taken as how much is wanted, and the wheel and the field lever are worked by themselves: notch by notch under a current limit, resting only on the running notches, shunting there, easing off as the grids warm. The real locomotive has no such thing.
- To the other units of a train she says what the game's locomotives say to one another, a temperature that lights every desk's motor-temperature lamp (steady from 90, blinking from 105): her motors' heat, and "critical" when power is asked of her and she cannot give any - overload relays out, main breaker open, earth fault - unless the hauled-run switch is up; also her starting resistors, warm or red hot.
- There is NO on-screen panel and there are no hidden keys: what the locomotive has to say, its desk, lamps and gauges say. (A developer gets a status panel, keypad keys and a file-based command hook by making a `debug` folder in the mod's folder; keypad 3 shows the panel.)
- It is painted: a livery of its own, deep blue with a darker band and pale yellow lines, lettered WE-4U; worn as delivered, and the game's paint cans give it fresh paint, rust, or primer to repaint over. The machine room is shaded and dusted, and its plates and signs say what things are.
- Vigilance: the game's own device, mounted in the cupboard under the desk, answered from the desk, with its lamp and buzzer in the box on the sill and its own valve and cut-off cock. The SHP train stop is answered the same way and comes with the signalling mod.
- Not done: cab B's desk; the bogies and the pantographs are still flat colours.

## The paint

Nothing is drawn by hand and nothing is anyone else's. The textures are generated and large, so they are not in the repository; the release's vehicle bundle carries them.

## Sources and rights

The circuit facts were read from the 303E schematics. Those drawings are not in this
repository and will not be. The locomotive design itself dates from the 1960s.

## Credits

- **Knsgf** - the catenary, the electric simulation and its circuit compiler, which this is built on.
- The starting resistors' ohms are in no document found; they are designed here
  (`starting_resistors.json`). An earlier set derived from a MaSzyna physics
  file was replaced on 2026-10-14: MaSzyna's licence asks consent of a file's author for any use
  outside the simulator.
- Motor ratings, field-weakening percentages, gear ratio and tractive effort figures are from the
  303E operating documentation; winding resistances from CTK Bydgoszcz.

## Licence

MIT, see `LICENSE`. Deliberately permissive: the ET41 (203E) is two of these joined, and
anyone who wants to build one from this is welcome to.
