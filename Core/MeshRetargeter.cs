using UnityEngine;

namespace MyFirstPlugin.Core;

public static class MeshRetargeter
{
    public static Vector3 MultiplyVector3x3(Matrix4x4 m, Vector3 v) =>
        new Vector3(m.m00 * v.x + m.m01 * v.y + m.m02 * v.z,
                    m.m10 * v.x + m.m11 * v.y + m.m12 * v.z,
                    m.m20 * v.x + m.m21 * v.y + m.m22 * v.z);

    public static Matrix4x4 Mul(Matrix4x4 m, float s)
    {
        Matrix4x4 r = default;
        r.SetColumn(0, m.GetColumn(0) * s);
        r.SetColumn(1, m.GetColumn(1) * s);
        r.SetColumn(2, m.GetColumn(2) * s);
        r.SetColumn(3, m.GetColumn(3) * s);
        return r;
    }

    public static Matrix4x4 Add(Matrix4x4 a, Matrix4x4 b) => new Matrix4x4
    {
        m00 = a.m00 + b.m00,
        m01 = a.m01 + b.m01,
        m02 = a.m02 + b.m02,
        m03 = a.m03 + b.m03,
        m10 = a.m10 + b.m10,
        m11 = a.m11 + b.m11,
        m12 = a.m12 + b.m12,
        m13 = a.m13 + b.m13,
        m20 = a.m20 + b.m20,
        m21 = a.m21 + b.m21,
        m22 = a.m22 + b.m22,
        m23 = a.m23 + b.m23,
        m30 = a.m30 + b.m30,
        m31 = a.m31 + b.m31,
        m32 = a.m32 + b.m32,
        m33 = a.m33 + b.m33
    };
}