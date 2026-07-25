using System.Globalization;
using System.Numerics;

namespace Illusion.Formats.Mathematics;

internal static class QuaternionExtensions
{
    public static Vector3 ToEuler(this Quaternion quat)
    {
        float X = quat.X;
        float Y = quat.Y;
        float Z = quat.Z;
        float W = quat.W;
        float X2 = X * 2.0f;
        float Y2 = Y * 2.0f;
        float Z2 = Z * 2.0f;
        float XX2 = X * X2;
        float XY2 = X * Y2;
        float XZ2 = X * Z2;
        float YY2 = Y * Y2;
        float YZ2 = Y * Z2;
        float ZZ2 = Z * Z2;
        float WX2 = W * X2;
        float WY2 = W * Y2;
        float WZ2 = W * Z2;

        Vector3 AxisX, AxisY, AxisZ;
        AxisX.X = (1.0f - (YY2 + ZZ2));
        AxisY.X = (XY2 + WZ2);
        AxisZ.X = (XZ2 - WY2);
        AxisX.Y = (XY2 - WZ2);
        AxisY.Y = (1.0f - (XX2 + ZZ2));
        AxisZ.Y = (YZ2 + WX2);
        AxisX.Z = (XZ2 + WY2);
        AxisY.Z = (YZ2 - WX2);
        AxisZ.Z = (1.0f - (XX2 + YY2));

        double SmallNumber = double.Parse("1E-08", NumberStyles.Float);
        Vector3 ResultVector = new Vector3();

        ResultVector.Y = (float)Math.Asin(-MathHelper.Clamp(AxisZ.X, -1.0f, 1.0f));

        if (Math.Abs(AxisZ.X) < 1.0f - SmallNumber)
        {
            ResultVector.X = (float)Math.Atan2(AxisZ.Y, AxisZ.Z);
            ResultVector.Z = (float)Math.Atan2(AxisY.X, AxisX.X);
        }
        else
        {
            ResultVector.X = 0.0f;
            ResultVector.Z = (float)Math.Atan2(-AxisX.Y, AxisY.Y);
        }

        ResultVector.Z = MathHelper.ToDegrees(ResultVector.Z);
        ResultVector.Y = MathHelper.ToDegrees(ResultVector.Y);
        ResultVector.X = MathHelper.ToDegrees(ResultVector.X);
        return ResultVector;
    }
}
