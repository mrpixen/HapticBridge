using System;
using System.Threading.Tasks;

namespace LovenseConnector
{
    internal sealed partial class Playback
    {
        public void ValidateFunction(string function,int value,int high=0)
        {
            if(profile==null)throw new NotSupportedException("Function controls are unavailable for this device.");
            switch(function) {
                case "depth":profile.DepthCommand(value);break;
                case "position":profile.PositionCommand(value);break;
                case "stroke":profile.ValidateStroke(value,high);break;
                case "reverse":if(!profile.HasRotation)throw new NotSupportedException("Rotation is not supported by this model.");break;
                default:throw new ArgumentException("Unknown device function.");
            }
        }
        // Settings and point moves stop current playback first. They cannot be mixed
        // with an old pattern that could immediately overwrite the new motion mode.
        public async Task ApplyFunction(string function,int value,int high=0,Func<bool> isCurrent=null)
        {
            ValidateFunction(function,value,high);
            int expected,revision;lock(sync) { if(stopping>0||isCurrent!=null&&!isCurrent())return;expected=epoch+1;revision=functionRevision; }
            await StopAll().ConfigureAwait(false);
            await controls.WaitAsync().ConfigureAwait(false);
            try {
                lock(sync)if(epoch!=expected||functionRevision!=revision||stopping>0||isCurrent!=null&&!isCurrent())return;
                lock(sync)if(jobs.Count>0||orderedMotors.Count>0||preset!=null)throw new InvalidOperationException("Playback changed while applying the setting. Retry after stopping output.");
                if(function=="stroke") { profile.SetStroke(value,high);return; }
                string command=function=="position"?profile.PositionCommand(value):function=="depth"?profile.DepthCommand(value):"RotateChange;";
                // Recheck after the transport queue as well as the playback queue.
                Func<string> sample=()=> {
                    lock(sync) {
                        if(epoch!=expected||functionRevision!=revision||isCurrent!=null&&!isCurrent())return null;
                        if(function=="position") { positionMoving=true;sent[7]=-1;Emit(7,-1,"Position move"); }
                        return command;
                    }
                };
                var latest=link as ILatestCommandLink;
                try {
                    if(latest!=null)await latest.CommandLatest(sample,System.Threading.CancellationToken.None).ConfigureAwait(false);
                    else await SendOrderedFallback(sample,System.Threading.CancellationToken.None).ConfigureAwait(false);
                    if(function=="position")await Task.Delay(300).ConfigureAwait(false);
                }
                finally {
                    // Position zero is a real endpoint, never a stop command.
                    if(function=="position") {
                        try { await ZeroAll().ConfigureAwait(false); }
                        finally { lock(sync)positionMoving=false; }
                    }
                }
            }
            finally { controls.Release(); }
        }
    }
}
