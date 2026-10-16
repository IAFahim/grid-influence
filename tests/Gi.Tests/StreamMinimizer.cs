using System.Globalization;
using System.Text;

namespace Gi.Tests;

internal static class StreamMinimizer
{
    public static List<StreamCommand> Minimize(StreamSetup setup, List<StreamCommand> commands, BugInjection? bug)
    {
        var current = new List<StreamCommand>(commands);
        var first = StreamRunner.TryRun(setup, current, bug);
        if (first == null) return current;

        if (first.CommandIndex >= 0 && first.CommandIndex + 1 < current.Count)
            current = current.GetRange(0, first.CommandIndex + 1);

        bool Fails(List<StreamCommand> candidate)
            => candidate.Count > 0 && StreamRunner.TryRun(setup, candidate, bug) != null;

        var granularity = 2;
        while (current.Count > 1)
        {
            var chunk = (current.Count + granularity - 1) / granularity;
            var reduced = false;
            for (var start = 0; start < current.Count && !reduced; start += chunk)
            {
                var end = Math.Min(start + chunk, current.Count);
                var candidate = new List<StreamCommand>(current.Count - (end - start));
                for (var i = 0; i < start; i++) candidate.Add(current[i]);
                for (var i = end; i < current.Count; i++) candidate.Add(current[i]);
                if (!Fails(candidate)) continue;
                current = candidate;
                reduced = true;
                granularity = Math.Max(2, granularity - 1);
            }

            if (reduced) continue;
            if (granularity >= current.Count) break;
            granularity = Math.Min(current.Count, granularity * 2);
        }

        return current;
    }

