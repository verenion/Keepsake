using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace PhotoLibrary.Core.Imaging;

/// <summary>
/// The Windows Imaging Component: Windows' own image decoders. Used for formats Skia can't read: TIFF (built
/// into Windows) and HEIC (with the HEIF/HEVC Store extensions). Decodes from a file or from memory, so vault
/// items never need a decrypted file on disk. Returns null off Windows or when no decoder accepts the data.
/// </summary>
public static class WicImage
{
    public static readonly Guid ContainerHeif = new("e1e62521-6787-405b-a339-500715b5763f");

    /// <summary>Why the last decode failed, for diagnostics.</summary>
    public static string? LastError { get; private set; }

    public static SKBitmap? DecodeFile(string path, int maxEdge) =>
        OperatingSystem.IsWindows() ? Guard(() => Wic.Decode(f => f.CreateDecoderFromFilename(path, Guid.Empty, Wic.GenericRead, Wic.MetadataCacheOnDemand), maxEdge)) : null;

    public static SKBitmap? DecodeMemory(byte[] data, int maxEdge)
    {
        if (!OperatingSystem.IsWindows() || data.Length == 0) return null;
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned); // WIC reads straight from this buffer while decoding
        try
        {
            return Guard(() => Wic.Decode(f =>
            {
                var stream = f.CreateStream();
                stream.InitializeFromMemory(pin.AddrOfPinnedObject(), (uint)data.Length);
                return f.CreateDecoderFromStream(stream, Guid.Empty, Wic.MetadataCacheOnDemand);
            }, maxEdge));
        }
        finally { pin.Free(); }
    }

    /// <summary>True when Windows has a decoder registered for the container format.</summary>
    public static bool HasDecoder(Guid container)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return Wic.HasDecoder(container); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException) { return false; }
    }

    private static SKBitmap? Guard(Func<SKBitmap?> decode)
    {
        try { return decode(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    // ---- interop: the handful of interfaces needed. Vtable order matters, so base methods are repeated.

    [SupportedOSPlatform("windows")]
    private static class Wic
    {
        private static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid PixelFormat32bppBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        public const uint GenericRead = 0x80000000;
        public const int MetadataCacheOnDemand = 0;
        private const int InterpolationFant = 3;

        private static IWICImagingFactory Factory() =>
            (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidImagingFactory)!)!;

        public static bool HasDecoder(Guid container)
        {
            var factory = Factory();
            try
            {
                var decoder = factory.CreateDecoder(container, IntPtr.Zero);
                Marshal.ReleaseComObject(decoder);
                return true;
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        public static SKBitmap? Decode(Func<IWICImagingFactory, IWICBitmapDecoder> open, int maxEdge)
        {
            var factory = Factory();
            object? decoder = null, frame = null, scaler = null, converter = null;
            try
            {
                var dec = open(factory);
                decoder = dec;
                var fr = dec.GetFrame(0);
                frame = fr;
                fr.GetSize(out var w, out var h);
                if (w == 0 || h == 0) return null;

                IWICBitmapSource source = fr;
                var scale = Math.Min(1.0, (double)maxEdge / Math.Max(w, h));
                var ow = Math.Max(1u, (uint)Math.Round(w * scale));
                var oh = Math.Max(1u, (uint)Math.Round(h * scale));
                if (scale < 1)
                {
                    var sc = factory.CreateBitmapScaler();
                    scaler = sc;
                    sc.Initialize(fr, ow, oh, InterpolationFant);
                    source = sc;
                }
                var conv = factory.CreateFormatConverter();
                converter = conv;
                conv.Initialize(source, PixelFormat32bppBGRA, 0, IntPtr.Zero, 0.0, 0);

                var bmp = new SKBitmap(new SKImageInfo((int)ow, (int)oh, SKColorType.Bgra8888, SKAlphaType.Unpremul));
                var stride = (uint)bmp.RowBytes;
                conv.CopyPixels(IntPtr.Zero, stride, stride * oh, bmp.GetPixels());
                return bmp;
            }
            finally
            {
                foreach (var o in new[] { converter, scaler, frame, decoder })
                    if (o is not null) Marshal.ReleaseComObject(o);
                Marshal.ReleaseComObject(factory);
            }
        }

        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICImagingFactory
        {
            IWICBitmapDecoder CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, in Guid vendor, uint desiredAccess, int metadataOptions);
            IWICBitmapDecoder CreateDecoderFromStream(IWICStream stream, in Guid vendor, int metadataOptions);
            void CreateDecoderFromFileHandle_();
            void CreateComponentInfo_();
            IWICBitmapDecoder CreateDecoder(in Guid containerFormat, IntPtr vendor);
            void CreateEncoder_();
            void CreatePalette_();
            IWICFormatConverter CreateFormatConverter();
            IWICBitmapScaler CreateBitmapScaler();
            void CreateBitmapClipper_();
            void CreateBitmapFlipRotator_();
            IWICStream CreateStream();
        }

        /// <summary>IWICStream extends IStream (Read, Write, then nine IStream methods) with its initialisers.</summary>
        [ComImport, Guid("135ff860-22b7-4ddf-b0f6-218f4f299a43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICStream
        {
            void Read_(); void Write_();
            void Seek_(); void SetSize_(); void CopyTo_(); void Commit_(); void Revert_();
            void LockRegion_(); void UnlockRegion_(); void Stat_(); void Clone_();
            void InitializeFromIStream_();
            void InitializeFromFilename_();
            void InitializeFromMemory(IntPtr buffer, uint size);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICBitmapDecoder
        {
            void QueryCapability_();
            void Initialize_();
            void GetContainerFormat_();
            void GetDecoderInfo_();
            void CopyPalette_();
            void GetMetadataQueryReader_();
            void GetPreview_();
            void GetColorContexts_();
            void GetThumbnail_();
            uint GetFrameCount();
            IWICBitmapFrameDecode GetFrame(uint index);
        }

        [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICBitmapSource
        {
            void GetSize(out uint width, out uint height);
            void GetPixelFormat(out Guid format);
            void GetResolution(out double dpiX, out double dpiY);
            void CopyPalette_();
            void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
        }

        [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICBitmapFrameDecode : IWICBitmapSource
        {
            new void GetSize(out uint width, out uint height);
            new void GetPixelFormat(out Guid format);
            new void GetResolution(out double dpiX, out double dpiY);
            new void CopyPalette_();
            new void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
        }

        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICFormatConverter : IWICBitmapSource
        {
            new void GetSize(out uint width, out uint height);
            new void GetPixelFormat(out Guid format);
            new void GetResolution(out double dpiX, out double dpiY);
            new void CopyPalette_();
            new void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
            void Initialize(IWICBitmapSource source, in Guid dstFormat, int dither, IntPtr palette, double alphaThreshold, int paletteTranslate);
        }

        [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IWICBitmapScaler : IWICBitmapSource
        {
            new void GetSize(out uint width, out uint height);
            new void GetPixelFormat(out Guid format);
            new void GetResolution(out double dpiX, out double dpiY);
            new void CopyPalette_();
            new void CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
            void Initialize(IWICBitmapSource source, uint width, uint height, int mode);
        }
    }
}
