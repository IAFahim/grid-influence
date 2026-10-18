using System;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities
{
    [NoAutoStaticsCleanup]
    public static class GiRecorderGate
    {
        private static readonly GiRecorder[] Active = new GiRecorder[256];
        private static readonly GiRecorder[] Retained = new GiRecorder[256];
        private static int _attached;

        public static bool Enabled => _attached != 0;

        public static void Attach(GiRecorder recorder)
        {
            var world = recorder.World;
            if (Active[world] == null) _attached++;
            Active[world] = recorder;
            Retained[world] = null;
        }

        public static GiRecorder Detach(byte world)
        {
            var recorder = Active[world];
            if (recorder == null) return null;

            Active[world] = null;
            Retained[world] = recorder;
            _attached--;
            return recorder;
        }

        public static void Forget(byte world)
        {
            Detach(world);
            Retained[world] = null;
        }

        public static GiRecorder Find(byte world)
        {
            return Active[world] ?? Retained[world];
        }

        public static bool IsRecording(byte world)
        {
            return Active[world] != null;
        }

        public static void Commands(byte world, ReadOnlySpan<GiCommand> commands, ReadOnlySpan<GiCommandResult> results)
        {
            Active[world]?.OnCommands(commands, results);
        }

        public static unsafe void Sync(byte world, Gi.World.SourceId* sources, float* xs, float* ys, float* headings, float* scales, int* categoryStart,
            int* categoryCount)
        {
            Active[world]?.OnSync(sources, xs, ys, headings, scales, categoryStart, categoryCount);
        }

        public static unsafe void Splat(byte world, int* layerStart, int* layerCount, float* xs, float* ys, byte* stamps, sbyte* gains)
        {
            Active[world]?.OnSplat(layerStart, layerCount, xs, ys, stamps, gains);
        }

        public static void Processed(byte world)
        {
            Active[world]?.OnProcessed();
        }
    }
}
