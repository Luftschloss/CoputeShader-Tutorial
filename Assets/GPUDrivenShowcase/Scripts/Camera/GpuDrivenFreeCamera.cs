using UnityEngine;

public sealed class GpuDrivenFreeCamera : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 18.0f;
    [SerializeField] private float fastMoveMultiplier = 4.0f;
    [SerializeField] private float lookSensitivity = 0.12f;
    [SerializeField] private bool lockCursorWhileLooking = true;

    private bool looking;
    private float yaw;
    private float pitch;

    private void Start()
    {
        Vector3 euler = transform.rotation.eulerAngles;
        yaw = euler.y;
        pitch = NormalizePitch(euler.x);
    }

    private void Update()
    {
        UpdateLookMode();
        UpdateRotation();
        UpdateMovement();
    }

    private void UpdateLookMode()
    {
        if (Input.GetMouseButtonDown(1))
        {
            looking = true;
            if (lockCursorWhileLooking)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
        else if (Input.GetMouseButtonUp(1))
        {
            looking = false;
            if (lockCursorWhileLooking)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }

    private void UpdateRotation()
    {
        if (!looking)
        {
            return;
        }

        yaw += Input.GetAxisRaw("Mouse X") * lookSensitivity * 60.0f;
        pitch -= Input.GetAxisRaw("Mouse Y") * lookSensitivity * 60.0f;
        pitch = Mathf.Clamp(pitch, -89.0f, 89.0f);
        transform.rotation = Quaternion.Euler(pitch, yaw, 0.0f);
    }

    private void UpdateMovement()
    {
        Vector3 input = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) input += Vector3.forward;
        if (Input.GetKey(KeyCode.S)) input += Vector3.back;
        if (Input.GetKey(KeyCode.D)) input += Vector3.right;
        if (Input.GetKey(KeyCode.A)) input += Vector3.left;
        if (Input.GetKey(KeyCode.E)) input += Vector3.up;
        if (Input.GetKey(KeyCode.Q)) input += Vector3.down;

        if (input.sqrMagnitude <= 0.0f)
        {
            return;
        }

        float speed = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)
            ? moveSpeed * fastMoveMultiplier
            : moveSpeed;
        transform.position += transform.TransformDirection(input.normalized) * speed * Time.unscaledDeltaTime;
    }

    private static float NormalizePitch(float pitchValue)
    {
        return pitchValue > 180.0f ? pitchValue - 360.0f : pitchValue;
    }
}
