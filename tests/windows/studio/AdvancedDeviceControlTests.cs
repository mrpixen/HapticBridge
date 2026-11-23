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
}
