using UnityEngine;

namespace Gi.Entities
{
    internal static class GiLog
    {
        public static void Error(string message)
        {
            Debug.unityLogger.Log(LogType.Error, (object)message);
        }
    }
}
