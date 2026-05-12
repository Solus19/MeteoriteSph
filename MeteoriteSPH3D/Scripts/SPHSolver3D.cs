using System.Collections.Generic;
using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class SPHSolver3D
    {
        private readonly SpatialHash3D hash = new SpatialHash3D();
        private readonly List<int> neighbours = new List<int>(128);

        public void Step(List<SPHParticle3D> particles, VoxelTerrain3D terrain, MeteoriteSPH3DController c, float dt)
        {
            hash.Rebuild(particles, c.smoothingRadius);
            ComputeDensityPressure(particles, c);
            Integrate(particles, terrain, c, dt);
        }

        private void ComputeDensityPressure(List<SPHParticle3D> particles, MeteoriteSPH3DController c)
        {
            float h = Mathf.Max(0.001f, c.smoothingRadius);
            float h2 = h * h;
            float spacing = Mathf.Max(c.particleSpacing, c.particleRadius * 2.05f);

            for (int i = 0; i < particles.Count; i++)
            {
                SPHParticle3D p = particles[i];
                if (p == null || !p.active) continue;

                hash.Query(p.position, neighbours);
                float density = c.particleMass;
                float nearDensity = 0f;

                for (int n = 0; n < neighbours.Count; n++)
                {
                    int j = neighbours[n];
                    SPHParticle3D q = particles[j];
                    if (q == null || !q.active) continue;

                    Vector3 d = p.position - q.position;
                    float r2 = d.sqrMagnitude;
                    if (r2 > h2) continue;

                    float r = Mathf.Sqrt(Mathf.Max(0f, r2));
                    float qn = Mathf.Clamp01(1f - r / h);
                    density += q.mass * qn * qn * qn;

                    float sn = Mathf.Clamp01(1f - r / spacing);
                    nearDensity += q.mass * sn * sn * (2f - sn);
                }

                p.density = Mathf.Clamp(density, c.minDensity, c.maxDensity);
                p.nearDensity = nearDensity;
                p.pressure = Mathf.Max(0f, c.gasConstant * (p.density - c.restDensity));
            }
        }

        private void Integrate(List<SPHParticle3D> particles, VoxelTerrain3D terrain, MeteoriteSPH3DController c, float dt)
        {
            float h = Mathf.Max(0.001f, c.smoothingRadius);
            float h2 = h * h;
            float spacing = Mathf.Max(c.particleSpacing, c.particleRadius * 2.05f);

            for (int i = 0; i < particles.Count; i++)
            {
                SPHParticle3D p = particles[i];
                if (p == null || !p.active) continue;

                p.age += dt;
                p.recentGroundContact = Mathf.Max(0f, p.recentGroundContact - dt);

                bool semiSolid = c.useViscoPlasticEjecta && p.temperature <= c.semiSolidTemperature;
                float gravityMul = semiSolid ? c.semiSolidGravityMultiplier : 1f;
                Vector3 acceleration = Vector3.down * c.gravity * gravityMul;

                hash.Query(p.position, neighbours);
                for (int n = 0; n < neighbours.Count; n++)
                {
                    int j = neighbours[n];
                    if (j == i) continue;

                    SPHParticle3D q = particles[j];
                    if (q == null || !q.active) continue;

                    Vector3 delta = p.position - q.position;
                    float r2 = delta.sqrMagnitude;
                    if (r2 < 0.0000001f || r2 > h2) continue;

                    float r = Mathf.Sqrt(r2);
                    Vector3 dir = delta / r;
                    float qn = Mathf.Clamp01(1f - r / h);

                    float pressurePair = 0.5f * (p.pressure + q.pressure);
                    acceleration += dir * (c.pressureStrength * pressurePair * qn * qn / Mathf.Max(c.minDensity, q.density));

                    float nearPair = 0.5f * (p.nearDensity + q.nearDensity);
                    float nearKernelWide = qn * qn * qn;
                    acceleration += dir * (c.nearPressureStrength * nearPair * nearKernelWide / Mathf.Max(c.minDensity, q.density));

                    float sn = Mathf.Clamp01(1f - r / spacing);
                    if (sn > 0f)
                    {
                        float nearKernelContact = sn * sn * (2f - sn);
                        acceleration += dir * (c.nearPressureStrength * 0.55f * nearPair * nearKernelContact / Mathf.Max(c.minDensity, q.density));
                    }
                    else
                    {
                        float denom = Mathf.Max(0.0001f, h - spacing);
                        float ct = Mathf.Clamp01((r - spacing) / denom);
                        float cohesionKernel = ct * (1f - ct) * 4f;
                        acceleration -= dir * (c.cohesionStrength * cohesionKernel * q.mass / Mathf.Max(c.minDensity, q.density));
                    }

                    float pairViscosity = c.viscosity;
                    float temp01 = Mathf.InverseLerp(c.coldViscosityTemperature, c.hotViscosityTemperature, 0.5f * (p.temperature + q.temperature));
                    pairViscosity *= Mathf.Lerp(c.coldViscosityMultiplier, 1f, temp01);
                    if (semiSolid || q.temperature <= c.semiSolidTemperature)
                    {
                        pairViscosity *= c.semiSolidViscosityMultiplier;
                    }

                    acceleration += (q.velocity - p.velocity) * (pairViscosity * qn / Mathf.Max(1f, q.density));
                }

                float accLen = acceleration.magnitude;
                if (accLen > c.maxAcceleration)
                {
                    acceleration = acceleration / accLen * c.maxAcceleration;
                }

                p.velocity += acceleration * dt;

                Vector3 xsph = Vector3.zero;
                float xsphW = 0f;
                hash.Query(p.position, neighbours);
                for (int n2 = 0; n2 < neighbours.Count; n2++)
                {
                    int j2 = neighbours[n2];
                    if (j2 == i) continue;
                    SPHParticle3D q2 = particles[j2];
                    if (q2 == null || !q2.active) continue;
                    float rr = Vector3.Distance(q2.position, p.position);
                    if (rr <= 0.0001f || rr >= h) continue;
                    float w = Mathf.Clamp01(1f - rr / h);
                    xsph += (q2.velocity - p.velocity) * w;
                    xsphW += w;
                }
                if (xsphW > 0.0001f)
                {
                    p.velocity += (xsph / xsphW) * Mathf.Clamp01(c.xsphVelocityBlend) * Mathf.Clamp01(dt * 8f);
                }

                p.velocity *= Mathf.Pow(c.damping, dt * 60f);

                if (semiSolid)
                {
                    p.velocity *= Mathf.Lerp(1f, c.semiSolidVelocityDamping, dt * 10f);
                }

                float speed = p.velocity.magnitude;
                if (speed > c.maxVelocity)
                {
                    p.velocity = p.velocity / speed * c.maxVelocity;
                }

                p.position += p.velocity * dt;
                p.temperature = Mathf.Max(0f, p.temperature - c.coolingRate * dt);

                ResolveBoundsAndTerrain(p, terrain, c, dt);
            }
        }

        private void ResolveBoundsAndTerrain(SPHParticle3D p, VoxelTerrain3D terrain, MeteoriteSPH3DController c, float dt)
        {
            float r = c.particleRadius;
            float w = terrain.WorldWidth;
            float h = terrain.WorldHeight + c.extraWorldHeight;
            float d = terrain.WorldDepth;

            if (p.position.x < r)
            {
                p.position.x = r;
                if (p.velocity.x < 0f) p.velocity.x = 0f;
            }
            if (p.position.x > w - r)
            {
                p.position.x = w - r;
                if (p.velocity.x > 0f) p.velocity.x = 0f;
            }
            if (p.position.z < r)
            {
                p.position.z = r;
                if (p.velocity.z < 0f) p.velocity.z = 0f;
            }
            if (p.position.z > d - r)
            {
                p.position.z = d - r;
                if (p.velocity.z > 0f) p.velocity.z = 0f;
            }
            if (p.position.y < r)
            {
                p.position.y = r;
                if (p.velocity.y < 0f) p.velocity.y = 0f;
                ApplyGroundLoss(p, Vector3.up, c, dt);
            }
            if (p.position.y > h)
            {
                p.position.y = h;
                if (p.velocity.y > 0f) p.velocity.y = 0f;
            }

            Vector3Int min = terrain.WorldToCell(p.position - Vector3.one * (r + terrain.CellSize));
            Vector3Int max = terrain.WorldToCell(p.position + Vector3.one * (r + terrain.CellSize));

            for (int y = min.y; y <= max.y; y++)
            {
                for (int z = min.z; z <= max.z; z++)
                {
                    for (int x = min.x; x <= max.x; x++)
                    {
                        if (!terrain.IsSolid(x, y, z)) continue;

                        Vector3 boxMin = new Vector3(x * terrain.CellSize, y * terrain.CellSize, z * terrain.CellSize);
                        Vector3 boxMax = boxMin + Vector3.one * terrain.CellSize;
                        Vector3 closest = new Vector3(
                            Mathf.Clamp(p.position.x, boxMin.x, boxMax.x),
                            Mathf.Clamp(p.position.y, boxMin.y, boxMax.y),
                            Mathf.Clamp(p.position.z, boxMin.z, boxMax.z));

                        Vector3 delta = p.position - closest;
                        float dist2 = delta.sqrMagnitude;
                        float r2 = r * r;
                        if (dist2 >= r2) continue;

                        Vector3 normal;
                        float penetration;

                        if (dist2 > 0.0000001f)
                        {
                            float dist = Mathf.Sqrt(dist2);
                            normal = delta / dist;
                            penetration = r - dist;
                        }
                        else
                        {
                            Vector3 center = (boxMin + boxMax) * 0.5f;
                            Vector3 local = p.position - center;
                            Vector3 abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                            if (abs.x >= abs.y && abs.x >= abs.z) normal = new Vector3(Mathf.Sign(local.x == 0f ? 1f : local.x), 0f, 0f);
                            else if (abs.y >= abs.x && abs.y >= abs.z) normal = new Vector3(0f, Mathf.Sign(local.y == 0f ? 1f : local.y), 0f);
                            else normal = new Vector3(0f, 0f, Mathf.Sign(local.z == 0f ? 1f : local.z));
                            penetration = r;
                        }

                        p.position += normal * (penetration + 0.0005f);

                        float vn = Vector3.Dot(p.velocity, normal);
                        if (vn < 0f)
                        {
                            Vector3 normalVelocity = normal * vn;
                            Vector3 tangentVelocity = p.velocity - normalVelocity;

                            if (normal.y > 0.35f)
                            {
                                p.velocity = tangentVelocity * c.groundTangentialDamping + normalVelocity * c.groundNormalDamping;
                                ApplyGroundLoss(p, normal, c, dt);
                            }
                            else
                            {
                                p.velocity = tangentVelocity * c.collisionFriction;
                            }
                        }
                    }
                }
            }
        }

        private void ApplyGroundLoss(SPHParticle3D p, Vector3 normal, MeteoriteSPH3DController c, float dt)
        {
            p.recentGroundContact = 0.45f;
            p.temperature = Mathf.Max(0f, p.temperature - c.groundCoolingRate * c.groundContactCoolingBoost * dt);
        }
    }
}
