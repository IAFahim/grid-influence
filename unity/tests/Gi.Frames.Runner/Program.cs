var code = Gi.Frames.GiFrames.Run(args, out var output, out var error);
Console.Out.Write(output);
Console.Error.Write(error);
return code;
