using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static BatchStatus ApplyBatch(byte world, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        fixed (GiRoot* root = &Runtime.Root) return ApplyBatch(root, world, commands, results);
    }
    [MethodImpl(Jit.Hot)]
    internal static BatchStatus ApplyBatch(GiRoot* root, byte world, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var context = GetContext(root, world);
        return context == null ? BatchStatus.InvalidWorld : ApplyBatchCommands(root, context, commands, results);
    }

    [MethodImpl(Jit.Hot)]
    private static BatchStatus ApplyBatchCommands(GiRoot* root,
        WorldCtx* context, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        if (commands.Length > MaxSourceSlots) return BatchStatus.TooManyCommands;
        if (results.Length < commands.Length) return BatchStatus.InsufficientResults;
        if (MemoryMarshal.AsBytes(commands).Overlaps(MemoryMarshal.AsBytes(results[..commands.Length])))
            return BatchStatus.OverlappingBuffers;

        var directMoves = true;
        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            if (command.Kind < SourceCommandKind.Place || command.Kind > SourceCommandKind.Expire)
                return BatchStatus.InvalidCommand;
            if (command.Kind != SourceCommandKind.Move || command.Target.Pending) directMoves = false;
            if (command.Kind == SourceCommandKind.Place || !command.Target.Pending) continue;

            var creationIndex = command.Target.Command;
            if ((uint)creationIndex >= (uint)index || commands[creationIndex].Kind != SourceCommandKind.Place)
                return BatchStatus.InvalidReference;
        }

        if (directMoves)
        {
            ApplyMovesDirect(root, context, commands, results);
            return BatchStatus.Applied;
        }

        for (var index = 0; index < commands.Length; index++) ApplyCommand(root, context, in commands[index], results, index);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyMovesDirect(GiRoot* root, WorldCtx* w, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var s = &w->Sources;
        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            var identifier = command.Target.Source;
            var slot = (int)identifier.Slot;
            if (identifier.Generation == 0 || (uint)slot >= (uint)s->Count || s->Alive[slot] == 0 ||
                identifier.Generation != s->Gen[slot])
            {
                results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
                continue;
            }

            if (float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY))
                WriteMove(root, w, s, slot, command.PositionX, command.PositionY);
            results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyCommand(GiRoot* root, WorldCtx* w, in SourceCommand command, Span<SourceResult> results, int index)
    {
        var s = &w->Sources;
        if (command.Kind == SourceCommandKind.Place)
        {
            var scale = float.IsFinite(command.ScaleValue) ? QuantizeScale(command.ScaleValue) : UnitScale;
            var status = PlaceSlot(root, w, command.Layer, command.PositionX, command.PositionY, command.Stamp, command.Gain,
                scale, out var created);
            results[index] = new SourceResult(created, status);
            if (status != SourceResultStatus.Applied) return;

            var placed = (int)created.Slot;
            if (float.IsFinite(command.HeadingRadians)) s->Angle[placed] = QuantizeAngle(command.HeadingRadians);
            return;
        }

        var identifier = command.Target.Pending ? results[command.Target.Command].SourceId : command.Target.Source;
        var slot = (int)identifier.Slot;
        if (identifier.Generation == 0 || (uint)slot >= (uint)s->Count || s->Alive[slot] == 0 ||
            identifier.Generation != s->Gen[slot])
        {
            results[index] = new SourceResult(default, SourceResultStatus.InvalidSource);
            return;
        }

        switch (command.Kind)
        {
            case SourceCommandKind.Update:
                WriteTransform(root, w, s, slot, in command);
                WriteGain(root, w, s, slot, command.Gain);
                break;
            case SourceCommandKind.UpdateTransform:
                WriteTransform(root, w, s, slot, in command);
                break;
            case SourceCommandKind.Move:
                if (float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY))
                    WriteMove(root, w, s, slot, command.PositionX, command.PositionY);
                break;
            case SourceCommandKind.Turn:
                if (float.IsFinite(command.HeadingRadians))
                    WriteTurn(root, w, s, slot, QuantizeAngle(command.HeadingRadians));
                break;
            case SourceCommandKind.Scale:
                if (float.IsFinite(command.ScaleValue)) WriteScale(root, w, s, slot, QuantizeScale(command.ScaleValue));
                break;
            case SourceCommandKind.SetGain:
                WriteGain(root, w, s, slot, command.Gain);
                break;
            case SourceCommandKind.Remove:
                WriteRemove(root, w, s, slot);
                break;
            case SourceCommandKind.Fade:
                WriteFade(root, w, s, slot, command.Gain, command.Ticks);
                break;
            case SourceCommandKind.Expire:
                WriteExpire(root, w, s, slot, command.Ticks);
                break;
        }

        results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
    }

    [MethodImpl(Jit.Hot)]
    private static void WriteTransform(GiRoot* root, WorldCtx* w, SourceColumns* s, int slot, in SourceCommand command)
    {
        if (float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY))
            WriteMove(root, w, s, slot, command.PositionX, command.PositionY);
        if (float.IsFinite(command.HeadingRadians)) WriteTurn(root, w, s, slot, QuantizeAngle(command.HeadingRadians));
        if (float.IsFinite(command.ScaleValue)) WriteScale(root, w, s, slot, QuantizeScale(command.ScaleValue));
    }
}
