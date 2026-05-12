using UnityEngine;

namespace MeteoriteSPH3D
{
    public static class MeteoriteSPH3DBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Object.FindObjectOfType<MeteoriteSPH3DController>() != null) return;
            GameObject go = new GameObject("Meteorite SPH 3D Demo");
            go.AddComponent<MeteoriteSPH3DController>();
        }
    }
}
