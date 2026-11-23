using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LovenseConnector
{
    internal sealed class HudPreview : FrameworkElement
    {
        BitmapSource bitmap;
        Point? drag;
        Point current;
        public ScreenProfile Profile;
        public bool CanSelect;
        public event Action<HudRegion> Selected;
        public HudPreview(){Height=280;MinHeight=160;Cursor=Cursors.Cross;Focusable=true;ClipToBounds=true;}
        public void Show(ScreenPixels frame)
        {
            bitmap=frame==null?null:BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Bgra,frame.Width*4);
            bitmap?.Freeze();InvalidateVisual();
        }
        Rect View {
            get { if(bitmap==null)return new Rect();double scale=Math.Min(ActualWidth/bitmap.PixelWidth,ActualHeight/bitmap.PixelHeight);return new Rect((ActualWidth-bitmap.PixelWidth*scale)/2,(ActualHeight-bitmap.PixelHeight*scale)/2,bitmap.PixelWidth*scale,bitmap.PixelHeight*scale); }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(10,8,20)),null,new Rect(0,0,ActualWidth,ActualHeight),16,16);
            if(bitmap==null){Label(dc,"Choose a window, then start preview",new Point(20,24),Brushes.Lavender);return;}
            var view=View;dc.DrawImage(bitmap,view);
            DrawRegion(dc,Profile.Health,Brushes.Violet,"Health",view);DrawRegion(dc,Profile.Badge,Brushes.Aquamarine,"Kill badge",view);
            if(Profile.ReadShield)DrawRegion(dc,Profile.Shield,Brushes.SkyBlue,"Shield",view);
            if(drag.HasValue)dc.DrawRectangle(null,new Pen(Brushes.White,2),new Rect(drag.Value,current));
        }
        void DrawRegion(DrawingContext dc,HudRegion region,Brush color,string label,Rect view)
        {
            if(region==null||!region.Valid)return;
            var rect=new Rect(view.X+region.X*view.Width,view.Y+region.Y*view.Height,region.Width*view.Width,region.Height*view.Height);
            dc.DrawRectangle(null,new Pen(color,1.5),rect);Label(dc,label,new Point(rect.X,Math.Max(view.Y,rect.Y-20)),color);
        }
        void Label(DrawingContext dc,string text,Point point,Brush color)=>dc.DrawText(new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,color,VisualTreeHelper.GetDpi(this).PixelsPerDip),point);
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);if(!CanSelect||bitmap==null||!View.Contains(e.GetPosition(this)))return;
            Focus();drag=current=e.GetPosition(this);CaptureMouse();e.Handled=true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);if(!drag.HasValue)return;var p=e.GetPosition(this);var v=View;current=new Point(Math.Clamp(p.X,v.Left,v.Right),Math.Clamp(p.Y,v.Top,v.Bottom));InvalidateVisual();
        }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);if(!drag.HasValue)return;var rect=new Rect(drag.Value,current);var view=View;drag=null;ReleaseMouseCapture();
            var region=new HudRegion { X=(rect.X-view.X)/view.Width,Y=(rect.Y-view.Y)/view.Height,Width=rect.Width/view.Width,Height=rect.Height/view.Height };
            if(region.Valid)Selected?.Invoke(region);InvalidateVisual();e.Handled=true;
        }
        protected override void OnLostMouseCapture(MouseEventArgs e){drag=null;InvalidateVisual();base.OnLostMouseCapture(e);}
    }
    internal static class HudDefaultGlyphs
    {
        public static HudGlyph[] Create()
        {
            var list=new List<HudGlyph>();
            foreach(string font in new[]{"Bahnschrift","Arial","Segoe UI"})foreach(var weight in new[]{FontWeights.Normal,FontWeights.Bold})for(int i=0;i<=9;i++) {
                var visual=new DrawingVisual();using(var dc=visual.RenderOpen()) {
                    dc.DrawRectangle(Brushes.Black,null,new Rect(0,0,100,100));
                    dc.DrawText(new FormattedText(i.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily(font),FontStyles.Normal,weight,FontStretches.Normal),64,Brushes.White,1),new Point(8,8));
                }
                var bitmap=new RenderTargetBitmap(100,100,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var bytes=new byte[40000];bitmap.CopyPixels(bytes,400,0);
                var frame=new ScreenPixels(bytes,100,100,0);var parts=HudVision.Segment(HudVision.Extract(frame,new HudRegion { Width=1,Height=1 }));
                if(parts.Length==1)list.Add(new HudGlyph { Digit=i,Aspect=(double)parts[0].Width/parts[0].Height,Mask=HudVision.Normalize(parts[0]) });
            }
            return list.ToArray();
        }
    }
}
