using UnityEngine;

namespace MeteoriteSPH3D
{
    public sealed class CameraController3D : MonoBehaviour
    {
        public Vector3 target;
        public float distance = 34f;
        public float yaw = 45f;
        public float pitch = 42f;
        public float rotateSpeed = 0.22f;
        public float panSpeed = 0.035f;
        public float zoomSpeed = 6.0f;
        public float fastZoomMultiplier = 3.0f;
        public float fastPanMultiplier = 2.2f;

        public void Initialize(Vector3 target, float distance)
        {
            this.target = target;
            this.distance = distance;
            UpdateTransform();
        }

        private void LateUpdate()
        {

            Vector2 delta = InputBridge3D.MouseDelta();

            if (InputBridge3D.Mouse(1) && !InputBridge3D.Key(KeyCode.LeftShift) && !InputBridge3D.Key(KeyCode.RightShift))
            {
                yaw += delta.x * rotateSpeed;
                pitch -= delta.y * rotateSpeed;
                pitch = Mathf.Clamp(pitch, 12f, 82f);
            }

            bool fast = InputBridge3D.Key(KeyCode.LeftShift) || InputBridge3D.Key(KeyCode.RightShift);
            if (InputBridge3D.Mouse(2) || (InputBridge3D.Mouse(1) && fast))
            {
                Vector3 right = transform.right;
                Vector3 up = Vector3.Cross(right, Vector3.up);
                float panMul = fast ? fastPanMultiplier : 1f;
                target += (-right * delta.x + up * delta.y) * panSpeed * panMul * Mathf.Max(1f, distance * 0.08f);
            }

            float scroll = InputBridge3D.ScrollY();
            if (Mathf.Abs(scroll) > 0.001f)
            {
                float zoomMul = fast ? fastZoomMultiplier : 1f;
                float zoomAmount = scroll * zoomSpeed * zoomMul * Mathf.Max(1f, distance * 0.12f);
                distance = Mathf.Clamp(distance - zoomAmount, 2.5f, 180f);
            }

            if (InputBridge3D.KeyDown(KeyCode.F))
            {
                target = new Vector3(MeteoriteSPH3DController.Instance.terrainWidth * MeteoriteSPH3DController.Instance.cellSize * 0.5f, MeteoriteSPH3DController.Instance.terrainHeight * MeteoriteSPH3DController.Instance.cellSize * 0.25f, MeteoriteSPH3DController.Instance.terrainDepth * MeteoriteSPH3DController.Instance.cellSize * 0.5f);
            }

            UpdateTransform();
        }

        private void UpdateTransform()
        {
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.position = target - rot * Vector3.forward * distance;
            transform.rotation = rot;
        }
    }
}
