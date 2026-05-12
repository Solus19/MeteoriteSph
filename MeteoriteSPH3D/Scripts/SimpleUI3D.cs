using UnityEngine;

namespace MeteoriteSPH3D
{
    // Menu intentionally disabled. Kept as a stub so old scenes referencing SimpleUI3D still compile.
    public sealed class SimpleUI3D : MonoBehaviour
    {
        public static bool PointerOverOpenMenu { get { return false; } }
        public void Initialize(MeteoriteSPH3DController controller) { }
    }
}
