using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LovenseConnector;
using ZXing;

// Only synthetic IDataObjects: never reads, overwrites, or clears the user's clipboard.
internal static class QrInputTests
{
    const string Pairing = "feelapp://connect?code=qr-fixture";
    static int assertions;
    [STAThread]
    static void Main()
    {
        var bitmap = MakeQr(Pairing);
        var png = EncodePng(bitmap);
        Check(QrInput.DecodeBitmap(bitmap) == Pairing, "direct bitmap decode");
        Check(QrInput.FromDataObject(Data("PNG", png)) == Pairing, "Firefox PNG bytes");
        Check(QrInput.FromDataObject(Data("image/png", png)) == Pairing, "MIME PNG bytes");
        Check(QrInput.FromDataObject(Data(DataFormats.Bitmap, bitmap)) == Pairing, "native bitmap fallback");
        Check(QrInput.FromDataObject(Data(DataFormats.UnicodeText, "  synthetic-clipboard-token  ")) == "synthetic-clipboard-token", "text token trims");
        Check(QrInput.FromDataObject(Data(DataFormats.UnicodeText, Pairing)) == Pairing, "connection-code link keeps its semantics");
        using (var stream = new MemoryStream(png))
        {
            stream.Position = stream.Length;
            Check(QrInput.FromDataObject(Data("PNG", stream)) == Pairing, "PNG stream reads from start");
            Check(stream.Position == stream.Length && stream.CanRead, "caller stream position and lifetime preserved");
            stream.Position = 7;
            Check(QrInput.FromDataObject(Data("image/png", stream)) == Pairing && stream.Position == 7, "nonzero stream position restored");
        }
        using (var stream = new MemoryStream(new byte[40]))
        {
            stream.Position = 9;
            Reject(() => QrInput.FromDataObject(Data("PNG", stream)), "malformed PNG");
            Check(stream.Position == 9 && stream.CanRead, "malformed stream position restored");
        }
        var mixed = Data("PNG", png);
        mixed.SetData(DataFormats.Html, "<img src='https://example.invalid/qr.png'>", false);
        mixed.SetData(DataFormats.UnicodeText, "https://example.invalid/qr.png", false);
        Check(QrInput.FromDataObject(mixed) == Pairing, "PNG preferred over HTML and image URL");
        mixed = Data("PNG", new byte[40]); mixed.SetData(DataFormats.Bitmap, bitmap, false);
        Check(QrInput.FromDataObject(mixed) == Pairing, "malformed PNG can use valid native bitmap");
        mixed = Data("PNG", new byte[40]); mixed.SetData(DataFormats.UnicodeText, "synthetic-token", false);
        Check(QrInput.FromDataObject(mixed) == "synthetic-token", "malformed image can use valid text");
        Reject(() => QrInput.FromDataObject(Data(DataFormats.Html, "<img src='https://example.invalid/qr.png'>")), "HTML is not fetched");
        Reject(() => QrInput.FromDataObject(Data(DataFormats.FileDrop, new[] { "synthetic-file.png" })), "file drop is not loaded");
        Reject(() => QrInput.FromDataObject(Data(DataFormats.UnicodeText, "https://example.invalid/qr.png")), "image URL is not fetched");
        Reject(() => QrInput.FromDataObject(Data(DataFormats.UnicodeText, new string('x', 8193))), "oversized text rejected");
        Reject(() => QrInput.FromDataObject(Data("PNG", new byte[QrInput.MaxImageBytes + 1])), "oversized PNG bytes rejected", "32 MB");
        using (var stream = new TooLargeStream())
        {
            stream.Position = 17;
            Reject(() => QrInput.FromDataObject(Data("PNG", stream)), "oversized stream rejected before reading", "32 MB");
            Check(stream.Reads == 0 && stream.Position == 17, "oversized stream not read or repositioned");
        }
        byte[] hostile = (byte[])png.Clone();
        hostile[16] = hostile[17] = hostile[18] = hostile[19] = 255;
        Reject(() => QrInput.FromDataObject(Data("PNG", hostile)), "oversized PNG header rejected before decode", "16 megapixels");
        int width = 4001, height = 4000, stride = (width + 7) / 8;
        var oversizedBitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.BlackWhite, null, new byte[stride * height], stride);
        Reject(() => QrInput.DecodeBitmap(oversizedBitmap), "oversized native bitmap rejected", "16 megapixels");
        Reject(() => QrInput.DecodeBitmap(BitmapSource.Create(20, 20, 96, 96, PixelFormats.Bgra32, null, new byte[1600], 80)), "image without QR rejected");
        Reject(() => QrInput.DecodeBitmap(MakeQr("https://example.invalid/credential")), "QR with unrelated URL rejected");
        Reject(() => QrInput.FromDataObject(null), "null clipboard data rejected");
        Check(QrInput.DecodeBitmap(MakeQr(Pairing, true)) == Pairing, "transparent background composites onto white");
        Check(QrInput.FromDataObject(Data("PNG", EncodePng(MakeQr(Pairing, true)))) == Pairing, "Firefox transparent PNG bytes decode over white");
        using (var stream = new MemoryStream(EncodePng(MakeQr(Pairing, true))))
            Check(QrInput.FromDataObject(Data("PNG", stream)) == Pairing && stream.Position == 0, "Firefox transparent PNG stream decodes and restores position");
        Console.WriteLine("QR input: " + assertions + " assertions passed (STA synthetic images; real clipboard untouched).");
    }
    static DataObject Data(string format, object value)
    { var data = new DataObject(); data.SetData(format, value, false); return data; }
    static BitmapSource MakeQr(string value, bool transparent = false)
    {
        var writer = new BarcodeWriterPixelData { Format = BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 260, Height = 260, Margin = 4 } };
        var qr = writer.Write(value);
        if (transparent) for (int i = 0; i < qr.Pixels.Length; i += 4) if (qr.Pixels[i] == 255)
            qr.Pixels[i] = qr.Pixels[i + 1] = qr.Pixels[i + 2] = qr.Pixels[i + 3] = 0;
        var bitmap = BitmapSource.Create(qr.Width, qr.Height, 96, 96, PixelFormats.Bgra32, null, qr.Pixels, qr.Width * 4);
        bitmap.Freeze(); return bitmap;
    }
    static byte[] EncodePng(BitmapSource bitmap)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray(); }
    static void Check(bool value, string label)
    { assertions++; if (!value) throw new Exception("QR input check failed: " + label); }
    static void Reject(Action action, string label, string messagePart = null)
    {
        try { action(); }
        catch (ArgumentException ex)
        {
            Check(messagePart == null || ex.Message.Contains(messagePart, StringComparison.Ordinal), label);
            Check(!ex.ToString().Contains(Pairing, StringComparison.Ordinal) && !ex.ToString().Contains("example.invalid", StringComparison.Ordinal), "rejection does not expose clipboard content");
            return;
        }
        throw new Exception("QR input unexpectedly accepted: " + label);
    }
    sealed class TooLargeStream : Stream
    {
        public int Reads;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => QrInput.MaxImageBytes + 1L;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; throw new IOException("Should not read an oversized stream."); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
