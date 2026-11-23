using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LovenseConnector
{
    internal sealed partial class ConnectorApp
    {
        int studioPreviewDurationMs;

        string NewStudioDraftKey()=>"draft:"+(++studioDraftSequence).ToString(CultureInfo.InvariantCulture);
        void AddStudioDraft()
        {
            if(studioDocument.Lanes.Count>=64)throw new ArgumentException("A mix supports up to 64 curves.");
            string key=NewStudioDraftKey();studioDraftOptions[key]=new StudioLanePreset();
            studioDocument.AddLane(key,"Unassigned curve "+studioDraftSequence);
            studioEditor.ActiveLaneId=key;RebuildStudioSelectors();UpdateStudioInspector();
        }
        void RemoveStudioDraft()
        {
            string key=studioEditor.ActiveLaneId;
            if(key==null||mixerRows.ContainsKey(key))throw new ArgumentException("Choose an unassigned curve to remove.");
            studioDocument.RemoveLane(key);studioDraftOptions.Remove(key);RebuildStudioSelectors();SetStudioMode(studioPatternMode);UpdateStudioInspector();
        }

        void DuplicateStudioCurve()
        {
            if(studioDocument.Lanes.Count>=64)throw new ArgumentException("A mix supports up to 64 curves.");
            if(studioDocument.IsEditing)throw new InvalidOperationException("Finish the current point edit first.");
            var source=studioDocument.GetLane(studioEditor.ActiveLaneId);
            var options=studioDraftOptions.GetValueOrDefault(source.Id)??new StudioLanePreset();
            bool assigned=mixerRows.TryGetValue(source.Id,out var motor);
            string key=NewStudioDraftKey();
            studioDraftOptions[key]=new StudioLanePreset { level=assigned?(int)motor.Level.Value:options.level,
                selected=assigned?motor.Include.IsChecked==true:options.selected };
            studioDocument.AddLane(key,"Unassigned curve "+studioDraftSequence,source.Points,source.Interpolation);
            studioEditor.ActiveLaneId=key;RebuildStudioSelectors();SetStudioMode(true);UpdateStudioInspector();
            hint.Text="Curve duplicated · independent, unassigned copy";
        }

        // Visual-only sampling: no Playback, Bluetooth, device/session or API calls.
        void StartStudioPreview()
        {
            if(studioDocument.Lanes.Count==0)AddStudioDraft();
            int requested=Number(mixerDuration);new PatternSpec { durationMs=requested }.Validate();
            studioPreviewDurationMs=studioDocument.Loop?requested:requested==0?studioDocument.LengthMs:Math.Min(requested,studioDocument.LengthMs);
            studioPreviewClock=Stopwatch.StartNew();studioPreview.Content="Stop preview";
            studioPreviewTimer.Start();RefreshStudioPreview();
        }
        void StopStudioPreview()
        {
            studioPreviewTimer?.Stop();studioPreviewClock=null;
            if(studioEditor!=null)studioEditor.PreviewPositionMs=null;
            if(studioPreview!=null)studioPreview.Content="Preview";
            if(studioPreviewLabel!=null)studioPreviewLabel.Text="";
        }
        void RefreshStudioPreview()
        {
            if(studioPreviewClock==null)return;
            double elapsed=studioPreviewClock.Elapsed.TotalMilliseconds;
            if(studioPreviewDurationMs>0&&elapsed>=studioPreviewDurationMs){StopStudioPreview();studioPreviewLabel.Text="Preview finished";return;}
            double at=studioDocument.Loop?elapsed%studioDocument.LengthMs:Math.Min(elapsed,studioDocument.LengthMs);
            studioEditor.PreviewPositionMs=at;
            string key=studioEditor.ActiveLaneId;
            studioPreviewLabel.Text=key!=null&&studioDocument.ContainsLane(key)?"Visual only · "+studioDocument.LevelAt(key,at)+" / 20":"Visual only";
        }

        StudioPreset ExportStudioMix()
        {
            var groups=new Dictionary<string,int>();
            var lanes=studioDocument.Lanes.Select(lane=>
            {
                if(!groups.ContainsKey(lane.GroupId))groups.Add(lane.GroupId,groups.Count);
                bool assigned=mixerRows.TryGetValue(lane.Id,out var motor);
                var options=studioDraftOptions.GetValueOrDefault(lane.Id)??new StudioLanePreset();
                return new StudioLanePreset { points=lane.Points.Select(p=>new Frame { ms=p.Ms,level=p.Level }).ToArray(),curve=lane.Interpolation.ToString().ToLowerInvariant(),
                    group=groups[lane.GroupId],level=assigned?(int)motor.Level.Value:options.level,selected=assigned?motor.Include.IsChecked==true:options.selected };
            }).ToArray();
            var preset=new StudioPreset { lengthMs=studioDocument.LengthMs,loop=studioDocument.Loop,durationMs=Number(mixerDuration),transitionMs=Number(mixerTransition),lanes=lanes };
            StudioPresetRules.Validate(preset);return preset;
        }

        void AssignStudioCurve(string sourceKey,string targetKey)
        {
            if(!studioDocument.ContainsLane(sourceKey)||!mixerRows.TryGetValue(targetKey,out var motor)||!motor.Device.Connected)
                throw new ArgumentException("Choose a curve and a connected motor.");
            var source=studioDocument.GetLane(sourceKey);
            if(sourceKey==targetKey)return;
            studioDocument.BeginEdit();
            try { studioDocument.Unlink(targetKey);studioDocument.ReplaceCurve(targetKey,source.Points,source.Interpolation);studioDocument.CommitEdit(); }
            catch { studioDocument.CancelEdit();throw; }
            studioEditor.ActiveLaneId=targetKey;UpdateStudioInspector();hint.Text="Curve assigned · press Start selected to play";
        }
        void ShowStudioAssignment()
        {
            string source=studioEditor.ActiveLaneId;
            if(source==null)throw new ArgumentException("Choose a curve first.");
            var targets=mixerRows.Where(pair=>pair.Value.Device.Connected&&pair.Value.Device.Player!=null).ToArray();
            if(targets.Length==0)throw new ArgumentException("Connect a motor to assign this curve. Preview and saving work without one.");
            var menu=new ContextMenu();
            foreach(var pair in targets)
            {
                string key=pair.Key;var item=new MenuItem { Header=studioDocument.GetLane(key).DisplayName,IsEnabled=key!=source };
                item.Click+=(s,e)=>{try { AssignStudioCurve(source,key); }catch(Exception ex){Report(ex);}};menu.Items.Add(item);
            }
            menu.PreviewKeyDown+=async(s,e)=>{if(e.Key==Key.Escape){menu.IsOpen=false;e.Handled=true;await StopAll();}};
            menu.PlacementTarget=studioMore;menu.IsOpen=true;
        }
    }
}
