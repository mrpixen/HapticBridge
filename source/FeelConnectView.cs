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
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LovenseConnector
{
    internal sealed class FeelRoute
    {
        public Device Device; public int Channel; public CheckBox Enabled; public Slider Gain; public Border Card;
        public OrderedRouteSettings OutputSettings=new(false,100);
    }
    internal sealed partial class ConnectorApp
    {
        readonly FeelConnectClient feelClient=new FeelConnectClient();
        readonly Dictionary<string,FeelRoute> feelRoutes=new Dictionary<string,FeelRoute>();
        readonly Dictionary<string,Tuple<Playback,int>> feelOwned=new Dictionary<string,Tuple<Playback,int>>();
        readonly SemaphoreSlim feelWriteGate=new SemaphoreSlim(1,1);
        PasswordBox feelPairing; TextBlock feelStatus,feelValue,feelCount,feelDetails,feelPresenceResult; ProgressBar feelMeter;
        TextBlock feelPairHint,feelOutputState,feelRouteSummary,feelRouteEmpty;
        Button feelOutputButton,feelConnectButton,feelDisconnectButton; WrapPanel feelRoutePanel; TextBox feelSmoothing; ComboBox feelPlaybackMode;
        DispatcherTimer feelTimer; CancellationTokenSource feelPairCancel;
        bool feelArmed,feelConnected,feelTickBusy,feelPresenceBusy,feelModeBusy,feelOutputPaused; double feelPercent; int feelGeneration,feelFrame,feelPresenceRevision;
        DateTimeOffset feelPresenceRetryAt;
        FeelConnectPresenceReport feelPresenceReport;
        FeelConnectRouteOccupancyReport feelReferenceReport;
        FeelConnectOutput feelLatestOutput;
        FeelOrderedRouter feelOrdered;
        FeelConnectOutput feelIngressOutput;
        int feelOrderedInput,feelDisplayQueued,feelInputAvailable;
        TabItem feelTab;
        void InitializeFeelConnect()
        {
            feelOrdered=new FeelOrderedRouter();
            var body=new StackPanel { Margin=new Thickness(24) };
            body.Children.Add(new TextBlock { Text="Video sync",FontSize=32,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,24) });
            var pairingBody=new StackPanel();
            feelStatus=Text("Not paired",19);feelStatus.Margin=new Thickness(0,0,0,10);pairingBody.Children.Add(feelStatus);
            var pairingLabel=Text("Pairing code",13);pairingLabel.Margin=new Thickness(0,0,0,5);pairingBody.Children.Add(pairingLabel);
            feelPairing=new PasswordBox { MaxLength=8192,Margin=new Thickness(0,0,0,10) };
            feelPairing.PasswordChanged+=(s,e)=>RefreshFeelPairingActions();
            System.Windows.Automation.AutomationProperties.SetName(feelPairing,"FeelConnect pairing value");pairingBody.Children.Add(feelPairing);
            feelPairing.ToolTip="Paste a QR image or pairing code (Ctrl+V)";
            feelPairing.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste,(s,e)=> { PasteFeelPairingSafely();e.Handled=true; },(s,e)=> { e.CanExecute=true;e.Handled=true; }));
            var pairingMenu=new ContextMenu();var pasteItem=new MenuItem { Header="Paste QR or code",Command=ApplicationCommands.Paste,CommandTarget=feelPairing };pairingMenu.Items.Add(pasteItem);feelPairing.ContextMenu=pairingMenu;
            var pairingActions=new WrapPanel();
            pairingActions.Children.Add(Action("Paste QR or code",()=> { PasteFeelPairing(); return Task.CompletedTask; }));
            pairingActions.Children.Add(Action("Open image…",async()=> {
                if(!await StopAll())throw new InvalidOperationException("Could not confirm Stop before opening the image picker.");
                OpenFeelImage();
            }));
            feelConnectButton=Action("Connect",PairFeelConnect);feelConnectButton.Background=Color("#806C5095");pairingActions.Children.Add(feelConnectButton);
            feelDisconnectButton=Action("Disconnect",DisconnectFeelConnect);pairingActions.Children.Add(feelDisconnectButton);pairingBody.Children.Add(pairingActions);
            foreach(FrameworkElement action in pairingActions.Children)action.Margin=new Thickness(0,0,8,0);
            feelPairHint=Text("Ctrl+V · or drop a QR image here",13);feelPairHint.Margin=new Thickness(0,8,0,0);feelPairHint.Foreground=Color("#C4BAD7");pairingBody.Children.Add(feelPairHint);
            var pairingCard=FeelSurface(pairingBody);pairingCard.AllowDrop=true;
            pairingCard.PreviewDragOver+=(s,e)=> { e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop,false)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true; };
            pairingCard.Drop+=(s,e)=> {
                e.Handled=true;
                try {
                    var files=e.Data.GetData(DataFormats.FileDrop,false) as string[];
                    if(files==null||files.Length!=1)throw new ArgumentException("Drop one local QR image.");
                    LoadFeelImage(files[0]);
                }catch(Exception ex) { feelPairHint.Text=ex.Message; }
            };body.Children.Add(pairingCard);

            var monitor=new StackPanel();
            var line=new DockPanel();feelValue=Text("—",32);feelValue.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(feelValue,Dock.Right);line.Children.Add(feelValue);
            var signalHeading=new StackPanel();var signalTitle=Text("Video signal",20);signalTitle.Margin=new Thickness(0,0,0,4);signalHeading.Children.Add(signalTitle);
            feelCount=Text("Waiting for cues",14);feelCount.Margin=new Thickness(0);feelCount.Foreground=Color("#C4BAD7");signalHeading.Children.Add(feelCount);line.Children.Add(signalHeading);monitor.Children.Add(line);
            feelMeter=new ProgressBar { Minimum=0,Maximum=100,Height=7,Foreground=Color("#CCB8FF"),Background=Color("#44364F"),BorderThickness=new Thickness(0),Margin=new Thickness(0,12,0,0) };monitor.Children.Add(feelMeter);
            System.Windows.Automation.AutomationProperties.SetName(feelMeter,"Incoming video intensity");
            var outputRow=new DockPanel { Margin=new Thickness(0,14,0,0) };
            feelOutputButton=Action("Enable output",ToggleFeelOutput);feelOutputButton.Margin=new Thickness(12,0,0,0);feelOutputButton.IsEnabled=false;DockPanel.SetDock(feelOutputButton,Dock.Right);outputRow.Children.Add(feelOutputButton);
            feelOutputState=Text("Output off",15);feelOutputState.VerticalAlignment=VerticalAlignment.Center;outputRow.Children.Add(feelOutputState);monitor.Children.Add(outputRow);body.Children.Add(FeelSurface(monitor));

            var routeBody=new StackPanel();
            var routeActions=new WrapPanel { Margin=new Thickness(0,12,0,0) };
            routeActions.Children.Add(Action("Select all",()=> { foreach(var r in feelRoutes.Values.Where(r=>r.Device.HasChannel(r.Channel)))r.Enabled.IsChecked=true;RefreshFeelRouteSummary();return Task.CompletedTask; }));
            routeActions.Children.Add(Action("Clear",()=> { foreach(var r in feelRoutes.Values)r.Enabled.IsChecked=false;RefreshFeelRouteSummary();return Task.CompletedTask; }));routeBody.Children.Add(routeActions);
            feelRouteEmpty=Text("Connect a device to choose motors.",14);feelRouteEmpty.Foreground=Color("#C4BAD7");routeBody.Children.Add(feelRouteEmpty);
            feelRoutePanel=new WrapPanel();routeBody.Children.Add(feelRoutePanel);
            feelRouteSummary=Text("Play targets · no motors connected",16);
            body.Children.Add(new Expander { Header=feelRouteSummary,Content=routeBody,Margin=new Thickness(0,4,0,12) });
            var playbackSettings=new StackPanel();
            var timing=new WrapPanel { Margin=new Thickness(0,12,0,0) };feelSmoothing=Input("100",120);feelSmoothing.ToolTip="Applies to timed cues; legacy batches are sent in order.";Field(timing,"Cue smoothing (ms)",feelSmoothing);playbackSettings.Children.Add(timing);
            var delivery=new WrapPanel();feelPlaybackMode=new ComboBox { Width=220,Margin=new Thickness(0,0,8,6) };
            feelPlaybackMode.Items.Add("Timed cues");feelPlaybackMode.Items.Add("Live levels (legacy)");feelPlaybackMode.SelectedIndex=0;
            feelPlaybackMode.ToolTip="Live levels can help with older players. Output stops if updates pause for five seconds.";
            System.Windows.Automation.AutomationProperties.SetName(feelPlaybackMode,"Video playback mode");
            Field(delivery,"Playback mode",feelPlaybackMode);
            var applyMode=Action("Apply mode",ApplyFeelPlaybackMode);applyMode.VerticalAlignment=VerticalAlignment.Bottom;delivery.Children.Add(applyMode);playbackSettings.Children.Add(delivery);
            body.Children.Add(new Expander { Header="Playback settings",Content=playbackSettings,Margin=new Thickness(0,14,0,0) });
            feelDetails=Text("No session",12);feelDetails.TextWrapping=TextWrapping.Wrap;
            var connectionDetails=new StackPanel();connectionDetails.Children.Add(feelDetails);
            var checkWebsite=Action("Check website",CheckFeelWebsite);checkWebsite.HorizontalAlignment=HorizontalAlignment.Left;connectionDetails.Children.Add(checkWebsite);
            var checkOldRoute=Action("Check old SDK route",CheckFeelReferenceRoute);checkOldRoute.HorizontalAlignment=HorizontalAlignment.Left;connectionDetails.Children.Add(checkOldRoute);
            feelPresenceResult=Text("",12);feelPresenceResult.TextWrapping=TextWrapping.Wrap;connectionDetails.Children.Add(feelPresenceResult);
            body.Children.Add(new Expander { Header="Connection details",Content=connectionDetails,Margin=new Thickness(0,6,0,0) });
            feelTab=new TabItem { Header="Video sync",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };tabs.Items.Insert(2,feelTab);
            window.PreviewKeyDown+=(s,e)=> {
                bool paste=(e.Key==Key.V&&(Keyboard.Modifiers&ModifierKeys.Control)!=0)||(e.Key==Key.Insert&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0);
                if(paste&&tabs.SelectedItem==feelTab&&!(Keyboard.FocusedElement is TextBox)) { e.Handled=true;PasteFeelPairingSafely(); }
            };
            feelClient.StatusChanged+=status=> {
                Volatile.Write(ref feelInputAvailable,status.Connected?1:0);
                if(!status.Connected)ObserveFeelOrderedStop(feelOrdered.Reset(true));
                UI(async()=> {
                string website=feelClient.WebsiteName;
                feelConnected=status.Connected;feelStatus.Text=status.Connected?(website.Length==0?"Paired":"Paired · "+website):status.Message;
                if(!status.Connected)feelLatestOutput=null;
                feelCount.Text=status.Connected?FeelSignalText():status.Phase;
                feelOutputButton.IsEnabled=feelConnected&&!closing;
                if(!status.Connected||status.Phase=="Refreshing") { feelPresenceRevision++;feelPresenceReport=null;feelReferenceReport=null;feelPresenceResult.Text="";feelPresenceRetryAt=default; }
                if(!status.Connected) { feelPercent=0;feelFrame++;await DisarmFeelConnect(); }
                RefreshFeelSignalDisplay();RefreshFeelPairingActions();
                });
            };
            feelClient.OutputChanged+=ReceiveFeelOutput;
            feelTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };feelTimer.Tick+=async(s,e)=> { RefreshFeelDetails();await RefreshFeelPresence();await FeelTick(); };feelTimer.Start();
            RefreshFeelPairingActions();
        }
        void ObserveFeelOrderedStop(Task task)
        {
            _=task.ContinueWith(t=> { if(t.IsFaulted) { _=t.Exception;UI(()=>hint.Text="Video output stopped; a disconnected motor could not confirm Stop."); } },TaskScheduler.Default);
        }
        void ReceiveFeelOutput(FeelConnectOutput output)
        {
            // This executes on the input thread. Stop invalidation and bounded
            // batch admission must not wait behind UI rendering or modal dialogs.
            Volatile.Write(ref feelIngressOutput,output);
            Volatile.Write(ref feelOrderedInput,output.Samples.Count>0?1:0);
            if(output.Samples.Count>0)feelOrdered.Submit(output.Samples);
            else ObserveFeelOrderedStop(feelOrdered.Reset());
            if(Interlocked.Exchange(ref feelDisplayQueued,1)!=0)return;
            UI(async()=> {
                Interlocked.Exchange(ref feelDisplayQueued,0);
                var latest=Volatile.Read(ref feelIngressOutput);
                feelLatestOutput=latest;feelPercent=latest.IsStop?0:latest.Percent;feelFrame++;
                RefreshFeelSignalDisplay();if(feelConnected)feelCount.Text=FeelSignalText();
                if(latest.IsStop)await StopFeelOwned();
            });
        }
        void PublishFeelRoutes()
        {
            if(feelOrdered==null)return;
            var targets=new List<FeelOrderedTarget>();
            foreach(var route in feelRoutes.Values)
            {
                var previous=Volatile.Read(ref route.OutputSettings);
                var settings=new OrderedRouteSettings(route.Device.HasChannel(route.Channel)&&route.Device.Player!=null&&route.Enabled.IsChecked==true,route.Gain.Value);
                Volatile.Write(ref route.OutputSettings,settings);
                if(previous.Enabled&&!settings.Enabled&&route.Device.Player!=null)ObserveFeelOrderedStop(feelOrdered.Suppress(route.Device.Player,route.Channel));
                if(route.Device.HasChannel(route.Channel)&&route.Device.Player!=null)
                    targets.Add(new(route.Device.Player,new OrderedMotorRoute(route.Channel,()=>Volatile.Read(ref route.OutputSettings))));
            }
            feelOrdered.Publish(targets);
        }
        Border FeelSurface(UIElement content)
        {
            var surface=new Border { CornerRadius=new CornerRadius(24),Padding=new Thickness(20),Margin=new Thickness(0,0,0,18),BorderBrush=Color("#30E4D3FF"),BorderThickness=new Thickness(0,1,0,0),Child=content };
            surface.SetResourceReference(Border.BackgroundProperty,"Glass");return surface;
        }
        void RefreshFeelPairingActions()
        {
            if(feelConnectButton!=null)feelConnectButton.IsEnabled=!closing&&!feelModeBusy&&feelPairCancel==null&&!String.IsNullOrWhiteSpace(feelPairing?.Password);
            if(feelDisconnectButton!=null)feelDisconnectButton.IsEnabled=!closing&&(feelConnected||feelPairCancel!=null);
        }
        bool FeelHasSignal()
        {
            if(!feelConnected||feelLatestOutput==null)return false;
            string kind=feelLatestOutput.Kind??"";
            return !feelLatestOutput.IsStop||kind.StartsWith("Video intensity",StringComparison.Ordinal)||kind=="Video cues v3"||
                kind.StartsWith("Website intensity",StringComparison.Ordinal)||kind=="Video stopped"||kind=="Timed tip"||kind=="Tip stopped";
        }
        void RefreshFeelSignalDisplay()
        {
            bool known=FeelHasSignal();
            if(feelValue!=null)feelValue.Text=known?Math.Round(feelPercent)+"%":"—";
            if(feelMeter!=null)feelMeter.Value=known?feelPercent:0;
            if(feelOutputState!=null)feelOutputState.Text=!feelArmed?(feelConnected&&feelOutputPaused?"Output paused":"Output off"):known?"Output enabled":"Output enabled · waiting for cues";
        }
        string FeelSignalText()
        {
            if(!feelClient.LastFrameAt.HasValue)return "No video cues received";
            if(feelLatestOutput==null)return "Control received · no vibration cue";
            if(feelLatestOutput.Kind=="Cue timeout")return "Video updates timed out";
            if(feelLatestOutput.Kind=="Publisher disconnected")return "Video sender disconnected";
            if(!FeelHasSignal())return "Waiting for video cues";
            return !feelLatestOutput.IsStop&&feelLatestOutput.Percent>0?"Receiving vibration cues":"Incoming level is zero";
        }
        async Task RefreshFeelPresence()
        {
            if(!feelConnected||closing||feelPresenceBusy||DateTimeOffset.UtcNow<feelPresenceRetryAt)return;
            feelPresenceBusy=true;
            try {
                await feelClient.UpdateDevicesAsync(devices.Values.Where(d=>d.Connected).Select(d=>d.Name).Distinct());
                if(hint.Text=="Video device status update failed; retrying…")hint.Text="";
            }
            catch(OperationCanceledException) { }
            catch { feelPresenceRetryAt=DateTimeOffset.UtcNow.AddSeconds(10);if(feelConnected&&!closing)hint.Text="Video device status update failed; retrying…"; }
            finally { feelPresenceBusy=false; }
        }
        async Task CheckFeelWebsite()
        {
            if(!feelConnected) { feelPresenceResult.Text="Pair a video session first.";return; }
            int revision=feelPresenceRevision;feelPresenceReport=null;feelPresenceResult.Text="Checking legacy connection…";
            try {
                var report=await feelClient.CheckPresenceAsync();
                if(closing||!feelConnected||revision!=feelPresenceRevision)return;
                feelPresenceReport=report;
                feelPresenceResult.Text=(report.Complete?"Last legacy connection check":"Partial legacy connection check")+
                    "\nWebsite channel: receiver "+report.Website.SelfPresentChannels+"/"+report.Website.Channels+
                    " · browser watchers "+report.Website.WatchPeers+" · players "+report.Website.PlayerPeers+
                    "\nVideo channels: receiver "+report.Video.SelfPresentChannels+"/"+report.Video.Channels+
                    " · browser watchers "+report.Video.WatchPeers+" · players "+report.Video.PlayerPeers;
            }
            catch { if(!closing&&revision==feelPresenceRevision)feelPresenceResult.Text="Website presence check unavailable. Try again."; }
        }
        async Task CheckFeelReferenceRoute()
        {
            if(!feelConnected) { feelPresenceResult.Text="Pair a video session first.";return; }
            int revision=feelPresenceRevision;feelReferenceReport=null;feelPresenceResult.Text="Checking old SDK route…";
            try {
                var report=await feelClient.CheckReferenceRouteAsync();
                if(closing||!feelConnected||revision!=feelPresenceRevision)return;
                feelReferenceReport=report;
                feelPresenceResult.Text=(report.Complete?"Last old SDK route check":"Partial old SDK route check")+
                    "\nWebsite: "+report.WebsiteOccupancy+" connections across "+report.WebsiteChannels+" channel(s)"+
                    "\nVideo: "+report.VideoOccupancy+" connections across "+report.VideoChannels+" channel(s)";
            }
            catch { if(!closing&&revision==feelPresenceRevision)feelPresenceResult.Text="Old SDK route check unavailable."; }
        }
        void RefreshFeelDetails()
        {
            RefreshFeelSignalDisplay();
            var status=feelClient.Diagnostics;
            var transport=status.Transport;
            var v2=status.V2;
            var writes=feelRoutes.Values.Select(r=>r.Device.Player).Where(p=>p!=null).Distinct().Select(p=>p.OrderedStatistics).ToArray();
            feelDetails.Text="Playback: "+(status.PlaybackMode=="live"?"Live levels (legacy)":"Timed cues")+
                "\nVideo channels: "+status.VideoRooms+"\nControl messages: "+status.CandidateCues+" · Accepted: "+status.AcceptedCues+
                "\nService messages: "+status.FecMessages+" live · "+status.LegacyMessages+" legacy"+
                "\nLegacy received: "+transport.LegacyRawFrames+
                "\nLegacy filtered: format "+transport.LegacyDroppedShape+" · channel "+transport.LegacyDroppedScope+" · type "+transport.LegacyDroppedType+
                " · time "+transport.LegacyDroppedTime+" · stale "+transport.LegacyDroppedStale+" · replay "+transport.LegacyDroppedReplay+
                "\nOther live events: "+transport.FecOtherEvents+" · control "+transport.FecControlEvents+" · session "+transport.FecSessionEvents+" · unknown "+transport.FecUnknownEvents+
                "\nOther messages: status "+transport.SessionMetadataMessages+" · object "+transport.UnknownObjectDataMessages+
                " · value "+transport.UnknownNonObjectDataMessages+" · cue format "+transport.UnknownCueShapeMessages+
                "\nLive batches: "+v2.AcceptedBatches+" · samples "+v2.Samples+" · positive "+v2.PositiveSamples+" · mixed "+v2.MixedBatches+
                "\nLast live batch: "+v2.LastBatchSamples+" samples · peak "+v2.LastBatchMaximum.ToString("0.##")+"% · final "+v2.LastBatchFinal.ToString("0.##")+"%"+
                "\nOrdered output: "+writes.Sum(w=>w.Submitted)+" writes · "+writes.Sum(w=>w.Pending)+" pending · "+writes.Sum(w=>w.Expired)+" expired · "+writes.Sum(w=>w.Overloaded)+" replaced";
        }
        void PasteFeelPairingSafely()
        {
            try { PasteFeelPairing(); }
            catch(Exception ex) { if(feelPairHint!=null)feelPairHint.Text=ex.Message;else feelStatus.Text=ex.Message; }
        }
        void PasteFeelPairing()
        {
            feelPairing.Password=QrInput.ReadClipboardPairing();if(feelPairHint!=null)feelPairHint.Text="Code ready · choose Connect";hint.Text="";
        }
        void OpenFeelImage()
        {
            var dialog=new OpenFileDialog { Filter="QR images|*.png;*.jpg;*.jpeg;*.bmp",Title="Open FeelConnect QR" };
            if(dialog.ShowDialog(window)!=true)return;
            LoadFeelImage(dialog.FileName);
        }
        void LoadFeelImage(string filename)
        {
            if(String.IsNullOrWhiteSpace(filename)||!Path.IsPathFullyQualified(filename)||filename.StartsWith(@"\\",StringComparison.Ordinal))
                throw new ArgumentException("Choose a local QR image.");
            string path=Path.GetFullPath(filename),extension=Path.GetExtension(path).ToLowerInvariant();
            if(!new[]{".png",".jpg",".jpeg",".bmp"}.Contains(extension))throw new ArgumentException("Choose a PNG, JPEG or BMP QR image.");
            if(new DriveInfo(Path.GetPathRoot(path)).DriveType==DriveType.Network)throw new ArgumentException("Choose an image saved on this computer.");
            var info=new FileInfo(path);
            if(!info.Exists)throw new ArgumentException("The QR image could not be found.");
            if(info.Length>QrInput.MaxImageBytes)throw new ArgumentException("Choose an image smaller than 32 MB.");
            if((info.Attributes&FileAttributes.ReparsePoint)!=0)throw new ArgumentException("Choose the QR image file directly.");
            string pairing;
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var bytes=new MemoryStream())
            {
                var buffer=new byte[81920];int read;
                while((read=file.Read(buffer,0,buffer.Length))>0)
                {
                    if(bytes.Length+read>QrInput.MaxImageBytes)throw new ArgumentException("Choose an image smaller than 32 MB.");
                    bytes.Write(buffer,0,read);
                }
                bytes.Position=0;
                if(extension==".png")
                {
                    var data=new DataObject();data.SetData("PNG",bytes.ToArray(),false);pairing=QrInput.FromDataObject(data);
                }
                else
                {
                    var decoder=BitmapDecoder.Create(bytes,BitmapCreateOptions.DelayCreation,BitmapCacheOption.OnDemand);
                    if(decoder.Frames.Count==0)throw new ArgumentException("No image found in this file.");
                    pairing=DecodeFeelQr(decoder.Frames[0]);
                }
            }
            feelPairing.Password=pairing;if(feelPairHint!=null)feelPairHint.Text="QR ready · choose Connect";hint.Text="";
        }
        internal static string DecodeFeelQr(BitmapSource bitmap) => QrInput.DecodeBitmap(bitmap);
        async Task PairFeelConnect()
        {
            string pairing=feelPairing.Password;FeelConnectPairing.Parse(pairing);feelPairing.Clear();
            if(feelPairHint!=null)feelPairHint.Text="Pairing…";
            await DisconnectFeelConnect();
            var cancel=new CancellationTokenSource();feelPairCancel=cancel;
            RefreshFeelPairingActions();
            try { await feelClient.PairAsync(pairing,devices.Values.Where(d=>d.Connected).Select(d=>d.Name).Distinct(),cancel.Token,SelectedFeelPlaybackMode());hint.Text="";if(feelPairHint!=null)feelPairHint.Text="Ctrl+V · or drop a new QR image here"; }
            catch(OperationCanceledException) { if(feelPairHint!=null)feelPairHint.Text="Pairing canceled"; }
            catch { if(!cancel.IsCancellationRequested) { feelStatus.Text="Pairing failed. Generate a fresh QR code and try again.";if(feelPairHint!=null)feelPairHint.Text="Paste a fresh QR code to try again."; } }
            finally { if(ReferenceEquals(feelPairCancel,cancel))feelPairCancel=null;cancel.Dispose();RefreshFeelPairingActions(); }
        }
        FeelConnectPlaybackMode SelectedFeelPlaybackMode() => feelPlaybackMode?.SelectedIndex==1?FeelConnectPlaybackMode.Live:FeelConnectPlaybackMode.Timed;
        async Task ApplyFeelPlaybackMode()
        {
            if(feelModeBusy||closing)return;
            var mode=SelectedFeelPlaybackMode();
            if(!feelConnected) { hint.Text="Playback mode selected for the next connection";return; }
            if(feelClient.Diagnostics.PlaybackMode==(mode==FeelConnectPlaybackMode.Live?"live":"timed")) { hint.Text="This playback mode is already active";return; }
            feelModeBusy=true;feelPlaybackMode.IsEnabled=false;
            CancellationTokenSource cancel=null;int operation=0;
            try {
                operation=++operatorGeneration;
                await DisarmFeelConnect();
                if(closing||operation!=operatorGeneration)return;
                feelConnected=false;feelPercent=0;feelFrame++;feelLatestOutput=null;RefreshFeelSignalDisplay();
                feelOutputButton.IsEnabled=false;feelStatus.Text="Changing playback mode…";
                feelPairCancel?.Cancel();cancel=new CancellationTokenSource();feelPairCancel=cancel;
                await feelClient.ReconnectAsync(mode,cancel.Token);
                if(!closing&&operation==operatorGeneration&&!cancel.IsCancellationRequested)hint.Text="Playback mode applied · enable output to resume";
            }
            catch(OperationCanceledException) { }
            catch { if(!closing&&operation==operatorGeneration&&(cancel==null||!cancel.IsCancellationRequested))feelStatus.Text="Reconnect failed. Paste a fresh QR code and connect again."; }
            finally {
                if(cancel!=null) { if(ReferenceEquals(feelPairCancel,cancel))feelPairCancel=null;cancel.Dispose(); }
                feelModeBusy=false;if(!closing)feelPlaybackMode.IsEnabled=true;
            }
        }
        async Task DisconnectFeelConnect()
        {
            operatorGeneration++;feelConnected=false;feelPercent=0;feelFrame++;feelPairCancel?.Cancel();
            feelLatestOutput=null;RefreshFeelSignalDisplay();feelStatus.Text="Not paired";feelOutputButton.IsEnabled=false;
            await DisarmFeelConnect();await feelClient.DisconnectAsync();RefreshFeelPairingActions();
        }
        async Task ToggleFeelOutput()
        {
            if(feelArmed) { operatorGeneration++;await DisarmFeelConnect();return; }
            if(!feelConnected)throw new InvalidOperationException("Connect a video session first.");
            if(!feelRoutes.Values.Any(r=>r.Enabled.IsChecked==true&&r.Device.HasChannel(r.Channel)))throw new InvalidOperationException("Connect and select at least one motor.");
            if(sweep!=null)throw new InvalidOperationException("Stop the speed test first.");
            int smoothing=Number(feelSmoothing);if(smoothing<0||smoothing>2000)throw new ArgumentException("Smoothing must be 0–2000 ms.");
            int operation=++operatorGeneration;
            var sources=PauseExternalSources();var stops=StopPlayers();await Task.WhenAll(sources,stops);
            if(closing||operation!=operatorGeneration||!feelConnected)return;
            if(!await stops)throw new InvalidOperationException("Output could not be stopped before switching sources.");
            // Untimed batches received while disarmed are never replayed on Enable.
            feelPercent=Volatile.Read(ref feelOrderedInput)==1?0:feelClient.CurrentPercent;RefreshFeelSignalDisplay();
            feelFrame++;feelArmed=true;feelOutputPaused=false;feelGeneration++;feelOutputButton.Content="Pause output";hint.Text="Video output enabled";
            PublishFeelRoutes();feelOrdered.Enable();
            RefreshFeelSignalDisplay();
        }
        async Task DisarmFeelConnect()
        {
            var orderedStop=feelOrdered?.Reset(true)??Task.CompletedTask;
            feelGeneration++;if(feelArmed)feelOutputPaused=true;feelArmed=false;if(!feelConnected)feelOutputPaused=false;
            RefreshFeelSignalDisplay();
            if(feelOutputButton!=null)feelOutputButton.Content="Enable output";
            if(hint.Text=="Video output enabled")hint.Text="Video output paused";
            await Task.WhenAll(orderedStop,StopFeelOwned());
        }
        async Task StopFeelOwned()
        {
            await feelWriteGate.WaitAsync();
            try {
                var owned=feelOwned.Values.ToArray();feelOwned.Clear();
                await Task.WhenAll(owned.Select(async item=> { try { await item.Item1.CancelSampled(item.Item2); }catch { hint.Text="Video output stopped; a disconnected motor could not confirm Stop."; } }));
            }finally { feelWriteGate.Release(); }
        }
        void RefreshFeelRoutes()
        {
            if(feelRoutePanel==null)return;
            bool added=false;
            foreach(var d in devices.Values)foreach(var channel in d.Motors.Keys) {
                string key=MixerKey(d,channel);if(feelRoutes.ContainsKey(key))continue;
                var route=new FeelRoute { Device=d,Channel=channel };var panel=new StackPanel();
                route.Card=new Border { Width=214,Padding=new Thickness(16),CornerRadius=new CornerRadius(20),Background=Color("#A0241935"),Margin=new Thickness(0,8,12,8),Child=panel };
                var heading=new StackPanel { MaxWidth=152 };
                heading.Children.Add(new TextBlock { Text=d.Name,TextWrapping=TextWrapping.Wrap,FontSize=16 });
                heading.Children.Add(new TextBlock { Text=d.ChannelLabel(channel),FontSize=12,Margin=new Thickness(0,4,0,0),Foreground=Color("#C4BAD7") });
                route.Enabled=new CheckBox { Content=heading,IsChecked=true };panel.Children.Add(route.Enabled);
                route.Enabled.Checked+=(s,e)=> { RefreshFeelRouteSummary();PublishFeelRoutes(); };route.Enabled.Unchecked+=(s,e)=> { RefreshFeelRouteSummary();PublishFeelRoutes(); };
                System.Windows.Automation.AutomationProperties.SetName(route.Enabled,d.Name+" motor "+channel+" video routing");
                var value=Text("Strength 100%",13);panel.Children.Add(value);
                route.Gain=new Slider { Minimum=0,Maximum=200,Value=100,TickFrequency=5,SmallChange=5,IsSnapToTickEnabled=true };
                System.Windows.Automation.AutomationProperties.SetName(route.Gain,d.Name+" motor "+channel+" video strength");
                route.Gain.ValueChanged+=(s,e)=> { value.Text="Strength "+(int)route.Gain.Value+"%";PublishFeelRoutes(); };panel.Children.Add(route.Gain);
                feelRoutes.Add(key,route);feelRoutePanel.Children.Add(route.Card);added=true;
            }
            foreach(var route in feelRoutes.Values)route.Card.IsEnabled=route.Device.HasChannel(route.Channel)&&!closing;
            RefreshFeelRouteSummary();
            PublishFeelRoutes();
            RefreshFeelPairingActions();
            if(added)QueueStudioAppearance();
        }
        void RefreshFeelRouteSummary()
        {
            if(feelRouteSummary==null)return;
            int available=feelRoutes.Values.Count(r=>r.Device.HasChannel(r.Channel)),selected=feelRoutes.Values.Count(r=>r.Device.HasChannel(r.Channel)&&r.Enabled.IsChecked==true);
            feelRouteSummary.Text=available==0?"Play targets · no motors connected":"Play targets · "+selected+" of "+available+" motors";
            if(feelRouteEmpty!=null)feelRouteEmpty.Visibility=available==0?Visibility.Visible:Visibility.Collapsed;
        }
        async Task FeelTick()
        {
            var input=feelLatestOutput;
            if(!feelArmed||!feelConnected||feelTickBusy||Volatile.Read(ref feelOrderedInput)==1||Volatile.Read(ref feelInputAvailable)==0||!ReferenceEquals(input,Volatile.Read(ref feelIngressOutput)))return;feelTickBusy=true;int generation=feelGeneration,frame=feelFrame;
            try {
                int smoothing=Number(feelSmoothing);if(smoothing<0||smoothing>2000)throw new ArgumentException("Smoothing must be 0–2000 ms.");
                Func<bool> isCurrent=()=>Volatile.Read(ref feelOrderedInput)==0&&Volatile.Read(ref feelInputAvailable)==1&&Volatile.Read(ref feelGeneration)==generation&&Volatile.Read(ref feelFrame)==frame&&ReferenceEquals(input,Volatile.Read(ref feelIngressOutput));
                await feelWriteGate.WaitAsync();
                try {
                    foreach(var pair in feelRoutes.ToArray()) {
                        if(!feelArmed||!feelConnected||generation!=feelGeneration||frame!=feelFrame||Volatile.Read(ref feelOrderedInput)==1)return;
                        var route=pair.Value;var player=route.Device.Player;
                        if(!route.Device.HasChannel(route.Channel)||player==null)continue;
                        int level=route.Enabled.IsChecked==true?Math.Clamp((int)Math.Round(feelPercent*route.Gain.Value/500.0,MidpointRounding.AwayFromZero),0,20):0;
                        if(level==0) { if(feelOwned.Remove(pair.Key))await player.CancelSampled(route.Channel); }
                        else {
                            feelOwned[pair.Key]=Tuple.Create(player,route.Channel);
                            if(!player.UpdateSteady(route.Channel,level,600,smoothing,isCurrent))await player.Start(route.Channel,new PatternSpec { mode="Steady",high=level,durationMs=600,transitionMs=smoothing },20,isCurrent:isCurrent);
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
