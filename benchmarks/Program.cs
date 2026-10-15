var mode = args.Length > 0 ? args[0] : "";
if (mode == "--budget") return BudgetVerification.Run(args);
if (mode == "--place") { Verification.PlaceProfile(); return 0; }
if (mode == "--daily") { Verification.Daily(); return 0; }
if (mode == "--batch") return Verification.SourceBatchTiming();
if (mode == "--sync") return Verification.SyncTransformTiming();
if (mode == "--stages") return Verification.StageTiming();
var result = Verification.Run();
if (mode == "--timing") Verification.Timing();
if (mode == "--compare") Verification.Compare();
if (mode == "--crowd") Verification.CrowdTiming();
return result;
