using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class StudioIntegrationTests
{
    static IEnumerable<System.Windows.DependencyObject> MotionDescendants(System.Windows.DependencyObject root)
    {
        yield return root;
        foreach(var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            foreach(var item in MotionDescendants(child))yield return item;
    }
    sealed class FunctionLink : ICommandLink,ILatestCommandLink
    {
        public readonly ConcurrentQueue<string> Sent=new();
        public bool PauseNextCommand;
        public readonly TaskCompletionSource<bool> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> Command(string command)=>Command(command,CancellationToken.None);
        public async Task<string> Command(string command,CancellationToken cancel) {
            cancel.ThrowIfCancellationRequested();
            if(PauseNextCommand) { PauseNextCommand=false;Entered.TrySetResult(true);await Resume.Task; }
            Sent.Enqueue(command);return "OK";
        }
        public Task<string> CommandLatest(Func<string> sample,CancellationToken cancel) { cancel.ThrowIfCancellationRequested();var command=sample();return command==null?Task.FromResult(""):Command(command,cancel); }
    }
    static async Task CancelledApiMotionDoesNotApplySettings()
    {
        foreach(string function in new[]{"stroke","position"}) {
            await using var f=new Fixture(false);var link=new FunctionLink { PauseNextCommand=true };
            var profile=ActuatorProfile.Create(new ModelMatch { Features=new[]{"Thrusting","Position"} },"BA","1");
            var d=new Device { Address=3,Name="Synthetic machine",Connected=true,Supported=true,Functions=profile,Channels=new[]{7},Player=new Playback(link,profile),Controls=new StackPanel(),State=new TextBlock(),Details=new TextBlock(),ConnectButton=new Button(),Tab=new TabItem() };
            Get<Dictionary<ulong,Device>>(f.App,"devices").Add(d.Address,d);Call(f.App,"BuildControls",d);
            var session=SyntheticApiSession(f);using var cancellation=new CancellationTokenSource();
            var request=NativeApi(f,"function",new() { ["sessionId"]=session.Id,["deviceId"]="000000000003",["function"]=function,["value"]=20,["high"]=75 },cancellation.Token);
            try { await Until(()=>link.Entered.Task.IsCompleted,"motion request reaches its initial stop"); }
            finally { cancellation.Cancel();link.Resume.TrySetResult(true); }
            bool rejected=false;try { await request; }catch(OperationCanceledException) { rejected=true; }
            Check(rejected,"cancelled "+function+" request is not acknowledged as accepted");
            Check(profile.StrokeLow==0&&profile.StrokeHigh==100&&!link.Sent.Any(c=>c.StartsWith("FSetSite:")),"cancelled "+function+" request leaves range unchanged and sends no point move");
        }
    }
    static async Task FunctionChannelsThroughNativeApp()
    {
        await using var f=new Fixture(false);var link=new FunctionLink();
        var profile=ActuatorProfile.Create(new ModelMatch { Features=new[]{"Thrusting","Position"} },"BA","1");
        var d=new Device { Address=3,Name="Synthetic machine",Connected=true,Supported=true,Functions=profile,Channels=new[]{7},Player=new Playback(link,profile),Controls=new StackPanel(),State=new TextBlock(),Details=new TextBlock(),ConnectButton=new Button(),Tab=new TabItem() };
        Get<Dictionary<ulong,Device>>(f.App,"devices").Add(d.Address,d);
        Call(f.App,"BuildControls",d);Call(f.App,"RefreshStudioMotors");
        Check(d.Motors.ContainsKey(7)&&d.ChannelLabel(7)=="Thrusting","device UI builds a named thrusting control");
        var rows=Get<Dictionary<string,MixerMotor>>(f.App,"mixerRows");
        Check(rows.Count==1&&rows.Values.Single().Channel==7,"Studio creates a curve lane for the motion actuator");
        JsonElement DeviceState() { using var json=JsonDocument.Parse(JsonSerializer.Serialize(Call(f.App,"ApiDevices")));return json.RootElement[0].Clone(); }
        Check(DeviceState().GetProperty("configuredStroke").GetProperty("start").GetInt32()==0&&DeviceState().GetProperty("configuredStroke").GetProperty("end").GetInt32()==100,"API exposes default configured stroke for the current connection");
        var session=SyntheticApiSession(f);
        await NativeApi(f,"motor",ApiMotor(session,"000000000003",7,14));
        await Until(()=>link.Sent.Any(c=>c.StartsWith("FLVS:")&&c[5]==14),"local motor API accepts and drives non-vibration channel 7");
        var setting=new Dictionary<string,object> { ["sessionId"]=session.Id,["deviceId"]="000000000003",["function"]="stroke",["value"]=20,["high"]=75 };
        await NativeApi(f,"function",setting);
        Check(!d.Player.Running&&profile.StrokeLow==20&&profile.StrokeHigh==75,"motion settings stop prior playback and update range");
        var configured=DeviceState().GetProperty("configuredStroke");
        Check(configured.GetProperty("start").GetInt32()==20&&configured.GetProperty("end").GetInt32()==75,"API readback reflects the accepted stroke range");
        var motion=MotionDescendants(d.Controls).OfType<Expander>().Single(e=>(string)e.Header=="Motion settings");
        var strokeFields=MotionDescendants(motion).OfType<TextBox>().Take(2).ToArray();
        await Until(()=>strokeFields[0].Text=="20"&&strokeFields[1].Text=="75","motion fields reflect stroke settings accepted through the API");
        await NativeApi(f,"motor",ApiMotor(session,"000000000003",7,10));
        await Until(()=>link.Sent.Any(c=>c.StartsWith("FLVS:")&&c[5]==10&&c[6]==20&&c[7]==75),"next playback uses the configured stroke endpoints");
        setting["function"]="position";setting["value"]=0;
        await NativeApi(f,"function",setting);
        Check(link.Sent.Contains("FSetSite:0;")&&link.Sent.Last()=="Mply:0:20;","API position endpoint zero moves then stops without leaving a pattern active");
        Call(f.App,"Release",d,"Synthetic disconnect");Call(f.App,"RefreshStudioMotors");
        Check(DeviceState().GetProperty("configuredStroke").ValueKind==JsonValueKind.Null,"disconnected device does not advertise an applicable stroke range");
        Check(!rows.Values.Single().Card.IsEnabled,"disconnected function lanes retain labels and disable without dereferencing a missing player");
        d.Functions=ActuatorProfile.Create(new ModelMatch { Features=new[]{"Oscillation"} },"EZ","1");d.Channels=new[]{9};d.Player=new Playback(link,d.Functions);d.Connected=true;
        Call(f.App,"BuildControls",d);Call(f.App,"RefreshStudioMotors");
        Check(rows.Values.Single(m=>m.Channel==7).Card.IsEnabled==false&&rows.Values.Single(m=>m.Channel==9).Card.IsEnabled,"reidentified device disables obsolete lanes and enables only current capabilities");
        Check(DeviceState().GetProperty("configuredStroke").ValueKind==JsonValueKind.Null,"non-stroking replacement does not inherit old range metadata");
        Check(((MixerMotor[])Call(f.App,"SelectedMixer")).All(m=>m.Channel==9),"stale motion channel cannot enter new Studio or audio playback");
    }
}
