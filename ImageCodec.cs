using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ExplorerDocks
{
    /// <summary>
    /// 画像の読み込みと保存。読み込みはWindows Imaging Component(WIC)を使い、
    /// Windowsに入っているコーデックの形式(PNG/JPEG/GIF/BMP/TIFF/ICO/WebP、拡張機能があればHEIC/AVIF/RAW等)を扱う。
    /// 保存はGDI+で、書き出せない形式はPNGにする。
    /// </summary>
    internal static class ImageCodec
    {
        private static readonly string[] JpegExtensions = { ".jpg", ".jpeg", ".jpe", ".jfif" };

        /// <summary>画像を読み込み、写真の向き(EXIF)を反映した32bit画像を返す。</summary>
        public static Bitmap Load(string path)
        {
            try
            {
                return LoadWithWic(path);
            }
            catch (Exception wicError) when (wicError is COMException or InvalidCastException)
            {
                try
                {
                    // WICで開けない場合の予備(古いEMF/WMFなど)
                    using var image = Image.FromFile(path);
                    var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImage(image, 0, 0, image.Width, image.Height);
                    return copy;
                }
                catch
                {
                    throw new InvalidDataException(
                        "この形式の画像を読み込めませんでした。対応するコーデック(画像拡張機能)が入っているか確認してください。",
                        wicError);
                }
            }
        }

        /// <summary>元画像と同じ形式で書き出せるならその拡張子、できなければ".png"。</summary>
        public static string OutputExtension(string sourcePath)
        {
            string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            return EncoderFor(ext) is null ? ".png" : ext;
        }

        /// <summary>元画像の隣に、重ならない名前のコピーのパスを作る(例: photo_trim.jpg)。</summary>
        public static string CopyPathFor(string sourcePath)
        {
            string dir = Path.GetDirectoryName(sourcePath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            string name = Path.GetFileNameWithoutExtension(sourcePath);
            string ext = OutputExtension(sourcePath);
            string candidate = Path.Combine(dir, $"{name}_trim{ext}");
            for (int i = 2; File.Exists(candidate); i++)
                candidate = Path.Combine(dir, $"{name}_trim({i}){ext}");
            return candidate;
        }

        public static void Save(Bitmap image, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            var format = EncoderFor(ext) ?? ImageFormat.Png;
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == format.Guid);

            // 透過を持てない形式は白地に合成する(回転で生じた余白など)
            bool opaque = format.Guid == ImageFormat.Jpeg.Guid || format.Guid == ImageFormat.Bmp.Guid;
            using var flattened = opaque ? Flatten(image) : null;
            var target = flattened ?? image;

            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(Encoder.Quality,
                format.Guid == ImageFormat.Jpeg.Guid ? 95L : 100L);
            // 途中で失敗しても壊れたファイルを残さないよう、一時ファイルに書いてから置く
            string temp = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    target.Save(stream, codec, parameters);
                File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static ImageFormat? EncoderFor(string ext) => ext switch
        {
            ".png" => ImageFormat.Png,
            ".bmp" or ".dib" => ImageFormat.Bmp,
            ".gif" => ImageFormat.Gif,
            ".tif" or ".tiff" => ImageFormat.Tiff,
            _ when JpegExtensions.Contains(ext) => ImageFormat.Jpeg,
            _ => null,
        };

        private static Bitmap Flatten(Bitmap image)
        {
            var result = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(result);
            g.Clear(Color.White);
            g.DrawImage(image, 0, 0, image.Width, image.Height);
            return result;
        }

        // ==== WIC ====

        private static readonly Guid ClsidImagingFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid PixelFormat32bppPBGRA = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
        private const uint GenericRead = 0x80000000;

        private static Bitmap LoadWithWic(string path)
        {
            var factoryType = Type.GetTypeFromCLSID(ClsidImagingFactory, true)!;
            var factory = (IWICImagingFactory)Activator.CreateInstance(factoryType)!;
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            IWICFormatConverter? converter = null;
            try
            {
                Marshal.ThrowExceptionForHR(factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, 0, out decoder));
                // アニメーションGIFなど複数コマの画像は最初のコマを使う
                Marshal.ThrowExceptionForHR(decoder.GetFrame(0, out frame));
                Marshal.ThrowExceptionForHR(factory.CreateFormatConverter(out converter));
                var format = PixelFormat32bppPBGRA;
                Marshal.ThrowExceptionForHR(converter.Initialize((IWICBitmapSource)frame, ref format, 0, IntPtr.Zero, 0, 0));
                Marshal.ThrowExceptionForHR(converter.GetSize(out uint width, out uint height));
                if (width == 0 || height == 0 || (long)width * height > 400_000_000)
                    throw new InvalidDataException("画像が大きすぎるか、サイズを読み取れません。");

                var bitmap = new Bitmap((int)width, (int)height, PixelFormat.Format32bppPArgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    Marshal.ThrowExceptionForHR(converter.CopyPixels(IntPtr.Zero, (uint)data.Stride,
                        (uint)(data.Stride * bitmap.Height), data.Scan0));
                }
                catch
                {
                    bitmap.UnlockBits(data);
                    bitmap.Dispose();
                    throw;
                }
                bitmap.UnlockBits(data);

                var flip = OrientationToRotateFlip(ReadOrientation(frame));
                if (flip != RotateFlipType.RotateNoneFlipNone) bitmap.RotateFlip(flip);
                return bitmap;
            }
            finally
            {
                if (converter is not null) Marshal.ReleaseComObject(converter);
                if (frame is not null) Marshal.ReleaseComObject(frame);
                if (decoder is not null) Marshal.ReleaseComObject(decoder);
                Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>EXIFの向き(1〜8)。読み取れなければ1。</summary>
        private static int ReadOrientation(IWICBitmapFrameDecode frame)
        {
            // メタデータを持たない形式(WebP等)では失敗するので、成功したときだけ使う
            if (frame.GetMetadataQueryReader(out IntPtr readerPointer) != 0 || readerPointer == IntPtr.Zero) return 1;
            IWICMetadataQueryReader reader;
            try { reader = (IWICMetadataQueryReader)Marshal.GetObjectForIUnknown(readerPointer); }
            catch { return 1; }
            finally { Marshal.Release(readerPointer); }
            try
            {
                foreach (var query in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                {
                    // PROPVARIANTは64bitで24バイト。余裕を持って確保する
                    IntPtr variant = Marshal.AllocCoTaskMem(32);
                    try
                    {
                        for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(variant, i, 0);
                        if (reader.GetMetadataByName(query, variant) != 0) continue;
                        short type = Marshal.ReadInt16(variant);
                        const short VT_UI2 = 18;
                        int value = type == VT_UI2 ? (ushort)Marshal.ReadInt16(variant, 8) : 0;
                        PropVariantClear(variant);
                        if (value is >= 1 and <= 8) return value;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(variant);
                    }
                }
                return 1;
            }
            catch { return 1; }
            finally { Marshal.ReleaseComObject(reader); }
        }

        private static RotateFlipType OrientationToRotateFlip(int orientation) => orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        };

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(IntPtr variant);

        // 使うメソッドより前の項目は、表(vtable)の並びを合わせるための穴埋め。

        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICImagingFactory
        {
            [PreserveSig]
            int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string fileName, IntPtr vendor,
                uint desiredAccess, int metadataOptions, out IWICBitmapDecoder decoder);
            void CreateDecoderFromStream();
            void CreateDecoderFromFileHandle();
            void CreateComponentInfo();
            void CreateDecoder();
            void CreateEncoder();
            void CreatePalette();
            [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapDecoder
        {
            void QueryCapability();
            void Initialize();
            void GetContainerFormat();
            void GetDecoderInfo();
            void CopyPalette();
            void GetMetadataQueryReader();
            void GetPreview();
            void GetColorContexts();
            void GetThumbnail();
            void GetFrameCount();
            [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
        }

        [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapSource
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
        }

        [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapFrameDecode
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig] int GetMetadataQueryReader(out IntPtr reader);
            void GetColorContexts();
            void GetThumbnail();
        }

        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICFormatConverter
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint bufferSize, IntPtr buffer);
            [PreserveSig]
            int Initialize(IWICBitmapSource source, ref Guid destinationFormat, int dither,
                IntPtr palette, double alphaThreshold, int paletteTranslate);
        }

        [ComImport, Guid("30989668-e1c9-4597-b395-458eedb808df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICMetadataQueryReader
        {
            void GetContainerFormat();
            void GetLocation();
            [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr value);
        }
    }
}
