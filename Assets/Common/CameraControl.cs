using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraControl : MonoBehaviour
{
    [SerializeField]
    Camera camera;
    [SerializeField]
    Joystick moveJS;
    [SerializeField]
    Joystick rotateJS;

    public float moveSpeed;
    public float rotateSpeed;


    Transform camTF;

    private void Start()
    {
        camTF = camera ? camera.transform : null;

    }

    private void OnEnable()
    {
        moveJS.JoystickMoveHandle += OnMove;
        rotateJS.JoystickMoveHandle += OnRotate;
    }

    private void OnDisable()
    {
        moveJS.JoystickMoveHandle -= OnMove;
        rotateJS.JoystickMoveHandle -= OnRotate;
    }

    public void OnMove(Vector2 moveDir)
    {
        moveDir = moveDir.normalized;
        if (camTF == null)
            return;
        Vector3 deltaPos = new Vector3(moveDir.x, 0, moveDir.y) * moveSpeed * Time.deltaTime;
        if (deltaPos != Vector3.zero)
            camTF.position += camTF.localToWorldMatrix.MultiplyVector(deltaPos);
    }

    public void OnRotate(Vector2 rotateDir)
    {
        rotateDir = rotateDir.normalized;
        if (camTF == null)
            return;
        if ((Vector3.Dot(Vector3.up, camTF.forward) > 0.99f && rotateDir.y < 0) || (Vector3.Dot(Vector3.up, camTF.forward) < -0.99f && rotateDir.y > 0))
            rotateDir.y = 0;
        camTF.rotation = Quaternion.AngleAxis(rotateDir.y * rotateSpeed * Time.deltaTime, camTF.right) * Quaternion.AngleAxis(-rotateDir.x * rotateSpeed * Time.deltaTime, Vector3.up) * camTF.rotation;
    }
}
