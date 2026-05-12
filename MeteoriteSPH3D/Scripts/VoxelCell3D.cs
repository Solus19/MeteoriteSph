using UnityEngine;

namespace MeteoriteSPH3D
{
    [System.Serializable]
    public struct VoxelCell3D
    {
        public bool solid;
        public float temperature;
        public float pressure;
        public float damage;
        public bool deposited;

        public void ClearFields(float cooling)
        {
            pressure = Mathf.Max(0f, pressure - cooling);
            temperature = Mathf.Max(0f, temperature - cooling);
            damage = Mathf.Clamp01(damage);
        }
    }
}
