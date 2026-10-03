using System.Globalization;

namespace Gi.Stats;

internal sealed class Options
{
    public string Command = "stats";
    public bool Json = true;
    public bool Help;
    public int Power = 8;
    public int Layers = 3;
    public int Sources = 4000;
    public int StampSize = 16;
    public bool Raster;
    public int Iterations = 1000;
    public int Queries = 10000;
    public int Moves = 200;

    public static bool TryParse(string[] args, out Options result, out string error)
    {
        result = new Options();
        error = "";
        var hasCommand = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h") { result.Help = true; continue; }
            if (arg == "--json") { result.Json = true; continue; }
            if (arg is "stats" or "memory" or "profile" or "verify")
            {
                if (hasCommand) { error = "Specify one command."; return false; }
                result.Command = arg;
                hasCommand = true;
                continue;
            }

            if (arg is not ("--power" or "--layers" or "--sources" or "--stamp-size" or "--stamp" or
                "--iterations" or "--queries" or "--moves" or "--format"))
            {
                error = $"Unknown command or option: {arg}";
                return false;
            }
            if (++i == args.Length) { error = $"Missing value for {arg}."; return false; }
            var value = args[i];
            if (arg == "--format")
            {
                if (value is not ("human" or "json")) { error = "Format must be human or json."; return false; }
                result.Json = value == "json";
                continue;
            }
            if (arg == "--stamp")
            {
                if (value is not ("box" or "raster")) { error = "Stamp must be box or raster."; return false; }
                result.Raster = value == "raster";
                continue;
            }
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                error = $"Expected an unsigned integer for {arg}.";
                return false;
            }
            switch (arg)
            {
                case "--power": result.Power = number; break;
                case "--layers": result.Layers = number; break;
                case "--sources": result.Sources = number; break;
                case "--stamp-size": result.StampSize = number; break;
                case "--iterations": result.Iterations = number; break;
                case "--queries": result.Queries = number; break;
                case "--moves": result.Moves = number; break;
            }
        }

        if (result.Power is < 5 or > 14) error = "Power must be 5..14.";
        else if (result.Layers is < 1 or > 32) error = "Layers must be 1..32.";
        else if (result.Sources is < 0 or > 1_000_000) error = "Sources must be 0..1000000.";
        else if (result.StampSize is < 1 or > 256) error = "Stamp size must be 1..256.";
        else if (result.Raster && result.StampSize == 1) error = "Raster stamp size must be at least 2.";
        else if (result.Iterations is < 1 or > 1_000_000) error = "Iterations must be 1..1000000.";
        else if (result.Queries is < 1 or > 10_000_000) error = "Queries must be 1..10000000.";
        else if (result.Moves < 0) error = "Moves must be nonnegative.";
        return error.Length == 0;
    }
}
