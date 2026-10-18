namespace Gi.Entities
{
    public static class GiWriteBack
    {
        public static GiFaultCode Fold(in GiCommand command, in GiCommandResult result, ref GiApplied applied)
        {
            switch (command.Kind)
            {
                case GiCommandKind.Place:
                    if (result.Code != GiFaultCode.None) return result.Code;
                    applied.Placed = true;
                    applied.Source = result.Source;
                    applied.Layer = command.LayerKey;
                    applied.Stamp = command.StampKey;
                    applied.Gain = command.TargetGain;
                    return GiFaultCode.None;
                case GiCommandKind.Remove:
                    if (result.Code != GiFaultCode.None && result.Code != GiFaultCode.InvalidSource) return result.Code;
                    applied.Placed = false;
                    applied.Source = default;
                    return GiFaultCode.None;
                case GiCommandKind.SetGain:
                case GiCommandKind.Fade:
                    if (result.Code != GiFaultCode.None) return result.Code;
                    applied.Gain = command.TargetGain;
                    return GiFaultCode.None;
                default:
                    return result.Code;
            }
        }
    }
}
