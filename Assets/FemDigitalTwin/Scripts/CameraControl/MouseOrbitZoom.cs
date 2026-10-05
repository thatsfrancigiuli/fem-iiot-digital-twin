using UnityEngine;

namespace FemDigitalTwin.CameraControl
{
    /// <summary>
    /// Minimal orbit / zoom / pan camera for inspecting the point cloud.
    ///   Right mouse button: orbit    Middle mouse button: pan    Wheel: zoom
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class MouseOrbitZoom : MonoBehaviour
    {
        [SerializeField] private Vector3 pivot = Vector3.zero;
        [SerializeField] private float distance = 10f;
        [SerializeField] private float orbitSpeed = 4f;
        [SerializeField] private float zoomSpeed = 0.15f;
        [SerializeField] private float panSpeed = 0.002f;
        [SerializeField] private float minDistance = 0.05f;
        [SerializeField] private float maxDistance = 10000f;

        private float yaw = 30f;
        private float pitch = 25f;

        public void FocusOnBounds(Bounds bounds, float distanceMultiplier)
        {
            pivot = bounds.center;
            float radius = bounds.extents.magnitude;
            float fov = GetComponent<Camera>().fieldOfView * Mathf.Deg2Rad;
            distance = Mathf.Clamp(radius / Mathf.Sin(fov * 0.5f) * Mathf.Max(0.1f, distanceMultiplier) * 0.5f,
                                   minDistance, maxDistance);
            Apply();
        }

        private void LateUpdate()
        {
            if (Input.GetMouseButton(1))
            {
                yaw += Input.GetAxis("Mouse X") * orbitSpeed;
                pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * orbitSpeed, -89f, 89f);
            }
            if (Input.GetMouseButton(2))
            {
                pivot -= transform.right * Input.GetAxis("Mouse X") * panSpeed * distance * 10f;
                pivot -= transform.up * Input.GetAxis("Mouse Y") * panSpeed * distance * 10f;
            }
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0f)
                distance = Mathf.Clamp(distance * (1f - wheel * zoomSpeed * 10f), minDistance, maxDistance);
            Apply();
        }

        private void Apply()
        {
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.rotation = rot;
            transform.position = pivot - rot * Vector3.forward * distance;
        }
    }
}
