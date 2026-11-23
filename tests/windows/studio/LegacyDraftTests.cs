using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using LovenseConnector;

// Synthetic legacy data only: no profile, real device identifiers or hardware.
internal static class LegacyDraftTests
{
    internal static int Run()
    {
        int assertions=0;
        void Check(bool condition,string message)
        { assertions++;if(!condition)throw new Exception("Legacy draft import: "+message); }
        var legacy=new MixerPreset { shape="Wave",high=12,low=0,periodMs=2400,durationMs=10000,transitionMs=100,
            levels=new Dictionary<string,int> { ["saved-a/1"]=5,["saved-b/0"]=11,["saved-c/2"]=17 } };

        var offline=StudioPresetRules.ConvertLegacy(legacy,Array.Empty<string>());
        Check(offline.lanes.Length==3,"zero connected motors retains every saved lane");
        Check(offline.lanes.Select(lane=>lane.level).SequenceEqual(new[]{5,11,17}),"offline saved levels retain insertion order");
        Check(offline.lanes.All(lane=>lane.selected),"saved legacy selections remain selected in draft metadata");
        Check(StudioPresetRules.Validate(offline).Lanes.Count==3,"offline conversion is a valid editable version2 document");
        Check(offline.lanes.Select(lane=>lane.points.Max(point=>point.level)).SequenceEqual(new[]{5,11,17}),"independent waveform amplitudes survive conversion");

        var partial=StudioPresetRules.ConvertLegacy(legacy,new[]{"saved-a/1","new-motor/0"});
        Check(partial.lanes.Length==4,"unmatched saved lanes remain after the current motor slots");
        Check(partial.lanes.Select(lane=>lane.level).SequenceEqual(new[]{5,12,11,17}),"identity match and unassigned lane levels are preserved");
        Check(partial.lanes.Select(lane=>lane.selected).SequenceEqual(new[]{true,false,true,true}),"unrelated current motor is not selected by legacy import");
        Check(StudioPresetRules.Validate(partial).Lanes.Count==4,"partial hardware conversion validates without dropping saved curves");

        var reordered=StudioPresetRules.ConvertLegacy(legacy,new[]{"saved-c/2","saved-a/1","saved-c/2"});
        Check(reordered.lanes.Select(lane=>lane.level).SequenceEqual(new[]{17,5,11}),"current exact matches lead, duplicated current keys do not duplicate saved data");
        Check(StudioPresetRules.ConvertLegacy(legacy,null).lanes.Length==3,"missing current-motor list behaves as offline import");
        legacy.stagger=true;
        var ripple=StudioPresetRules.ConvertLegacy(legacy,Array.Empty<string>());
        Check(ripple.lanes.Select(lane=>lane.points[0].level).SequenceEqual(new[]{0,8,13}),"saved Ripple offsets survive conversion instead of aligning every motor");
        var ripplePartial=StudioPresetRules.ConvertLegacy(legacy,new[]{"saved-a/1","new-motor/0"});
        Check(ripplePartial.lanes.Select(lane=>lane.points[0].level).SequenceEqual(new[]{0,0,8,13}),"unselected current motors do not alter saved Ripple phases");
        var rippleReordered=StudioPresetRules.ConvertLegacy(legacy,new[]{"saved-c/2","saved-a/1"});
        Check(rippleReordered.lanes.Select(lane=>lane.points[0].level).SequenceEqual(new[]{13,0,8}),"saved Ripple phases follow their curves when current devices are reordered");
        legacy.stagger=false;
        string exported=JsonSerializer.Serialize(partial);
        Check(!exported.Contains("saved-",StringComparison.Ordinal)&&!exported.Contains("new-motor",StringComparison.Ordinal),"version2 data contains no legacy or current device identifiers");

        bool rejected=false;
        try { StudioPresetRules.ConvertLegacy(new MixerPreset { levels=null },Array.Empty<string>()); }
        catch(ArgumentException){rejected=true;}
        Check(rejected,"missing saved level map is rejected");
        rejected=false;
        var oversized=new MixerPreset { levels=Enumerable.Range(0,65).ToDictionary(index=>"synthetic-"+index,index=>1) };
        try { StudioPresetRules.ConvertLegacy(oversized,Array.Empty<string>()); }
        catch(ArgumentException){rejected=true;}
        Check(rejected,"oversized saved map is rejected rather than truncated");
        Console.WriteLine("Legacy draft import: "+assertions+" assertions passed (synthetic data only).");
        return assertions;
    }
}
