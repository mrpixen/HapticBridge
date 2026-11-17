using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LovenseConnector
{
    // A history of successfully submitted commands, never editable or physical telemetry.
    internal sealed class LiveMotorTrace : FrameworkElement
    {
        readonly Queue<int> samples=new Queue<int>();
        public Brush Accent { get; set; }=new SolidColorBrush(System.Windows.Media.Color.FromRgb(204,184,255));
        public LiveMotorTrace() { Height=76;MinWidth=100;IsHitTestVisible=false; }
        public void Push(int value) { samples.Enqueue(value);while(samples.Count>80)samples.Dequeue();InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);double w=ActualWidth,h=ActualHeight;if(w<1||h<1)return;
            var grid=new Pen(new SolidColorBrush(System.Windows.Media.Color.FromArgb(25,220,205,245)),1);
            dc.DrawLine(grid,new Point(0,h-16),new Point(w,h-16));dc.DrawLine(grid,new Point(0,8),new Point(w,8));
            var pen=new Pen(Accent,2);var values=samples.ToArray();Point? last=null;
            for(int i=0;i<values.Length;i++) {
                if(values[i]<0){last=null;continue;}
                var point=new Point(w*(80-values.Length+i)/79.0,h-16-values[i]/20.0*(h-24));
                if(last.HasValue)dc.DrawLine(pen,last.Value,point);last=point;
            }
            var label=new FormattedText("Sent · last 8 s",CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
                new Typeface(UiFonts.Body,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),11,
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(196,186,215)),VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label,new Point(0,h-13));
        }
    }
}
