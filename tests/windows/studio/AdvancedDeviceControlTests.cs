using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LovenseConnector;

internal static partial class StudioIntegrationTests
{
    static IEnumerable<DependencyObject> DeviceControlElements(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var element in DeviceControlElements(child))yield return element;
    }

    static Button AdvancedPatternButton(Fixture fixture)
    {
        var device=fixture.Rows[0].Device;
        Set(fixture.App,"json",new System.Web.Script.Serialization.JavaScriptSerializer());
        Call(fixture.App,"BuildControls",device);
        var controls=DeviceControlElements(device.Controls).ToArray();
        controls.OfType<ComboBox>().Single(c=>c.Items.Contains("All motors")).SelectedItem="All motors";
        controls.OfType<ComboBox>().Single(c=>c.Items.Contains("Steady")).SelectedItem="Steady";
        return controls.OfType<Button>().Single(b=>(string)b.Content=="Play pattern");
    }

    static async Task DevicePatternCannotResumeAfterStop()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;var play=AdvancedPatternButton(fixture);
        var preset=device.Player.HardwarePreset(1,10000);
        await Until(()=>device.Player.Level(1)<0,"synthetic firmware preset is active before advanced pattern handoff");
        fixture.Dual.HoldNextPresetStop=true;
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop=fixture.Stop();fixture.Dual.ReleasePreset.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        await Until(()=>play.IsEnabled,"cancelled pattern request returns after the blocked preset stop");
        await preset.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(150);
        Check(!fixture.Commands().Any(c=>c.Nonzero)&&fixture.Levels().All(level=>level==0),
            "Stop all cancels every motor in an advanced pattern request, including motors not yet started");

        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>play.IsEnabled&&device.Player.Level(1)==3&&device.Player.Level(2)==3,
            "a new explicit advanced pattern request still starts both motors");
        device.Motors[1].Level.Value=19;
        await Until(()=>device.Player.Level(1)==19&&device.Player.Level(2)==3,
            "advanced manual sliders still independently edit their own active steady output");
    }

    static async Task DevicePatternCannotReachReconnectedPlayer()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;var play=AdvancedPatternButton(fixture);
        var previous=device.Player;var preset=previous.HardwarePreset(1,10000);
        await Until(()=>previous.Level(1)<0,"synthetic preset is active before reconnection race");
        fixture.Dual.HoldNextPresetStop=true;play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // Release cancels the previous player; a subsequent connection has a new generation and player.
        previous.CancelAll();device.Generation+=2;
        var replacement=new FakeLink("dual");device.Player=new Playback(replacement,new[]{1,2});
        fixture.Dual.ReleasePreset.TrySetResult();
        await Until(()=>play.IsEnabled,"old pattern handoff ends after a replacement connection");
        await preset.WaitAsync(TimeSpan.FromSeconds(3));await Task.Delay(150);
        Check(!fixture.Dual.Snapshot().Any(c=>c.Nonzero)&&!replacement.Snapshot().Any(c=>c.Nonzero)&&device.Player.Level(1)==0&&device.Player.Level(2)==0,
            "an old all-motors request cannot start either the released player or its replacement");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(()=>play.IsEnabled&&device.Player.Level(1)==3&&device.Player.Level(2)==3,
            "reconnected motors remain available to a new explicit pattern request");
    }

    static Button AdvancedPowerOffButton(Fixture fixture,Device device)
    {
        Call(fixture.App,"BuildControls",device);
        return DeviceControlElements(device.Controls).OfType<Button>().Single(b=>(string)b.Content=="Power off toy");
    }

    static async Task PowerOffCannotReleaseReplacement()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;
        var power=AdvancedPowerOffButton(fixture,device);var previous=device.Player;
        fixture.Dual.HoldNextPresetStop=true;power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        previous.CancelAll();int generation=device.Generation+=2;
        var replacement=new Playback(new FakeLink("dual"),new[]{1,2});device.Player=replacement;
        fixture.Dual.ReleasePreset.TrySetResult();
        await Until(()=>power.IsEnabled,"obsolete power-off request finishes after its initial stop");
        // No BLE link is created. This exercises stale failed-command cleanup, not physical power-off.
        Check(device.Connected&&ReferenceEquals(device.Player,replacement)&&device.Generation==generation,
            "an old power-off request cannot release a replacement connection after stopping its original player");
    }

    static async Task PowerOffCannotRetargetDuringHandoff()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[2].Device;
        var power=AdvancedPowerOffButton(fixture,device);
        await fixture.Start();await Until(()=>fixture.Levels().All(level=>level>0),"Studio runs before pending power-off handoff");
        fixture.Dual.HoldNextLaneStop=true;power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        device.Player.CancelAll();int generation=device.Generation+=2;
        var link=new FakeLink("replacement");var replacement=new Playback(link,new[]{0});device.Player=replacement;
        fixture.Dual.ReleaseLaneStop.TrySetResult();
        await Until(()=>power.IsEnabled,"obsolete power-off handoff finishes after other sources stop");
        Check(device.Connected&&ReferenceEquals(device.Player,replacement)&&device.Generation==generation&&link.Snapshot().Length==0,
            "power-off captures its connection before waiting for other sources and never stops its replacement");
    }

    static async Task PowerOffFailureStillReleasesOriginal()
    {
        await using var fixture=new Fixture();var device=fixture.Rows[0].Device;
        var power=AdvancedPowerOffButton(fixture,device);int generation=device.Generation;
        fixture.Dual.HoldNextPresetStop=true;power.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Dual.PresetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stop=fixture.Stop();fixture.Dual.ReleasePreset.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));await Until(()=>power.IsEnabled,"power-off failure completes during a global stop");
        // The intentionally absent BLE link fails the command. Cleanup must still release the original connection.
        Check(!device.Connected&&device.Player==null&&device.Generation==generation+1,
            "a failed power-off still releases its original connection even when Stop all arrives during the device stop");
        Check(!fixture.Commands().Any(command=>command.Nonzero),"power-off failure and global stop send no positive motor commands");
    }

    static async Task PendingDeviceStartsStayOnOriginalConnection()
    {
        var retargeted=new List<string>();
        foreach(string title in new[]{"Start","Test 2s","Play pattern","Play hardware preset"})
        {
            await using var fixture=new Fixture();var device=fixture.Rows[2].Device;
            Set(fixture.App,"json",new System.Web.Script.Serialization.JavaScriptSerializer());
            Call(fixture.App,"BuildControls",device);
            var controls=DeviceControlElements(device.Controls).ToArray();
            controls.OfType<ComboBox>().Single(c=>c.Items.Contains("Steady")).SelectedItem="Steady";
            var button=controls.OfType<Button>().Single(b=>(string)b.Content==title);
            await fixture.Start();await Until(()=>fixture.Levels().All(level=>level>0),"Studio runs before "+title+" waits for its handoff");
            fixture.Dual.HoldNextLaneStop=true;button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await fixture.Dual.LaneStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            device.Player.CancelAll();device.Generation+=2;
            var link=new FakeLink("replacement");var replacement=new Playback(link,new[]{0});device.Player=replacement;
            fixture.Dual.ReleaseLaneStop.TrySetResult();
            await Until(()=>button.IsEnabled,"old "+title+" request finishes after reconnect",5000);
            await Task.Delay(100);
            if(link.Snapshot().Length>0||replacement.Running)retargeted.Add(title);
            await fixture.Stop();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(()=>title=="Play hardware preset"?replacement.Level(0)<0:replacement.Level(0)>0,
                "a fresh "+title+" request can use the replacement connection");
            await fixture.Stop();await Until(()=>button.IsEnabled,"explicit "+title+" finishes after Stop all");
        }
        Check(retargeted.Count==0,"pending device starts must not switch to a replacement connection: "+string.Join(", ",retargeted));
    }

    static async Task ShortCyclesAcceptBuiltInPresets()
    {
        await using var fixture=new Fixture(false);Call(fixture.App,"AddStudioDraft");
        var document=Get<CurveDocument>(fixture.App,"studioDocument");var editor=Get<CurveEditorControl>(fixture.App,"studioEditor");
        string active=editor.ActiveLaneId;document.SetLength(200);document.ClearHistory();
        foreach(var preset in new[]{(Name:"Wave",Length:400),(Name:"Pulse",Length:400),(Name:"Heartbeat",Length:715),(Name:"Surge",Length:500),(Name:"Staircase",Length:400)})
        {
            var before=document.ExportPoints(active);Call(fixture.App,"ApplyStudioPreset",preset.Name);
            Check(document.LengthMs==preset.Length&&document.GetLane(active).Points.Zip(document.GetLane(active).Points.Skip(1),(a,b)=>b.Ms-a.Ms).All(gap=>gap>=100),
                preset.Name+" expands a short cycle just enough to keep valid point spacing");
            Check(Get<ComboBox>(fixture.App,"studioPresetChoice").SelectedItem as string==preset.Name&&Get<TextBox>(fixture.App,"studioLength").Text==preset.Length.ToString(),
                "preset selection and cycle field reflect the applied "+preset.Name+" shape");
            Check(document.Undo()&&document.LengthMs==200&&document.ExportPoints(active).SequenceEqual(before)&&!document.CanUndo,
                "one Undo restores the original curve and short cycle after "+preset.Name);
            Check(document.Redo()&&document.LengthMs==preset.Length,"Redo restores "+preset.Name+" and its required cycle");
            document.Undo();document.ClearHistory();
        }
        document.SetLength(5000);document.ClearHistory();Call(fixture.App,"ApplyStudioPreset","Heartbeat");
        Check(document.LengthMs==5000,"a preset preserves a cycle that already has enough spacing");
        Check(fixture.Commands().Length==0,"building, undoing and redoing presets never sends motor output");
    }
}
