using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace LovenseConnector
{
    internal static class ScreenCalibrationImage
    {
        internal static ScreenPixels Load(string path)
        {
            using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(stream.Length>32*1024*1024)throw new ArgumentException("Choose a HUD image smaller than 32 MB.");
            try { return Decode(stream); }
            catch(Exception ex) when(ex is NotSupportedException||ex is FileFormatException) {
                throw new ArgumentException("This image could not be read. Choose a valid PNG, JPEG or BMP.",ex);
            }
        }
        static ScreenPixels Decode(Stream stream)
        {
            var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnDemand);
            if(!(decoder is PngBitmapDecoder)&&!(decoder is JpegBitmapDecoder)&&!(decoder is BmpBitmapDecoder))throw new ArgumentException("Choose a PNG, JPEG or BMP image.");
            var frame=decoder.Frames[0];int w=frame.PixelWidth,h=frame.PixelHeight;
            if(w<8||h<8||w>8192||h>4320||(long)w*h>16000000)throw new ArgumentException("Choose an image up to 8192 × 4320 and 16 million pixels.");
            var converted=new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);var bytes=new byte[w*h*4];converted.CopyPixels(bytes,w*4,0);
            return new ScreenPixels(bytes,w,h,0);
        }
    }
}
