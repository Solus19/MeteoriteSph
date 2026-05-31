using UnityEngine;

namespace MeteoriteSPH3D
{
    public static class MeteoriteSPH3DBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (FindExistingController() != null) return;
            GameObject go = new GameObject("Meteorite SPH 3D Demo");
            go.AddComponent<MeteoriteSPH3DController>();
        }

        private static MeteoriteSPH3DController FindExistingController()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<MeteoriteSPH3DController>();
#else
            return UnityEngine.Object.FindObjectOfType<MeteoriteSPH3DController>();
#endif
        }
    }
}
