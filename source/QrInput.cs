using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZXing;

namespace LovenseConnector
{
    // Reads image bytes already on the clipboard. URL, HTML and file-drop formats are never fetched.
    internal static class QrInput
    {
        internal const int MaxImageBytes = 32 * 1024 * 1024;
        internal const int MaxPixels = 16000000;
        const string NoQr = "No usable QR code found. Copy a clear, complete FeelConnect QR image or pairing code.";
        static readonly string[] PngFormats = { "PNG", "image/png", "image/x-png" };

        sealed class InputError : ArgumentException
        {
            public InputError(string message) : base(message) { }
        }

        public static string ReadClipboardPairing()
        {
            try { return FromDataObject(Clipboard.GetDataObject()); }
            catch (ExternalException) { throw new InvalidOperationException("The clipboard is busy. Copy the QR image again and retry Paste."); }
        }

        public static string FromDataObject(IDataObject data)
        {
            if (data == null) throw new InputError(NoQr);
            string failure = NoQr;
            foreach (var format in PngFormats)
            {
                try
                {
                    if (!data.GetDataPresent(format, false)) continue;
                    var value = data.GetData(format, false);
                    if (value is byte[] bytes) return DecodePng(bytes);
                    if (value is Stream stream) return DecodePng(ReadBounded(stream));
                }
                catch (Exception ex) when (IsInputFailure(ex)) { failure = ex is InputError ? ex.Message : NoQr; }
            }
            try
            {
                if (data.GetDataPresent(DataFormats.Bitmap, true) && data.GetData(DataFormats.Bitmap, true) is BitmapSource bitmap)
                    return DecodeBitmap(bitmap);
            }
            catch (Exception ex) when (IsInputFailure(ex)) { failure = ex is InputError ? ex.Message : NoQr; }
            foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat })
            {
                try
                {
                    if (data.GetDataPresent(format, true) && data.GetData(format, true) is string text)
                        return ValidatePairing(text);
                }
                catch (Exception ex) when (IsInputFailure(ex)) { /* Image errors remain more useful than an unrelated image URL. */ }
            }
            throw new InputError(failure);
        }

        public static string DecodeBitmap(BitmapSource bitmap)
        {
            if (bitmap == null) throw new InputError(NoQr);
            if (bitmap is BitmapImage image && image.UriSource != null) throw new InputError(NoQr);
            ValidateDimensions((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight);
            try
            {
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int stride = checked(converted.PixelWidth * 4);
                var pixels = new byte[checked(stride * converted.PixelHeight)];
                converted.CopyPixels(pixels, stride, 0);
                // Browser PNGs can carry transparent backgrounds; QR readers need an opaque light background.
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int alpha = pixels[i + 3];
                    if (alpha == 255) continue;
                    for (int channel = 0; channel < 3; channel++)
                        pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                    pixels[i + 3] = 255;
                }
                var reader = new BarcodeReaderGeneric
                {
                    AutoRotate = true,
                    Options = new ZXing.Common.DecodingOptions
                    { TryHarder = true, TryInverted = true, PossibleFormats = new[] { BarcodeFormat.QR_CODE } }
                };
                var result = reader.Decode(pixels, converted.PixelWidth, converted.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
                if (result == null) throw new InputError(NoQr);
                return ValidatePairing(result.Text);
            }
            catch (InputError) { throw; }
            catch (Exception ex) when (IsInputFailure(ex)) { throw new InputError(NoQr); }
        }

        static string DecodePng(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 24) throw new InputError(NoQr);
            if (bytes.Length > MaxImageBytes) throw new InputError("Choose a QR image smaller than 32 MB.");
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) throw new InputError(NoQr);
            if (BigEndian(bytes, 8) != 13 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new InputError(NoQr);
            // Check IHDR before WPF decodes/decompresses the image, including deliberately corrupt oversized PNGs.
            ValidateDimensions(BigEndian(bytes, 16), BigEndian(bytes, 20));
            try
            {
                using var input = new MemoryStream(bytes, false);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) throw new InputError(NoQr);
                return DecodeBitmap(decoder.Frames[0]);
            }
            catch (InputError) { throw; }
            catch (Exception ex) when (IsInputFailure(ex)) { throw new InputError(NoQr); }
        }

        static byte[] ReadBounded(Stream stream)
        {
            if (!stream.CanRead) throw new InputError(NoQr);
            long? originalPosition = stream.CanSeek ? stream.Position : null;
            try
            {
                if (stream.CanSeek)
                {
                    if (stream.Length > MaxImageBytes) throw new InputError("Choose a QR image smaller than 32 MB.");
                    stream.Position = 0;
                }
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                while (true)
                {
                    int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxImageBytes - copy.Length + 1));
                    if (count == 0) break;
                    if (copy.Length + count > MaxImageBytes) throw new InputError("Choose a QR image smaller than 32 MB.");
                    copy.Write(buffer, 0, count);
                }
                return copy.ToArray();
            }
            finally { if (originalPosition.HasValue) stream.Position = originalPosition.Value; }
        }

        static string ValidatePairing(string value)
        {
            if (value == null || value.Length > 8192) throw new InputError(NoQr);
            value = value.Trim();
            try { FeelConnectPairing.Parse(value); }
            catch (ArgumentException) { throw new InputError(NoQr); }
            // Keep the original deep-link form because /connect?code and /authorize?token have different semantics.
            return value;
        }
        static void ValidateDimensions(uint width, uint height)
        {
            if (width == 0 || height == 0 || (ulong)width * height > MaxPixels)
                throw new InputError("Choose a QR image smaller than 16 megapixels.");
        }
        static uint BigEndian(byte[] value, int at) => ((uint)value[at] << 24) | ((uint)value[at + 1] << 16) | ((uint)value[at + 2] << 8) | value[at + 3];
        static bool IsInputFailure(Exception ex) => ex is ArgumentException || ex is IOException || ex is NotSupportedException ||
            ex is InvalidOperationException || ex is ExternalException || ex is System.FormatException || ex is OverflowException;
    }
}
