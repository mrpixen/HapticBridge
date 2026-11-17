using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Runtime.InteropServices;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
        void InitializeTheme()
        {
            window.FontFamily=UiFonts.Body;
            window.SourceInitialized+=(sender,e)=> { int dark=1;DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,20,ref dark,sizeof(int)); };
            window.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,e)=> {
                if(!SystemParameters.ClientAreaAnimation)return;
                var button=e.OriginalSource as Button;if(button==null)return;
                var layer=button.Template.FindName("Glimmer",button) as Border;if(layer==null)return;
                Point point=button.IsMouseOver?Mouse.GetPosition(button):new Point(button.ActualWidth/2,button.ActualHeight/2);
                var brush=new RadialGradientBrush(System.Windows.Media.Color.FromArgb(100,240,218,255),Colors.Transparent);
                double x=Math.Clamp(point.X/Math.Max(1,button.ActualWidth),0,1),y=Math.Clamp(point.Y/Math.Max(1,button.ActualHeight),0,1);
                brush.GradientOrigin=brush.Center=new Point(x,y);brush.RadiusX=.55;brush.RadiusY=1;layer.Background=brush;
                var movement=new PointAnimation(new Point(x,y),new Point(.5+(0.5-x)*.4,.5+(0.5-y)*.4),TimeSpan.FromMilliseconds(1500)) { EasingFunction=new CubicEase { EasingMode=EasingMode.EaseInOut } };
                brush.BeginAnimation(RadialGradientBrush.CenterProperty,movement);brush.BeginAnimation(RadialGradientBrush.GradientOriginProperty,movement);
                var light=new DoubleAnimationUsingKeyFrames();light.KeyFrames.Add(new LinearDoubleKeyFrame(0,TimeSpan.Zero));light.KeyFrames.Add(new EasingDoubleKeyFrame(.8,TimeSpan.FromMilliseconds(320)));light.KeyFrames.Add(new EasingDoubleKeyFrame(.45,TimeSpan.FromMilliseconds(800)));light.KeyFrames.Add(new EasingDoubleKeyFrame(0,TimeSpan.FromMilliseconds(1500)));layer.BeginAnimation(UIElement.OpacityProperty,light);
            }));
        }
    }
}