    public static string PrintAsCSharpTest(StreamSetup setup, List<StreamCommand> commands)
    {
        var text = new StringBuilder();
        text.Append("// seed ").Append(setup.Seed.ToString(CultureInfo.InvariantCulture))
            .Append(" — ").Append(commands.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" commands");
        text.AppendLine("[Fact]");
        text.AppendLine("public void MinimizedDifferentialFailure()");
        text.AppendLine("{");
        text.Append("    var setup = new StreamSetup { Seed = ")
            .Append(setup.Seed.ToString(CultureInfo.InvariantCulture)).AppendLine(",");

        text.AppendLine("        Grids = new[]");
        text.AppendLine("        {");
        foreach (var grid in setup.Grids)
        {
            text.Append("            new GridSpec { Power = ").Append(grid.Power.ToString(CultureInfo.InvariantCulture))
                .Append(", X = ").Append(FloatLiteral(grid.X))
                .Append(", Y = ").Append(FloatLiteral(grid.Y))
                .Append(", Size = ").Append(FloatLiteral(grid.Size))
                .AppendLine(" },");
        }

        text.AppendLine("        },");
        text.AppendLine("        Layers = new[]");
        text.AppendLine("        {");
        foreach (var layer in setup.Layers)
        {
            text.Append("            new LayerSpec { ");
            if (layer.Derived)
            {
                text.Append("Derived = true, Op = LayerOp.").Append(layer.Op)
                    .Append(", A = ").Append(layer.A.ToString(CultureInfo.InvariantCulture))
                    .Append(", B = ").Append(layer.B.ToString(CultureInfo.InvariantCulture));
                if (layer.Op == LayerOp.Sum)
                    text.Append(", WeightA = ").Append(layer.WeightA.ToString(CultureInfo.InvariantCulture))
                        .Append(", WeightB = ").Append(layer.WeightB.ToString(CultureInfo.InvariantCulture))
                        .Append(", Shift = ").Append(layer.Shift.ToString(CultureInfo.InvariantCulture));
                if (layer.Op == LayerOp.Mask)
                    text.Append(", Low = ").Append(layer.Low.ToString(CultureInfo.InvariantCulture))
                        .Append(", High = ").Append(layer.High.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append("Precision = ").Append(PrecisionLiteral(layer.Precision));
            }

            text.AppendLine(" },");
        }

        text.AppendLine("        },");
        text.AppendLine("        Stamps = new[]");
        text.AppendLine("        {");
        foreach (var stamp in setup.Stamps)
        {
            text.Append("            new StampSpec { Kind = StampKind.").Append(stamp.Kind);
            if (stamp.Kind == StampKind.Raster)
            {
                text.Append(", Width = ").Append(stamp.Width.ToString(CultureInfo.InvariantCulture))
                    .Append(", Height = ").Append(stamp.Height.ToString(CultureInfo.InvariantCulture))
                    .Append(", DataSeed = ").Append(stamp.DataSeed.ToString(CultureInfo.InvariantCulture)).Append("ul");
            }
            else if (stamp.Kind is StampKind.Box or StampKind.Tent or StampKind.Bell)
            {
                text.Append(", Width = ").Append(stamp.Width.ToString(CultureInfo.InvariantCulture))
                    .Append(", Height = ").Append(stamp.Height.ToString(CultureInfo.InvariantCulture))
                    .Append(", Value = ").Append(stamp.Value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(", Radius = ").Append(stamp.Radius.ToString(CultureInfo.InvariantCulture))
                    .Append(", Arc = ").Append(stamp.Arc.ToString(CultureInfo.InvariantCulture))
                    .Append(", Value = ").Append(stamp.Value.ToString(CultureInfo.InvariantCulture));
            }

            text.AppendLine(" },");
        }

        text.AppendLine("        },");
        text.AppendLine("        Commands = new List<StreamCommand>");
        text.AppendLine("        {");
        foreach (var command in commands)
        {
            text.Append("            new StreamCommand { Op = StreamOp.").Append(command.Op);
            if (command.Op == StreamOp.Read) text.Append(", Read = ReadKind.").Append(command.Read);
            if (command.Layer != 0) text.Append(", Layer = ").Append(command.Layer.ToString(CultureInfo.InvariantCulture));
            if (command.Grid != 0) text.Append(", Grid = ").Append(command.Grid.ToString(CultureInfo.InvariantCulture));
            if (command.Stamp != 0) text.Append(", Stamp = ").Append(command.Stamp.ToString(CultureInfo.InvariantCulture));
            if (command.Handle != 0) text.Append(", Handle = ").Append(command.Handle.ToString(CultureInfo.InvariantCulture));
            if (command.Gain != 0) text.Append(", Gain = ").Append(command.Gain.ToString(CultureInfo.InvariantCulture));
            if (command.Int0 != 0) text.Append(", Int0 = ").Append(command.Int0.ToString(CultureInfo.InvariantCulture));
            if (command.X != 0f) text.Append(", X = ").Append(FloatLiteral(command.X));
            if (command.Y != 0f) text.Append(", Y = ").Append(FloatLiteral(command.Y));
            if (command.Aux != 0) text.Append(", Aux = ").Append(command.Aux.ToString(CultureInfo.InvariantCulture)).Append("ul");
            text.AppendLine(" },");
        }

        text.AppendLine("        },");
        text.AppendLine("    };");
        text.AppendLine("    Assert.Null(StreamRunner.TryRun(setup, setup.Commands));");
        text.AppendLine("}");
        return text.ToString();
    }

    private static string PrecisionLiteral(Layer.Precision precision)
    {
        if (precision.Equals(Layer.Precision.Exact)) return "Layer.Precision.Exact";
        if (precision.Equals(Layer.Precision.Half)) return "Layer.Precision.Half";
        if (precision.Equals(Layer.Precision.Quarter)) return "Layer.Precision.Quarter";
        if (precision.Equals(Layer.Precision.Cell)) return "Layer.Precision.Cell";
        return $"new Layer.Precision({precision.PhaseBits}, {precision.TurnBits})";
    }

    private static string FloatLiteral(float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        var literal = bits < 0 ? $"unchecked((int)0x{bits.ToString("X8", CultureInfo.InvariantCulture)})" : $"0x{bits.ToString("X8", CultureInfo.InvariantCulture)}";
        return $"BitConverter.Int32BitsToSingle({literal})";
    }
}
