// =================================================================================================
// Shell.cs: what the Windows shell knows about a window's app: its app id and its icon.
//
// Packaged apps (Settings, Calculator, Photos...) all run inside ApplicationFrameHost.exe, whose own
// icon says nothing and whose windows answer WM_GETICON with nothing, so the strip used to draw a
// lettered disc for them. The taskbar finds their icons through the window's AppUserModelID; so does
// ShellApps. The id also lets "New window" in the icon menu start a packaged app.
// =================================================================================================

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DeskHud
{
    /// App ids and icons from the Windows shell. UI thread (COM, STA).
    static class ShellApps
    {
        static readonly Guid PropertyStoreId = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        static readonly Guid ImageFactoryId = new Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B");
        static readonly PROPERTYKEY AppUserModelId = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        const ushort VT_LPWSTR = 31;
        const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

        /// The window's AppUserModelID, the name the taskbar groups it by; null when it has none.
        public static string AppId(IntPtr window)
        {
            IPropertyStore store = null;
            var value = new PROPVARIANT();
            try
            {
                Guid iid = PropertyStoreId;
                if (SHGetPropertyStoreForWindow(window, ref iid, out store) != 0 || store == null) return null;
                PROPERTYKEY key = AppUserModelId;
                if (store.GetValue(ref key, out value) != 0 || value.vt != VT_LPWSTR || value.pointer == IntPtr.Zero) return null;
                string id = Marshal.PtrToStringUni(value.pointer);
                return string.IsNullOrEmpty(id) ? null : id;
            }
            catch (Exception ex)
            {
                Log.Error("shell: app id", ex);
                return null;
            }
            finally
            {
                PropVariantClear(ref value);
                if (store != null) Marshal.ReleaseComObject(store);
            }
        }

        /// The app's icon as the Start menu shows it, `pixels` square; null when the shell has none.
        public static Bitmap Icon(string appId, int pixels)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            IShellItemImageFactory factory = null;
            IntPtr hbitmap = IntPtr.Zero;
            try
            {
                Guid iid = ImageFactoryId;
                if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                if (factory.GetImage(new Native.SIZE(pixels, pixels), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap) != 0) return null;
                return FromHbitmap(hbitmap);
            }
            catch (Exception ex)
            {
                Log.Error("shell: icon for " + appId, ex);
                return null;
            }
            finally
            {
                if (hbitmap != IntPtr.Zero) Native.DeleteObject(hbitmap);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        /// The shell hands back 32-bit pixels with alpha, which Image.FromHbitmap would drop. Despite the
        /// docs they are not always premultiplied (Settings' gear came back straight); treated as
        /// premultiplied, those pixels punched transparent holes in the island.
        static Bitmap FromHbitmap(IntPtr hbitmap)
        {
            BITMAP info;
            if (GetObject(hbitmap, Marshal.SizeOf(typeof(BITMAP)), out info) == 0) return null;
            int w = info.bmWidth, h = Math.Abs(info.bmHeight);
            if (w <= 0 || h <= 0) return null;
            // A negative height asks for rows top-down, whichever way the shell stored them.
            var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new int[w * h];
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(screen, hbitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
            // Some icons come without alpha at all; treat those as opaque. A colour brighter than its
            // alpha cannot be premultiplied, so one such pixel means the whole image is straight alpha.
            bool anyAlpha = false, straight = false;
            foreach (int p in pixels)
            {
                int a = (p >> 24) & 0xFF;
                if (a != 0) anyAlpha = true;
                if (((p >> 16) & 0xFF) > a || ((p >> 8) & 0xFF) > a || (p & 0xFF) > a) straight = true;
            }
            if (!anyAlpha)
            {
                for (int i = 0; i < pixels.Length; i++) pixels[i] |= unchecked((int)0xFF000000);
                straight = true;
            }

            PixelFormat format = straight ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppPArgb;
            var bitmap = new Bitmap(w, h, format);
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, format);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid fmtid; public uint pid; }

        // Only the type tag and the pointer member are read; the size covers the 64-bit layout.
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointer;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr hbitmap);
        }

        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, out BITMAP bitmap);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbitmap, uint start, uint lines, [Out] int[] bits, ref BITMAPINFOHEADER info, uint usage);
    }
}
