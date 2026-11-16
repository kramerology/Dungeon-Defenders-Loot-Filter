namespace DDLootFilter;

internal readonly record struct V3(double X, double Y, double Z)
{
    public static V3 Read(byte[] b, int i) => new(BitConverter.ToSingle(b, i), BitConverter.ToSingle(b, i + 4), BitConverter.ToSingle(b, i + 8));
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public double Dot(V3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public double Length => Math.Sqrt(Dot(this));
    public bool Valid => double.IsFinite(X + Y + Z) && Math.Abs(X) < 1e7 && Math.Abs(Y) < 1e7 && Math.Abs(Z) < 1e7;
}

internal sealed record Camera(V3 Location, int Pitch, int Yaw, int Roll, double Fov)
{
    // Unreal rotator units (65536 per turn) → camera-space (right, up, forward).
    public V3 ToView(V3 p)
    {
        double pitch = Pitch * Math.PI / 32768, yaw = Yaw * Math.PI / 32768, roll = Roll * Math.PI / 32768;
        double sp = Math.Sin(pitch), cp = Math.Cos(pitch), sy = Math.Sin(yaw), cy = Math.Cos(yaw), sr = Math.Sin(roll), cr = Math.Cos(roll);
        V3 forward = new(cp * cy, cp * sy, sp);
        V3 right = new(sr * sp * cy - cr * sy, sr * sp * sy + cr * cy, -sr * cp);
        V3 up = new(-(cr * sp * cy + sr * sy), cy * sr - cr * sp * sy, cr * cp);
        V3 d = p - Location;
        return new(d.Dot(right), d.Dot(up), d.Dot(forward));
    }
    public bool Project(V3 world, double width, double height, out System.Windows.Point screen, out double depth)
    {
        V3 v = ToView(world); screen = default; depth = v.Z;
        if (v.Z < 10) return false;
        double focal = width / (2 * Math.Tan(Fov * Math.PI / 360));
        screen = new(width / 2 + v.X * focal / v.Z, height / 2 - v.Y * focal / v.Z);
        return double.IsFinite(screen.X + screen.Y);
    }
}
