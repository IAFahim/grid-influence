var timing = args.Length > 0 && args[0] == "--timing";
var result = Verification.Run();
if (timing) Verification.Timing();
return result;
