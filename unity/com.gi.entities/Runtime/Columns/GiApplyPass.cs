using System;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static class GiApplyPass
    {
        public const int BatchLimit = 1 << 24;

        public static GiApplyStatus Apply(ref GiWorld.Token token, byte world, ReadOnlySpan<GiCommand> commands,
            Span<GiCommandResult> results, Span<GiWorld.SourceCommand> scratch, Span<GiWorld.SourceResult> batchResults,
            int limit)
        {
            var status = GiApplyStatus.Applied;
            var start = 0;
            while (start < commands.Length)
            {
                var stop = SliceEnd(commands, start, limit);
                var sliced = ApplySlice(ref token, world, commands, results, scratch, batchResults, start, stop);
                if (sliced != GiApplyStatus.Applied) status = sliced;
                start = stop;
            }

            return status;
        }

        public static int SliceEnd(ReadOnlySpan<GiCommand> commands, int start, int limit)
        {
            var target = (int)Math.Min(commands.Length, (long)start + Math.Max(limit, 1));
            var stop = target;
            while (stop > start && stop < commands.Length && commands[stop].Target >= 0) stop--;
            if (stop > start) return stop;

            stop = target;
            while (stop < commands.Length && commands[stop].Target >= 0) stop++;
            return stop;
        }

        private static GiApplyStatus ApplySlice(ref GiWorld.Token token, byte world, ReadOnlySpan<GiCommand> commands,
            Span<GiCommandResult> results, Span<GiWorld.SourceCommand> scratch, Span<GiWorld.SourceResult> batchResults,
            int start, int stop)
        {
            var count = stop - start;
            for (var i = 0; i < count; i++) scratch[i] = Convert(commands[start + i], start);

            var batch = GiWorld.ApplyBatch(token, scratch.Slice(0, count), batchResults.Slice(0, count));
            if (batch == GiWorld.BatchStatus.StaleRevision)
            {
                token = GiWorld.Capture(world);
                batch = GiWorld.ApplyBatch(token, scratch.Slice(0, count), batchResults.Slice(0, count));
            }

            if (batch != GiWorld.BatchStatus.Applied)
            {
                var code = batch == GiWorld.BatchStatus.StaleRevision ? GiFaultCode.StaleSource : GiFaultCode.Unknown;
                for (var i = 0; i < count; i++) results[start + i] = new GiCommandResult { Code = code };
                return batch == GiWorld.BatchStatus.StaleRevision ? GiApplyStatus.Stale : GiApplyStatus.Adapter;
            }

            for (var i = 0; i < count; i++)
                results[start + i] = new GiCommandResult { Code = Map(batchResults[i].Status), Source = batchResults[i].SourceId };
            return GiApplyStatus.Applied;
        }

        private static GiWorld.SourceTarget TargetOf(in GiCommand command, int start)
        {
            return command.Target >= 0
                ? GiWorld.SourceTarget.CreatedBy(command.Target - start)
                : GiWorld.SourceTarget.Existing(command.Source);
        }

        private static GiWorld.SourceCommand Convert(in GiCommand command, int start)
        {
            switch (command.Kind)
            {
                case GiCommandKind.Place:
                    return GiWorld.SourceCommand.Place(command.Layer, command.X, command.Y, command.Stamp, command.Gain,
                        command.Heading, command.Scale);
                case GiCommandKind.Remove:
                    return GiWorld.SourceCommand.Remove(TargetOf(command, start));
                case GiCommandKind.SetGain:
                    return GiWorld.SourceCommand.SetGain(TargetOf(command, start), command.Gain);
                case GiCommandKind.Fade:
                    return GiWorld.SourceCommand.Fade(TargetOf(command, start), command.Gain, command.Ticks);
                default:
                    return GiWorld.SourceCommand.Expire(TargetOf(command, start), command.Ticks);
            }
        }

        private static GiFaultCode Map(GiWorld.SourceResultStatus status)
        {
            switch (status)
            {
                case GiWorld.SourceResultStatus.Applied:
                    return GiFaultCode.None;
                case GiWorld.SourceResultStatus.InvalidSource:
                    return GiFaultCode.InvalidSource;
                case GiWorld.SourceResultStatus.InvalidLayer:
                    return GiFaultCode.InvalidLayer;
                case GiWorld.SourceResultStatus.InvalidStamp:
                    return GiFaultCode.InvalidStamp;
                case GiWorld.SourceResultStatus.SourceLimitReached:
                    return GiFaultCode.SourceLimit;
                default:
                    return GiFaultCode.Unknown;
            }
        }
    }
}
