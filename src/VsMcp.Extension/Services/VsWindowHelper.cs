using System;
using EnvDTE80;

namespace VsMcp.Extension.Services
{
    /// <summary>
    /// Small helpers for looking up Visual Studio window handles from DTE APIs.
    /// Kept in one place so multiple tool modules can share the same
    /// version-tolerant HWnd extraction logic.
    /// </summary>
    public static class VsWindowHelper
    {
        /// <summary>
        /// Return the VS main window HWND, or IntPtr.Zero if it cannot be resolved.
        /// DTE.Window.HWnd was <c>long</c> in older SDKs and is <c>IntPtr?</c> in
        /// newer ones — this accepts either shape via runtime binding.
        /// </summary>
        public static IntPtr TryGetMainWindowHandle(DTE2 dte)
        {
            try
            {
                var mainWindow = dte?.MainWindow;
                if (mainWindow == null) return IntPtr.Zero;
                object raw = mainWindow.HWnd;
                if (raw == null) return IntPtr.Zero;
                if (raw is IntPtr ip) return ip;
                long asLong = Convert.ToInt64(raw);
                return asLong == 0 ? IntPtr.Zero : new IntPtr(asLong);
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
