if (args.Length > 0 && args[0] == "--frames") return FrameHarness.Run(args);
var mode = args.Length > 0 ? args[0] : "";
if (mode == "--floor") return FloorProbes.Run(args);
if (mode == "--place") { Verification.PlaceProfile(); return 0; }
if (mode == "--daily") { Verification.Daily(); return 0; }
if (mode == "--batch") return Verification.SourceBatchTiming();
if (mode == "--sync") return Verification.SyncTransformTiming();
if (mode == "--stages") return Verification.StageTiming();
if (mode == "--sweep") return Verification.SweepProfile();
if (mode == "--view") return ViewTiming.Run();
if (mode == "--splat") return SplatTiming.Run();
var result = Verification.Run();
if (mode is "" or "--verify")
{
    result |= FloorProbes.Receipts();
    result |= FrameReceipts.Run(Console.Out) == 0 ? 0 : 1;
}
if (mode == "--timing") Verification.Timing();
if (mode == "--compare") Verification.Compare();
if (mode == "--crowd") Verification.CrowdTiming();
return result;
