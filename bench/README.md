# Bench

Runs the EU07's circuit, contactors and motor on Knsgf's real compiler and solver,
without the game running.

The bench is compiled into the mod's Debug build, so it tests the code that ships:

1. Build `mod/dv_eu07.csproj` in Debug (see `mod/README.md`).
2. Build `bench/runner` and run it:
   `runner.exe mod/bin/Debug/dv_eu07.dll <game>/DerailValley_Data/Managed`

It exits 0 and ends `ALL PASSED` when every check holds.
