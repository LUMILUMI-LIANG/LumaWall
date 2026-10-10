using System.Runtime.InteropServices;

namespace LumaWall;

static class Nv12ConversionTest
{
    public static void Run()
    {
        const int pitch = 32, height = 8;
        byte[] input = new byte[pitch * height * 3 / 2];
        new Random(7821).NextBytes(input);
        IntPtr source = Marshal.AllocHGlobal(input.Length), target = IntPtr.Zero;
        int cases = 0;
        try
        {
            Marshal.Copy(input, 0, source, input.Length);
            foreach (int matrix in new[] { 1, 2, 4 })
                foreach (bool full in new[] { false, true })
                    foreach (var aperture in new[] { new Rectangle(0, 0, 18, 8), new Rectangle(1, 1, 15, 5) })
                    {
                        int outputPitch = aperture.Width * 4 + 20;
                        byte[] output = Enumerable.Repeat((byte)0xa5, outputPitch * aperture.Height).ToArray();
                        target = Marshal.AllocHGlobal(output.Length); Marshal.Copy(output, 0, target, output.Length);
                        Nv12Converter.Copy(target, outputPitch, source, pitch, height, aperture, matrix, full);
                        Marshal.Copy(target, output, 0, output.Length);
                        for (int y = 0; y < aperture.Height; y++)
                        {
                            for (int x = 0; x < aperture.Width; x++)
                            {
                                int sx = aperture.X + x, sy = aperture.Y + y;
                                double luma = full ? input[sy * pitch + sx] : (input[sy * pitch + sx] - 16) * 255.0 / 219;
                                double u = input[pitch * height + sy / 2 * pitch + (sx & ~1)] - 128;
                                double v = input[pitch * height + sy / 2 * pitch + (sx & ~1) + 1] - 128;
                                if (!full) { u *= 255.0 / 224; v *= 255.0 / 224; }
                                double kr = matrix == 1 ? .2126 : matrix == 4 ? .2627 : .299;
                                double kb = matrix == 1 ? .0722 : matrix == 4 ? .0593 : .114;
                                double kg = 1 - kr - kb;
                                double[] expected = [luma + 2 * (1 - kb) * u,
                                    luma - 2 * kb * (1 - kb) / kg * u - 2 * kr * (1 - kr) / kg * v,
                                    luma + 2 * (1 - kr) * v];
                                int index = y * outputPitch + x * 4;
                                for (int channel = 0; channel < 3; channel++)
                                    if (Math.Abs(output[index + channel] - Math.Clamp((int)Math.Round(expected[channel]), 0, 255)) > 2)
                                        throw new InvalidOperationException($"Incorrect NV12 color: matrix={matrix}, full={full}, aperture={aperture}, pixel={x},{y}, channel={channel}.");
                                if (output[index + 3] != 255) throw new InvalidOperationException("Converted video must remain opaque.");
                            }
                            for (int padding = aperture.Width * 4; padding < outputPitch; padding++)
                                if (output[y * outputPitch + padding] != 0xa5) throw new InvalidOperationException("NV12 conversion wrote beyond the destination row.");
                        }
                        Marshal.FreeHGlobal(target); target = IntPtr.Zero; cases++;
                    }
            byte[] unchanged = new byte[input.Length]; Marshal.Copy(source, unchanged, 0, input.Length);
            if (!input.SequenceEqual(unchanged)) throw new InvalidOperationException("NV12 source must remain unchanged.");
        }
        finally { if (target != IntPtr.Zero) Marshal.FreeHGlobal(target); Marshal.FreeHGlobal(source); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "nv12-test.txt"),
            $"PASS: {cases} matrix/range/aperture cases compared with independent floating-point reference; vector batches and scalar tails; odd aperture offsets; padded source/destination; opaque alpha; source unchanged.");
    }
}
