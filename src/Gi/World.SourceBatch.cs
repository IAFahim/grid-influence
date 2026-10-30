using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static BatchStatus ApplyBatch(byte world, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        var context = GetContext(world);
        return context == null ? BatchStatus.InvalidWorld : ApplyBatchCommands(context, commands, results);
    }

    [MethodImpl(Jit.Hot)]
    private static BatchStatus ApplyBatchCommands(
        WorldCtx* context, ReadOnlySpan<SourceCommand> commands, Span<SourceResult> results)
    {
        if (commands.Length > MaxSourceSlots) return BatchStatus.TooManyCommands;
        if (results.Length < commands.Length) return BatchStatus.InsufficientResults;
        if (MemoryMarshal.AsBytes(commands).Overlaps(MemoryMarshal.AsBytes(results[..commands.Length])))
            return BatchStatus.OverlappingBuffers;

        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            if (command.Kind < SourceCommandKind.Place || command.Kind > SourceCommandKind.Expire)
                return BatchStatus.InvalidCommand;
            if (command.Kind == SourceCommandKind.Place || !command.Target.Pending) continue;

            var creationIndex = command.Target.Command;
            if ((uint)creationIndex >= (uint)index || commands[creationIndex].Kind != SourceCommandKind.Place)
                return BatchStatus.InvalidReference;
        }

        for (var index = 0; index < commands.Length; index++) ApplyCommand(context, in commands[index], results, index);
        return BatchStatus.Applied;
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyCommand(WorldCtx* w, in SourceCommand command, Span<SourceResult> results, int index)
    {
        var s = &w->Sources;
        if (command.Kind == SourceCommandKind.Place)
        {
            var scale = float.IsFinite(command.ScaleValue) ? QuantizeScale(command.ScaleValue) : UnitScale;
            var status = PlaceSlot(w, command.Layer, command.PositionX, command.PositionY, command.Stamp, command.Gain,
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
                WriteTransform(w, s, slot, in command);
                WriteGain(w, s, slot, command.Gain);
                break;
            case SourceCommandKind.UpdateTransform:
                WriteTransform(w, s, slot, in command);
                break;
            case SourceCommandKind.Move:
                if (float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY))
                    WriteMove(w, s, slot, command.PositionX, command.PositionY);
                break;
            case SourceCommandKind.Turn:
                if (float.IsFinite(command.HeadingRadians))
                    WriteTurn(w, s, slot, QuantizeAngle(command.HeadingRadians));
                break;
            case SourceCommandKind.Scale:
                if (float.IsFinite(command.ScaleValue)) WriteScale(w, s, slot, QuantizeScale(command.ScaleValue));
                break;
            case SourceCommandKind.SetGain:
                WriteGain(w, s, slot, command.Gain);
                break;
            case SourceCommandKind.Remove:
                WriteRemove(w, s, slot);
                break;
            case SourceCommandKind.Fade:
                WriteFade(w, s, slot, command.Gain, command.Ticks);
                break;
            case SourceCommandKind.Expire:
                WriteExpire(w, s, slot, command.Ticks);
                break;
        }

        results[index] = new SourceResult(identifier, SourceResultStatus.Applied);
    }

    [MethodImpl(Jit.Hot)]
    private static void WriteTransform(WorldCtx* w, SourceColumns* s, int slot, in SourceCommand command)
    {
        if (float.IsFinite(command.PositionX) && float.IsFinite(command.PositionY))
            WriteMove(w, s, slot, command.PositionX, command.PositionY);
        if (float.IsFinite(command.HeadingRadians)) WriteTurn(w, s, slot, QuantizeAngle(command.HeadingRadians));
        if (float.IsFinite(command.ScaleValue)) WriteScale(w, s, slot, QuantizeScale(command.ScaleValue));
    }
}
