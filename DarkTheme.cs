using System.Runtime.InteropServices;
namespace LumaWall;
static class DarkTheme
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PreferredMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate bool AllowWindow(IntPtr hwnd, bool allow);
    static AllowWindow? allowWindow;
    internal static void Initialize()
    {
        // These optional Windows 10+ exports improve legacy native scrollbars.
        // Guard by OS build and availability; DWM colors remain independent.
        if (Environment.OSVersion.Version.Build < 18362) return;
        var module = LoadLibrary("uxtheme.dll");
        if (module == IntPtr.Zero) return;
        var appMode = GetProcAddress(module, (IntPtr)135);
        if (appMode != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<PreferredMode>(appMode)(2);
        var windowMode = GetProcAddress(module, (IntPtr)133);
        if (windowMode != IntPtr.Zero) allowWindow = Marshal.GetDelegateForFunctionPointer<AllowWindow>(windowMode);
    }
    internal static void Attach(Control root)
    {
        if (root is TextBoxBase input)
        {
            input.BackColor = Palette.Field;
            input.ForeColor = Palette.Text;
            input.BorderStyle = BorderStyle.FixedSingle;
        }
        void Apply()
        {
            if (!root.IsHandleCreated) return;
            allowWindow?.Invoke(root.Handle, true);
            SetWindowTheme(root.Handle, root is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer", null);
            if (root is Form)
            {
                int dark = 1, caption = ColorTranslator.ToWin32(Palette.Background), border = ColorTranslator.ToWin32(Palette.Border);
                DwmSetWindowAttribute(root.Handle, 20, ref dark, 4);
                DwmSetWindowAttribute(root.Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(root.Handle, 34, ref border, 4);
            }
            if (root is ComboBox combo) combo.Invalidate();
        }
        root.HandleCreated += (_, _) => Apply(); Apply();
        foreach (Control child in root.Controls) Attach(child);
        root.ControlAdded += (_, e) => { if (e.Control != null) Attach(e.Control); };
    }
}
