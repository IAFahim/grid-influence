var mode = args.Length > 0 ? args[0] : "";
var result = Verification.Run();
if (mode == "--timing") Verification.Timing();
if (mode == "--compare") Verification.Compare();
return result;
