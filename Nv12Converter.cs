using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace LumaWall;

// Decode the compact hardware surface directly into reusable desktop storage.
// Each chroma pair belongs to two pixels on two adjacent rows. Use the media
// matrix/range instead of assuming every clip has the same color space.
static class Nv12Converter
{
    internal static unsafe void Copy(IntPtr destination, int destinationStride, IntPtr source, int stride,
        int storageHeight, Rectangle aperture, int matrix, bool fullRange)
    {
        int cy = fullRange ? 256 : 298, yOffset = fullRange ? 0 : 16;
        bool bt709 = matrix == 1, bt2020 = matrix is 4 or 5;
        int cr = fullRange ? (bt709 ? 403 : bt2020 ? 377 : 359) : (bt709 ? 459 : bt2020 ? 430 : 409);
        int cb = fullRange ? (bt709 ? 475 : bt2020 ? 482 : 454) : (bt709 ? 541 : bt2020 ? 548 : 516);
        int gu = fullRange ? (bt709 ? 48 : bt2020 ? 42 : 88) : (bt709 ? 55 : bt2020 ? 48 : 100);
        int gv = fullRange ? (bt709 ? 120 : bt2020 ? 146 : 183) : (bt709 ? 136 : bt2020 ? 167 : 208);
        var yBias = Vector256.Create(yOffset); var uvBias = Vector256.Create(128);
        var yCoefficient = Vector256.Create(cy); var rCoefficient = Vector256.Create(cr);
        var bCoefficient = Vector256.Create(cb); var guCoefficient = Vector256.Create(gu); var gvCoefficient = Vector256.Create(gv);
        var rounding = Vector256.Create(128); var zero = Vector256<int>.Zero; var maximum = Vector256.Create(255);
        var alpha = Vector256.Create(unchecked((int)0xff000000));
        var uMask = Vector128.Create((byte)0, 0, 2, 2, 4, 4, 6, 6, 128, 128, 128, 128, 128, 128, 128, 128);
        var vMask = Vector128.Create((byte)1, 1, 3, 3, 5, 5, 7, 7, 128, 128, 128, 128, 128, 128, 128, 128);
        byte* baseY = (byte*)source; byte* baseUv = baseY + stride * storageHeight;
        for (int row = 0; row < aperture.Height; row++)
        {
            int sourceY = row + aperture.Y;
            byte* y = baseY + sourceY * stride + aperture.X;
            byte* uv = baseUv + (sourceY / 2) * stride;
            int* output = (int*)((byte*)destination + row * destinationStride);
            int x = 0;
            if (Avx2.IsSupported && (aperture.X & 1) == 0)
                for (; x + 8 <= aperture.Width; x += 8)
                {
                    var yy = Avx2.Subtract(Avx2.ConvertToVector256Int32(Vector128.CreateScalar(*(ulong*)(y + x)).AsByte()), yBias);
                    var chroma = Vector128.CreateScalar(*(ulong*)(uv + aperture.X + x)).AsByte();
                    var u = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(chroma, uMask)), uvBias);
                    var v = Avx2.Subtract(Avx2.ConvertToVector256Int32(Ssse3.Shuffle(chroma, vMask)), uvBias);
                    var luma = Avx2.Add(Avx2.MultiplyLow(yy, yCoefficient), rounding);
                    var r = Avx2.ShiftRightArithmetic(Avx2.Add(luma, Avx2.MultiplyLow(v, rCoefficient)), 8);
                    var g = Avx2.ShiftRightArithmetic(Avx2.Subtract(Avx2.Subtract(luma, Avx2.MultiplyLow(u, guCoefficient)), Avx2.MultiplyLow(v, gvCoefficient)), 8);
                    var b = Avx2.ShiftRightArithmetic(Avx2.Add(luma, Avx2.MultiplyLow(u, bCoefficient)), 8);
                    r = Avx2.Min(maximum, Avx2.Max(zero, r)); g = Avx2.Min(maximum, Avx2.Max(zero, g)); b = Avx2.Min(maximum, Avx2.Max(zero, b));
                    var pixels = Avx2.Or(alpha, Avx2.Or(Avx2.ShiftLeftLogical(r, 16), Avx2.Or(Avx2.ShiftLeftLogical(g, 8), b)));
                    Avx.Store(output + x, pixels);
                }
            for (; x < aperture.Width; x++)
            {
                int chromaX = (aperture.X + x) & ~1, u = uv[chromaX] - 128, v = uv[chromaX + 1] - 128;
                int luma = cy * (y[x] - yOffset) + 128;
                int r = Math.Clamp((luma + cr * v) >> 8, 0, 255);
                int g = Math.Clamp((luma - gu * u - gv * v) >> 8, 0, 255);
                int b = Math.Clamp((luma + cb * u) >> 8, 0, 255);
                output[x] = unchecked((int)0xff000000) | r << 16 | g << 8 | b;
            }
        }
    }
}
