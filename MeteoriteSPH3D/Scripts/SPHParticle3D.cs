using UnityEngine;

namespace MeteoriteSPH3D
{
    [System.Serializable]
    public sealed class SPHParticle3D
    {
        public Vector3 position;
        public Vector3 velocity;
        public float density;
        public float nearDensity;
        public float pressure;
        public float temperature;
        public float mass;
        public float age;
        public float recentGroundContact;
        public bool active;
        // GPU buffer slot for async readback/deactivation. -1 means CPU-only particle.
        public int gpuIndex;

        public SPHParticle3D(Vector3 position, Vector3 velocity, float temperature, float mass)
        {
            this.position = position;
            this.velocity = velocity;
            this.temperature = temperature;
            this.mass = mass;
            density = 0f;
            nearDensity = 0f;
            pressure = 0f;
            age = 0f;
            recentGroundContact = 0f;
            active = true;
            gpuIndex = -1;
        }
    }
}
