var mode = args.Length > 0 ? args[0] : "";
if (mode == "--place") { Verification.PlaceProfile(); return 0; }
if (mode == "--daily") { Verification.Daily(); return 0; }
var result = Verification.Run();
if (mode == "--timing") Verification.Timing();
if (mode == "--compare") Verification.Compare();
if (mode == "--crowd") Verification.CrowdTiming();
return result;
