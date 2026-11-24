using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class StudioIntegrationTests
{
    // Exercises settings and power-off acknowledgements without creating a Bluetooth device.
    sealed class SettingsLink : IDeviceLink
    {
        public bool Connected { get; private set; }=true;
        public string HoldCommand;
        public readonly List<string> Commands=new();
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Command(string command)=>Command(command,CancellationToken.None);
        public async Task<string> Command(string command,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();Commands.Add(command);
            if(command==HoldCommand){Entered.TrySetResult();await Continue.Task.WaitAsync(token);}
            return "OK";
        }
        public Task<string> Query(string command,Func<string,bool> match)
        {
            Commands.Add(command);
            string reply=command=="GetLight;"?"Light:1":command=="GetAS;"?"AutoSwith:1:0":"50";
            if(!match(reply))throw new InvalidOperationException("Synthetic setting reply rejected");
            return Task.FromResult(reply);
        }
        public void Dispose(){Connected=false;Continue.TrySetResult();}
    }

    static async Task DeviceSettingsCannotReachReplacement()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;
        var original=new SettingsLink { HoldCommand="Light:on;" };device.Link=original;
        Call(fixture.App,"BuildControls",device);device.Light.IsChecked=true;device.AutoStop.IsChecked=true;
        var apply=DeviceControlElements(device.Controls).OfType<Button>().Single(b=>(string)b.Content=="Apply settings");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await original.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // The acknowledgement completes; replace the connection before its UI continuation runs.
        original.Continue.TrySetResult();device.Player.CancelAll();device.Generation+=2;
        original.Dispose();var replacement=new SettingsLink();device.Link=replacement;
        device.Player=new Playback(new FakeLink("dual"),new[]{1,2});
        await Until(()=>apply.IsEnabled,"settings action returns after its connection is replaced");
        Check(replacement.Commands.Count==0,"old Apply settings must not send switches or readbacks to a replacement connection");
        Check(original.Commands.SequenceEqual(new[]{"Light:on;"}),"an obsolete settings request stops after the completed original command");
        apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>apply.IsEnabled,"a new settings action finishes");
        Check(replacement.Commands.SequenceEqual(new[]{"Light:on;","AutoSwith:On:Off;","GetLight;","GetAS;"}),
            "fresh Apply settings sends both options and reads back the same connection");
    }

    static async Task PowerOffAcknowledgementCannotReleaseReplacement()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;
        var original=new SettingsLink { HoldCommand="PowerOff;" };device.Link=original;
        var power=AdvancedPowerOffButton(fixture,device);power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await original.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        original.Continue.TrySetResult();device.Player.CancelAll();int generation=device.Generation+=2;original.Dispose();
        var replacement=new SettingsLink();device.Link=replacement;
        var player=new Playback(new FakeLink("dual"),new[]{1,2});device.Player=player;
        await Until(()=>power.IsEnabled,"old power-off acknowledgement returns after a replacement connection");
        Check(device.Connected&&device.Generation==generation&&ReferenceEquals(device.Link,replacement)&&ReferenceEquals(device.Player,player),
            "a late power-off acknowledgement cannot release a replacement connection");
        Check(replacement.Commands.Count==0&&replacement.Connected,"replacement receives no power-off command and is not disposed");
        power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Until(()=>power.IsEnabled,"a fresh power-off request completes");
        Check(replacement.Commands.SequenceEqual(new[]{"PowerOff;"})&&!replacement.Connected&&!device.Connected,
            "fresh power-off reaches and releases its current connection after acknowledging the motor stop");
    }
}
