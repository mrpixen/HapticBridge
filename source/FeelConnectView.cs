using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZXing;

namespace LovenseConnector
{
    internal sealed class FeelRoute
    {
        public Device Device; public int Channel; public CheckBox Enabled; public Slider Gain; public Border Card;
    }
    internal sealed partial class ConnectorApp
    {
        readonly FeelConnectClient feelClient=new FeelConnectClient();
        readonly Dictionary<string,FeelRoute> feelRoutes=new Dictionary<string,FeelRoute>();
        readonly Dictionary<string,Tuple<Playback,int>> feelOwned=new Dictionary<string,Tuple<Playback,int>>();
        readonly SemaphoreSlim feelWriteGate=new SemaphoreSlim(1,1);
        PasswordBox feelPairing; TextBlock feelStatus,feelValue,feelCount; ProgressBar feelMeter;
        Button feelOutputButton; WrapPanel feelRoutePanel; TextBox feelSmoothing;
        DispatcherTimer feelTimer; CancellationTokenSource feelPairCancel;
        bool feelArmed,feelConnected,feelTickBusy; double feelPercent; int feelGeneration,feelFrame;
        TabItem feelTab;
        void InitializeFeelConnect()
        {
            var body=new StackPanel { Margin=new Thickness(24) };
            body.Children.Add(Text("Video sync",32));
            feelStatus=Text("Disconnected",14);feelStatus.Foreground=Color("#C4BAD7");body.Children.Add(feelStatus);
            body.Children.Add(Text("FeelConnect pairing",16));
            feelPairing=new PasswordBox { MaxLength=8192,Margin=new Thickness(0,4,0,14) };
            System.Windows.Automation.AutomationProperties.SetName(feelPairing,"FeelConnect pairing value");body.Children.Add(feelPairing);
            var pairingActions=new WrapPanel();
            pairingActions.Children.Add(Action("Paste QR or code",()=> { PasteFeelPairing(); return Task.CompletedTask; }));
            pairingActions.Children.Add(Action("Open QR image…",()=> { OpenFeelImage(); return Task.CompletedTask; }));
            pairingActions.Children.Add(Action("Connect",PairFeelConnect));
            pairingActions.Children.Add(Action("Disconnect",DisconnectFeelConnect));body.Children.Add(pairingActions);
            var monitor=new StackPanel { Margin=new Thickness(0,26,0,20) };
            var line=new DockPanel();feelValue=Text("0%",28);DockPanel.SetDock(feelValue,Dock.Right);line.Children.Add(feelValue);line.Children.Add(Text("Video intensity",18));monitor.Children.Add(line);
            feelMeter=new ProgressBar { Minimum=0,Maximum=100,Height=8,Foreground=Color("#CCB8FF"),Background=Color("#44364F"),BorderThickness=new Thickness(0),Margin=new Thickness(0,8,0,12) };monitor.Children.Add(feelMeter);
            feelCount=Text("Waiting for cues",12);feelCount.Foreground=Color("#C4BAD7");monitor.Children.Add(feelCount);
            feelOutputButton=Action("Enable output",ToggleFeelOutput);feelOutputButton.HorizontalAlignment=HorizontalAlignment.Left;feelOutputButton.IsEnabled=false;monitor.Children.Add(feelOutputButton);body.Children.Add(monitor);
            body.Children.Add(Text("Motors",19));feelRoutePanel=new WrapPanel();body.Children.Add(feelRoutePanel);
            var timing=new WrapPanel { Margin=new Thickness(0,12,0,0) };feelSmoothing=Input("100",120);Field(timing,"Smoothing (ms)",feelSmoothing);
            body.Children.Add(new Expander { Header="Timing",Content=timing,Margin=new Thickness(0,14,0,0) });
            feelTab=new TabItem { Header="Video sync",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };tabs.Items.Insert(2,feelTab);
            feelClient.StatusChanged+=status=>UI(async()=> {
                feelConnected=status.Connected;feelStatus.Text=status.Message;feelCount.Text=status.MessagesReceived+" messages received";
                feelOutputButton.IsEnabled=feelConnected&&!closing;
                if(!status.Connected) { feelPercent=0;feelFrame++;feelValue.Text="0%";feelMeter.Value=0;await DisarmFeelConnect(); }
            });
            feelClient.OutputChanged+=output=>UI(async()=> {
                feelPercent=output.IsStop?0:output.Percent;feelFrame++;
                feelValue.Text=Math.Round(feelPercent)+"%";feelMeter.Value=feelPercent;
                if(output.IsStop)await StopFeelOwned();
            });
            feelTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };feelTimer.Tick+=async(s,e)=>await FeelTick();feelTimer.Start();
        }
        void PasteFeelPairing()
        {
            if(Clipboard.ContainsImage()) { feelPairing.Password=DecodeFeelQr(Clipboard.GetImage());feelStatus.Text="QR code ready"; }
            else if(Clipboard.ContainsText()) { string value=Clipboard.GetText();FeelConnectPairing.Parse(value);feelPairing.Password=value;feelStatus.Text="Pairing code ready"; }
            else throw new InvalidOperationException("Copy a QR image or pairing code first.");
        }
        void OpenFeelImage()
        {
            var dialog=new OpenFileDialog { Filter="QR images|*.png;*.jpg;*.jpeg;*.bmp",Title="Open FeelConnect QR" };
            if(dialog.ShowDialog(window)!=true)return;
            if(new FileInfo(dialog.FileName).Length>32*1024*1024)throw new ArgumentException("Choose an image smaller than 32 MB.");
            using(var stream=File.OpenRead(dialog.FileName)) {
                var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();
                feelPairing.Password=DecodeFeelQr(bitmap);feelStatus.Text="QR code ready";
            }
        }
        internal static string DecodeFeelQr(BitmapSource bitmap)
        {
            if(bitmap==null||bitmap.PixelWidth<1||bitmap.PixelHeight<1||(long)bitmap.PixelWidth*bitmap.PixelHeight>16000000)throw new ArgumentException("Choose a QR image smaller than 16 megapixels.");
            var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);int stride=checked(converted.PixelWidth*4);var pixels=new byte[checked(stride*converted.PixelHeight)];converted.CopyPixels(pixels,stride,0);
            var reader=new BarcodeReaderGeneric { AutoRotate=true,Options=new ZXing.Common.DecodingOptions { TryHarder=true,TryInverted=true,PossibleFormats=new[]{BarcodeFormat.QR_CODE} } };
            var result=reader.Decode(pixels,converted.PixelWidth,converted.PixelHeight,RGBLuminanceSource.BitmapFormat.BGRA32);
            if(result==null)throw new ArgumentException("No QR code found. Use a clear image with the complete code.");
            FeelConnectPairing.Parse(result.Text);return result.Text;
        }
        async Task PairFeelConnect()
        {
            string pairing=feelPairing.Password;FeelConnectPairing.Parse(pairing);feelPairing.Clear();
            await DisconnectFeelConnect();
            var cancel=new CancellationTokenSource();feelPairCancel=cancel;
            try { await feelClient.PairAsync(pairing,devices.Values.Where(d=>d.Connected).Select(d=>d.Name).Distinct(),cancel.Token); }
            catch(OperationCanceledException) { }
            catch { if(!cancel.IsCancellationRequested)feelStatus.Text="Pairing failed. Generate a fresh QR code and try again."; }
            finally { if(ReferenceEquals(feelPairCancel,cancel))feelPairCancel=null;cancel.Dispose(); }
        }
        async Task DisconnectFeelConnect()
        {
            operatorGeneration++;feelConnected=false;feelPercent=0;feelFrame++;feelPairCancel?.Cancel();
            feelMeter.Value=0;feelValue.Text="0%";feelStatus.Text="Disconnected";feelOutputButton.IsEnabled=false;
            await DisarmFeelConnect();await feelClient.DisconnectAsync();
        }
        async Task ToggleFeelOutput()
        {
            if(feelArmed) { operatorGeneration++;await DisarmFeelConnect();return; }
            if(!feelConnected)throw new InvalidOperationException("Connect a video session first.");
            if(!feelRoutes.Values.Any(r=>r.Enabled.IsChecked==true&&r.Device.Connected))throw new InvalidOperationException("Connect and select at least one motor.");
            if(sweep!=null)throw new InvalidOperationException("Stop the speed test first.");
            int smoothing=Number(feelSmoothing);if(smoothing<0||smoothing>2000)throw new ArgumentException("Smoothing must be 0–2000 ms.");
            int operation=++operatorGeneration;
            var sources=PauseExternalSources();var stops=StopPlayers();await Task.WhenAll(sources,stops);
            if(closing||operation!=operatorGeneration||!feelConnected)return;
            if(!await stops)throw new InvalidOperationException("Output could not be stopped before switching sources.");
            feelPercent=feelClient.CurrentPercent;feelValue.Text=Math.Round(feelPercent)+"%";feelMeter.Value=feelPercent;
            feelFrame++;feelArmed=true;feelGeneration++;feelOutputButton.Content="Pause output";hint.Text="Video output enabled";
        }
        async Task DisarmFeelConnect()
        {
            feelGeneration++;feelArmed=false;
            if(feelOutputButton!=null)feelOutputButton.Content="Enable output";
            await StopFeelOwned();
        }
        async Task StopFeelOwned()
        {
            await feelWriteGate.WaitAsync();
            try {
                var owned=feelOwned.Values.ToArray();feelOwned.Clear();
                await Task.WhenAll(owned.Select(async item=> { try { await item.Item1.Stop(item.Item2); }catch { hint.Text="Video output stopped; a disconnected motor could not confirm Stop."; } }));
            }finally { feelWriteGate.Release(); }
        }
        void RefreshFeelRoutes()
        {
            if(feelRoutePanel==null)return;
            foreach(var d in devices.Values)foreach(var channel in d.Motors.Keys) {
                string key=MixerKey(d,channel);if(feelRoutes.ContainsKey(key))continue;
                var route=new FeelRoute { Device=d,Channel=channel };var panel=new StackPanel();
                route.Card=new Border { Width=226,Padding=new Thickness(18),CornerRadius=new CornerRadius(20),Background=Color("#A0241935"),Margin=new Thickness(0,8,12,8),Child=panel };
                var heading=new StackPanel { MaxWidth=152 };
                heading.Children.Add(new TextBlock { Text=d.Name,TextWrapping=TextWrapping.Wrap,FontSize=16 });
                heading.Children.Add(new TextBlock { Text=channel==0?"Motor":"Motor "+channel,FontSize=12,Margin=new Thickness(0,4,0,0),Foreground=Color("#C4BAD7") });
                route.Enabled=new CheckBox { Content=heading,IsChecked=true };panel.Children.Add(route.Enabled);
                System.Windows.Automation.AutomationProperties.SetName(route.Enabled,d.Name+" motor "+channel+" video routing");
                var value=Text("Strength 100%",13);panel.Children.Add(value);
                route.Gain=new Slider { Minimum=0,Maximum=200,Value=100,TickFrequency=5,SmallChange=5,IsSnapToTickEnabled=true };
                System.Windows.Automation.AutomationProperties.SetName(route.Gain,d.Name+" motor "+channel+" video strength");
                route.Gain.ValueChanged+=(s,e)=>value.Text="Strength "+(int)route.Gain.Value+"%";panel.Children.Add(route.Gain);
                feelRoutes.Add(key,route);feelRoutePanel.Children.Add(route.Card);
            }
            foreach(var route in feelRoutes.Values)route.Card.IsEnabled=route.Device.Connected&&!closing;
        }
        async Task FeelTick()
        {
            if(!feelArmed||!feelConnected||feelTickBusy)return;feelTickBusy=true;int generation=feelGeneration,frame=feelFrame;
            try {
                int smoothing=Number(feelSmoothing);if(smoothing<0||smoothing>2000)throw new ArgumentException("Smoothing must be 0–2000 ms.");
                await feelWriteGate.WaitAsync();
                try {
                    foreach(var pair in feelRoutes.ToArray()) {
                        if(!feelArmed||!feelConnected||generation!=feelGeneration||frame!=feelFrame)return;
                        var route=pair.Value;var player=route.Device.Player;
                        if(!route.Device.Connected||player==null)continue;
                        int level=route.Enabled.IsChecked==true?Math.Clamp((int)Math.Round(feelPercent*route.Gain.Value/500.0,MidpointRounding.AwayFromZero),0,20):0;
                        if(level==0) { if(feelOwned.Remove(pair.Key))await player.Stop(route.Channel); }
                        else {
                            feelOwned[pair.Key]=Tuple.Create(player,route.Channel);
                            if(!player.UpdateSteady(route.Channel,level,600,smoothing))await player.Start(route.Channel,new PatternSpec { mode="Steady",high=level,durationMs=600,transitionMs=smoothing },20);
                        }
                    }
                }finally { feelWriteGate.Release(); }
            }catch(Exception ex) { await DisarmFeelConnect();Report(ex); }
            finally { feelTickBusy=false; }
        }
        async Task CloseFeelConnect()
        {
            feelTimer?.Stop();feelPairCancel?.Cancel();await DisarmFeelConnect();await feelClient.DisposeAsync();
        }
    }
}
