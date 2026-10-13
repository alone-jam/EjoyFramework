using System;
using System.Numerics;
namespace EjoyFramework.GamePlay.Combat
{
    internal static class HistoricalIntersection
    {
        public static bool Sweep(in HistoricalCollider c, Vector3 o, Vector3 d, float radius, out float t, out Vector3 normal)
        {
            if (c.Shape == HistoricalShape.Sphere) return Capsule(o, d, c.A, c.A, c.Radius + radius, out t, out normal);
            if (c.Shape == HistoricalShape.Capsule) return Capsule(o, d, c.A, c.B, c.Radius + radius, out t, out normal);
            var inverse = Quaternion.Conjugate(c.Rotation); var localO = Vector3.Transform(o - c.A, inverse); var localD = Vector3.Transform(d, inverse);
            bool found = Box(localO, localD, c.B, radius, out t, out normal); normal = Vector3.Transform(normal, c.Rotation); return found;
        }
        private static double Dot(Vector3 a, Vector3 b) => (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z;
        private static Vector3 Normal(Vector3 v, Vector3 fallback) => v.LengthSquared() < 1e-12f ? -fallback : Vector3.Normalize(v);
        private static bool Sphere(Vector3 o, Vector3 d, Vector3 center, float radius, out float t, out Vector3 normal)
        {
            var m = o - center; double a = Dot(d, d), b = Dot(m, d), c = Dot(m, m) - (double)radius * radius;
            if (c <= 0) { t = 0; normal = Normal(m, d); return true; }
            double h = b * b - a * c;
            if (h < 0 || b > 0) { t = 0; normal = default; return false; }
            t = (float)((-b - Math.Sqrt(h)) / a); normal = Normal(o + d * t - center, d); return t >= 0;
        }
        private static bool Capsule(Vector3 o, Vector3 d, Vector3 a, Vector3 b, float radius, out float t, out Vector3 normal)
        {
            var ba = b - a; var oa = o - a; double baba = Dot(ba, ba);
            if (baba < 1e-12) return Sphere(o, d, a, radius, out t, out normal);
            float projection = (float)Math.Max(0, Math.Min(1, Dot(oa, ba) / baba));
            var closest = a + ba * projection;
            if (Vector3.DistanceSquared(o, closest) <= radius * radius) { t = 0; normal = Normal(o - closest, d); return true; }
            bool found = false; t = float.PositiveInfinity; normal = default;
            double bard = Dot(ba, d), baoa = Dot(ba, oa), rdoa = Dot(d, oa), oaoa = Dot(oa, oa);
            double qa = baba * Dot(d, d) - bard * bard, qb = baba * rdoa - baoa * bard, qc = baba * oaoa - baoa * baoa - (double)radius * radius * baba;
            double h = qb * qb - qa * qc;
            if (qa > 1e-12 && h >= 0)
            {
                double candidate = (-qb - Math.Sqrt(h)) / qa, y = baoa + candidate * bard;
                if (candidate >= 0 && y >= 0 && y <= baba) { t = (float)candidate; normal = Normal(o + d * t - a - ba * (float)(y / baba), d); found = true; }
            }
            if (Sphere(o, d, a, radius, out float ta, out var na) && ta < t) { t = ta; normal = na; found = true; }
            if (Sphere(o, d, b, radius, out float tb, out var nb) && tb < t) { t = tb; normal = nb; found = true; }
            return found;
        }
        private static float Get(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
        private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
        private static bool Box(Vector3 o, Vector3 d, Vector3 half, float radius, out float t, out Vector3 normal)
        {
            var nearest = Vector3.Clamp(o, -half, half);
            if (Vector3.DistanceSquared(o, nearest) <= radius * radius) { t = 0; normal = Normal(o - nearest, d); return true; }
            bool found = false; t = float.PositiveInfinity; normal = default;
            // Exact rounded-box faces and edge capsules (including spherical corners), not an expanded-AABB approximation.
            for (int axis = 0; axis < 3; axis++)
            {
                int j = (axis + 1) % 3, k = (axis + 2) % 3; float denominator = Get(d, axis);
                if (Math.Abs(denominator) > 1e-8f)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        float candidate = (sign * (Get(half, axis) + radius) - Get(o, axis)) / denominator;
                        if (candidate < 0 || candidate >= t) continue;
                        var point = o + d * candidate;
                        if (Math.Abs(Get(point, j)) <= Get(half, j) && Math.Abs(Get(point, k)) <= Get(half, k))
                        { t = candidate; normal = Axis(axis) * sign; found = true; }
                    }
                if (radius <= 0) continue;
                for (int sj = -1; sj <= 1; sj += 2)
                    for (int sk = -1; sk <= 1; sk += 2)
                    {
                        var center = Axis(j) * Get(half, j) * sj + Axis(k) * Get(half, k) * sk;
                        var edge = Axis(axis) * Get(half, axis);
                        if (Capsule(o, d, center - edge, center + edge, radius, out float candidate, out var n) && candidate < t)
                        { t = candidate; normal = n; found = true; }
                    }
            }
            return found;
        }
    }
}
