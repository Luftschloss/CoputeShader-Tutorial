using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CameraTool
{
    /// <summary>
    /// 点法式获取平面方程（Ax+By+Cz+D=0）4个系数
    /// </summary>
    /// <param name="normal">平面法线</param>
    /// <param name="point">平面上一点</param>
    /// <returns></returns>
    public static Vector4 GetPlane(Vector3 normal, Vector3 point)
    {
        return new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(normal, point));
    }

    /// <summary>
    /// 三点确定一个平面
    /// </summary>
    /// <param name="a"></param>
    /// <param name="b"></param>
    /// <param name="c"></param>
    /// <returns></returns>
    public static Vector4 GetPlane(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return GetPlane(normal, a);
    }

    /// <summary>
    /// 获取远裁剪面上四个点
    /// </summary>
    /// <param name="camera"></param>
    /// <returns></returns>
    public static Vector3[] GetCameraFarClipPlanePoint(Camera camera)
    {
        Vector3[] points = new Vector3[4];
        camera.CalculateFrustumCorners(new Rect(0, 0, 1, 1), camera.farClipPlane, camera.stereoActiveEye, points);
        for (int i = 0; i < 4; i++)
        {
            points[i] = camera.transform.TransformVector(points[i]);
        }
        return points;
    }

    /// <summary>
    /// 获取视锥体的六个平面
    /// </summary>
    /// <param name="camera"></param>
    /// <returns></returns>
    public static Vector4[] GetFrustumPlane(Camera camera)
    {
        Vector4[] planes = new Vector4[6];
        Vector3 pos = camera.transform.position;
        Vector3 fwd = camera.transform.forward;
        Vector3[] points = GetCameraFarClipPlanePoint(camera);
        //顺时针
        planes[0] = GetPlane(pos, points[0], points[1]);                //left
        planes[1] = GetPlane(pos, points[2], points[3]);                //right
        planes[2] = GetPlane(pos, points[3], points[0]);                //bottom
        planes[3] = GetPlane(pos, points[1], points[2]);                //up
        planes[4] = GetPlane(-fwd, pos + fwd * camera.nearClipPlane);   //near
        planes[5] = GetPlane(fwd, pos + fwd * camera.farClipPlane);     //far
        return planes;
    }

    public static bool IsOutsideThPlane(Vector4 plane, Vector3 point)
    {
        Vector3 normal = new Vector3(plane.x, plane.y, plane.z);
        return Vector3.Dot(normal, point) + plane.w > 0;
    }


}