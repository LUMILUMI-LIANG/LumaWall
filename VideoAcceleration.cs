using System.Runtime.InteropServices;

namespace LumaWall;

// Decoder and video processor share a hardware D3D11 device. Output remains
// compatible with the existing complete-frame renderer and its frame leases.
sealed class VideoAcceleration : IDisposable
{
    IntPtr device, context, manager;
    public IntPtr Manager => manager;
    static readonly Guid Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    internal static readonly Guid SourceReaderManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");

    public static VideoAcceleration? TryCreate()
    {
        var value = new VideoAcceleration();
        IntPtr protection = IntPtr.Zero;
        try
        {
            // Hardware only: a software D3D rasterizer adds overhead without
            // providing the video acceleration that this path is intended for.
            MfNative.Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x820,
                IntPtr.Zero, 0, 7, out value.device, out _, out value.context));
            var iid = Multithread;
            MfNative.Check(Marshal.QueryInterface(value.device, ref iid, out protection));
            MfNative.Method<SetProtection>(protection, 5)(protection, 1);
            MfNative.Check(MFCreateDXGIDeviceManager(out uint token, out value.manager));
            MfNative.Check(MfNative.Method<ResetDevice>(value.manager, 7)(value.manager, value.device, token));
            return value;
        }
        catch { value.Dispose(); return null; }
        finally { MfNative.Release(ref protection); }
    }

    public void Dispose()
    {
        MfNative.Release(ref manager); MfNative.Release(ref context); MfNative.Release(ref device);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ResetDevice(IntPtr self, IntPtr device, uint token);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetProtection(IntPtr self, int enabled);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, uint driver, IntPtr software, uint flags,
        IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);
    [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out uint token, out IntPtr manager);
}
