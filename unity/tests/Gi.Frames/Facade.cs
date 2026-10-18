using System.Text;

namespace Gi.Frames;

public static class GiFrames
{
    public static int Run(string[] args, out string standardOutput, out string standardError)
    {
        var output = Console.Out;
        var error = Console.Error;
        var capturedOutput = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var capturedError = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        Console.SetOut(capturedOutput);
        Console.SetError(capturedError);
        ReadMode.Fused = false;
        ReadMode.View = false;
        ReadMode.Transient = false;
        try
        {
            return FrameHarness.Run(args);
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            standardOutput = capturedOutput.ToString();
            standardError = capturedError.ToString();
        }
    }
}
