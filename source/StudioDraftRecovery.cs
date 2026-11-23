using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LovenseConnector
{
    // The normal mix format contains numbered lanes, never device IDs or names.
    internal static class StudioDraftStore
    {
        const int MaximumBytes=1048576;
        internal static string Encode(StudioPreset preset)
        {
            StudioPresetRules.Validate(preset);
            string text=JsonSerializer.Serialize(preset);
            if(Encoding.UTF8.GetByteCount(text)>MaximumBytes)throw new ArgumentException("Draft is too large.");
            return text;
        }
        internal static StudioPreset Load(string path)
        {
            if(!File.Exists(path))return null;
            if(new FileInfo(path).Length>MaximumBytes)throw new ArgumentException("Draft is too large.");
            string text=File.ReadAllText(path);
            if(Encoding.UTF8.GetByteCount(text)>MaximumBytes)throw new ArgumentException("Draft is too large.");
            var preset=JsonSerializer.Deserialize<StudioPreset>(text);
            StudioPresetRules.Validate(preset);return preset;
        }
        internal static void Save(string path,string text)
        {
            // Same-directory replacement preserves the previous draft if writing fails.
            string temporary=path+".tmp.mix.json";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try { File.WriteAllText(temporary,text);File.Move(temporary,path,true); }
            finally { try { if(File.Exists(temporary))File.Delete(temporary); } catch(IOException){}catch(UnauthorizedAccessException){} }
        }
    }

    internal sealed partial class ConnectorApp
    {
        DispatcherTimer studioDraftTimer;
        bool studioDraftDirty,studioDraftHasWork;
        string studioSavedDraft;
        StudioPreset studioRecoveryDraft;
        MenuItem studioRestoreDraftChoice;
        string StudioDraftPath=>Path.Combine(dataDir,"studio-draft.mix.json");

        void InitializeStudioDraftRecovery()
        {
            if(dataDir==null||studioDraftTimer!=null)return;
            studioDraftTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(750) };
            studioDraftTimer.Tick+=(s,e)=> {
                if(studioDocument.IsEditing)return;
                studioDraftTimer.Stop();SaveStudioDraft();
            };
            try {
                studioRecoveryDraft=StudioDraftStore.Load(StudioDraftPath);
                studioRestoreDraftChoice.IsEnabled=studioRecoveryDraft!=null;
                if(studioRecoveryDraft!=null)hint.Text="Saved draft available · More → Restore previous draft";
            }
            catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is JsonException) {
                hint.Text="Saved draft could not be restored · the file was left unchanged";
            }
            mixerDuration.TextChanged+=(s,e)=>QueueStudioDraftSave();
            mixerTransition.TextChanged+=(s,e)=>QueueStudioDraftSave();
        }
        void QueueStudioDraftSave(bool patternEdit=false)
        {
            if(studioDraftTimer==null||studioLoading||closing)return;
            if(patternEdit)studioDraftHasWork=true;
            if(!studioDraftHasWork)return;
            studioDraftDirty=true;studioDraftTimer.Stop();studioDraftTimer.Start();
        }
        async Task RestoreStudioDraft()
        {
            var preset=studioRecoveryDraft;
            if(preset==null)throw new ArgumentException("No previous draft is available.");
            var stop=StopAll();int operation=operatorGeneration;
            if(!await stop||closing||operation!=operatorGeneration)return;
            ApplyStudioMix(preset);hint.Text="Previous draft restored · output off";
        }
        bool SaveStudioDraft()
        {
            if(!studioDraftDirty||dataDir==null||studioDocument.IsEditing||studioDocument.Lanes.Count==0)return false;
            try {
                string text=StudioDraftStore.Encode(ExportStudioMix());
                if(text!=studioSavedDraft)StudioDraftStore.Save(StudioDraftPath,text);
                studioSavedDraft=text;studioDraftDirty=false;return true;
            }
            catch(ArgumentException) {hint.Text="Draft not saved · check pattern timing";return false;}
            catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException) {
                hint.Text="Draft could not be saved · the previous draft is intact";return false;
            }
        }
        void FinishStudioDraftSaving(){studioDraftTimer?.Stop();SaveStudioDraft();}
    }
}
