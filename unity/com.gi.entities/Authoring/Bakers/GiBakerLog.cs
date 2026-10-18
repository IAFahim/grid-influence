using System.Collections.Generic;
using UnityEngine;

namespace Gi.Entities.Authoring
{
    internal static class GiBakerLog
    {
        public static void Errors(Object context, string what, List<string> errors)
        {
            foreach (var error in errors) Debug.LogError("Gi " + what + " '" + context.name + "': " + error, context);
        }
    }
}
