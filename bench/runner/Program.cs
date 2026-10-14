// Loads a built electric_sim.dll and runs eu07_bench in it.
//
//     runner.exe <path to electric_sim.dll> <path to Derail Valley's Managed folder>
//
// The DLL is the mod, so it wants the game's assemblies; they are found where the game
// keeps them, nothing is copied.

using System;
using System.IO;
using System.Reflection;

internal static class Program
{
    private static int Main(string[] arguments)
    {
        if (arguments.Length < 2)
        {
            Console.Error.WriteLine("usage: runner <electric_sim.dll> <DerailValley_Data/Managed>");
            return 2;
        }
        string sim = Path.GetFullPath(arguments[0]), managed = Path.GetFullPath(arguments[1]);
        string[] places = [Path.GetDirectoryName(sim)!, managed, Path.Combine(managed, "UnityModManager")];
        AppDomain.CurrentDomain.AssemblyResolve += (_, wanted) =>
        {
            string file = new AssemblyName(wanted.Name).Name + ".dll";
            foreach (string place in places)
            {
                string path = Path.Combine(place, file);
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            MethodInfo run = Assembly.LoadFrom(sim).GetType("electric_sim.unit_A.eu07_bench", throwOnError: true)!.GetMethod("run")!;
            string[] rest = new string[arguments.Length - 2];
            Array.Copy(arguments, 2, rest, 0, rest.Length);
            return (int) run.Invoke(null, [rest])!;
        }
        catch (TargetInvocationException failure)
        {
            Console.Error.WriteLine(failure.InnerException);
            return 1;
        }
    }
}
