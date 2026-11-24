using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        TabItem studioDevicesTab;
        StackPanel studioDeviceCards;
        Border studioDeviceEmpty;
        TextBlock studioDeviceEmptyTitle,studioDeviceEmptyMessage;
        Button studioFindDevices;
        readonly Dictionary<Device,Expander> studioDeviceExpanders=new Dictionary<Device,Expander>();
        readonly Dictionary<Device,TextBlock> studioDeviceCapabilities=new Dictionary<Device,TextBlock>();
        readonly Dictionary<Device,bool> studioDeviceConnected=new Dictionary<Device,bool>();

        void InitializeStudioDevices()
        {
            if(studioDevicesTab!=null)return;
            var body=new StackPanel { Margin=new Thickness(24) };
            var header=new DockPanel { Margin=new Thickness(0,0,0,18) };
            studioFindDevices=Action("Find devices",async()=> { if(scanning)await EndScan(false);else StartScan();RefreshStudioDevices(); });
            studioFindDevices.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(studioFindDevices,Dock.Right);header.Children.Add(studioFindDevices);
            header.Children.Add(new TextBlock { Text="Devices",FontSize=32,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center });body.Children.Add(header);

            var empty=new StackPanel { HorizontalAlignment=HorizontalAlignment.Center };
            var icon=new Border { Width=106,Height=49,CornerRadius=new CornerRadius(20),Background=Color("#50473768"),Margin=new Thickness(0,6,0,16),HorizontalAlignment=HorizontalAlignment.Center };
            var dots=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center };
            for(int i=0;i<3;i++)dots.Children.Add(new Ellipse { Width=9,Height=9,Fill=Color("#CCB8FF"),Margin=new Thickness(5) });icon.Child=dots;empty.Children.Add(icon);
            studioDeviceEmptyTitle=Text("No connected devices",23);studioDeviceEmptyTitle.TextAlignment=TextAlignment.Center;empty.Children.Add(studioDeviceEmptyTitle);
            studioDeviceEmptyMessage=Text("Find a nearby device to get started.");studioDeviceEmptyMessage.Foreground=Color("#C4BAD7");studioDeviceEmptyMessage.TextAlignment=TextAlignment.Center;empty.Children.Add(studioDeviceEmptyMessage);
            studioDeviceEmpty=new Border { Padding=new Thickness(18),Margin=new Thickness(0,0,0,16),Child=empty };body.Children.Add(studioDeviceEmpty);
            studioDeviceCards=new StackPanel();body.Children.Add(studioDeviceCards);

            var tools=new WrapPanel { Margin=new Thickness(0,12,0,0) };
            MoveStudioElement(sweepButton,tools);
            MoveStudioElement(Find<Button>("DisconnectAllButton"),tools);
            body.Children.Add(new Expander { Header="Device tools",Content=tools,Margin=new Thickness(0,18,0,0) });
            studioDevicesTab=new TabItem { Header="Devices",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled } };
            studioDevicesTab.Loaded+=(s,e)=>RefreshStudioDevices();tabs.Items.Add(studioDevicesTab);
            RefreshStudioDevices();
        }

        static void MoveStudioElement(FrameworkElement element,Panel destination)
        {
            if(element==null)return;
            var parent=element.Parent;
            if(parent is Panel panel)
            {
                panel.Children.Remove(element);
                if(panel.Children.Count==0&&panel.Parent is Expander emptyTools)emptyTools.Visibility=Visibility.Collapsed;
            }
            else if(parent is ContentControl content)content.Content=null;
            else if(parent is Decorator decorator)decorator.Child=null;
            destination.Children.Add(element);
        }

        void BuildStudioDeviceCard(Device d)
        {
            if(studioDevicesTab==null)InitializeStudioDevices();
            // Preserve the existing lifecycle fields and all capability controls.
            // This compatibility TabItem is intentionally absent from navigation.
            d.Tab=new TabItem { Header=d.Name };
            d.Content=new StackPanel { Margin=new Thickness(0,12,0,0) };
            d.Details=Text(d.AdvertisedName,13);d.Details.Foreground=Color("#C4BAD7");d.Content.Children.Add(d.Details);
            d.Controls=new StackPanel();d.Content.Children.Add(d.Controls);
            var cardBody=new StackPanel();
            var header=new DockPanel();
            d.ConnectButton=Action("Connect",async()=> { if(d.Connected)await Disconnect(d);else await Connect(d); });
            d.ConnectButton.Margin=new Thickness(12,0,0,0);DockPanel.SetDock(d.ConnectButton,Dock.Right);header.Children.Add(d.ConnectButton);
            d.State=Text(d.Name+" · Available",20);d.State.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(d.State);cardBody.Children.Add(header);
            var capability=Text("Connect to read capabilities",13);capability.Foreground=Color("#C4BAD7");cardBody.Children.Add(capability);studioDeviceCapabilities[d]=capability;
            var expander=new Expander { Header="Controls and device options",Content=d.Content,Margin=new Thickness(0,10,0,0) };cardBody.Children.Add(expander);studioDeviceExpanders[d]=expander;studioDeviceConnected[d]=false;
            var card=new Border { CornerRadius=new CornerRadius(24),Padding=new Thickness(20),Margin=new Thickness(0,0,0,14),BorderBrush=Color("#30E4D3FF"),BorderThickness=new Thickness(0,1,0,0),Child=cardBody };card.SetResourceReference(Border.BackgroundProperty,"Glass");studioDeviceCards.Children.Add(card);
            RefreshStudioDevices();QueueStudioAppearance();
        }

        void RefreshStudioDevices()
        {
            if(studioDevicesTab==null)return;
            studioFindDevices.Content=scanning?"Stop scanning":"Find devices";
            studioFindDevices.IsEnabled=ready&&!closing;
            int connected=devices.Values.Count(d=>d.Connected);
            studioDeviceEmpty.Visibility=connected==0?Visibility.Visible:Visibility.Collapsed;
            studioDeviceEmptyTitle.Text=scanning?"Searching for devices":"No connected devices";
            studioDeviceEmptyMessage.Text=scanning?"Nearby devices appear below.":!ready?(bluetooth?.Text??"Bluetooth unavailable"):
                devices.Count==0?"Find a nearby device to get started.":"Choose Connect to use a device.";
            foreach(var d in devices.Values)
            {
                if(d.State!=null)d.State.Text=d.Name;
                if(d.ConnectButton!=null)d.ConnectButton.Content=d.Connected?"Disconnect":d.Busy?"Connecting…":"Connect";
                if(studioDeviceCapabilities.TryGetValue(d,out var label))
                {
                    string capabilities=d.Channels.Length>0?string.Join(" · ",d.Channels.Select(d.ChannelLabel)):d.Connected?"No supported controls":"Capabilities available after connecting";
                    string status=d.Connected?"Connected":d.Busy?"Connecting…":d.Status;
                    label.Text=status+" · "+capabilities+(d.Battery.HasValue?" · Battery "+d.Battery.Value+"%":"");
                }
                if(studioDeviceConnected.TryGetValue(d,out bool wasConnected)&&d.Connected&&!wasConnected)QueueStudioAppearance();
                studioDeviceConnected[d]=d.Connected;
            }
        }

        void SelectStudioDevice(Device d)
        {
            if(studioDevicesTab==null)return;
            tabs.SelectedItem=studioDevicesTab;
            if(studioDeviceExpanders.TryGetValue(d,out var expander))
            {
                expander.IsExpanded=true;
                window.Dispatcher.BeginInvoke(new Action(()=>expander.BringIntoView()));
            }
        }
    }
}
