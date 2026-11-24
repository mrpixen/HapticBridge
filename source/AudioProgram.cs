using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace LovenseConnector
{
    internal sealed class AudioProgram
    {
        public int Id { get; }
        public string Name { get; }
        internal long Started { get; }
        internal AudioProgram(int id,long started,string name)
        {
            if(id<=0||started<=0)throw new ArgumentOutOfRangeException(nameof(id));
            Id=id;Started=started;Name=name??"Program";
        }
        // A restarted program must be selected again, even if Windows reuses its PID.
        internal bool IsCurrent(Func<int,long?> lookup=null)
        {
            try { return (lookup??ReadStart)(Id)==Started; }
            catch { return false; }
        }
        static long? ReadStart(int id)
        {
            using var process=Process.GetProcessById(id);
            return process.HasExited?null:process.StartTime.ToUniversalTime().Ticks;
        }
        internal IAudioSource CreateSource()
        {
            if(!WindowsProcessAudio.Supported)throw new PlatformNotSupportedException("Program audio requires Windows build 20348 or later.");
            return new ProcessAudioSource(Name,()=>IsCurrent(),token=>WindowsProcessAudio.Open(Id,token));
        }
        internal static AudioProgram[] List()
        {
            var programs=new List<AudioProgram>();
            foreach(var process in Process.GetProcesses())
            {
                using(process)
                {
                    try
                    {
                        if(process.Id==Environment.ProcessId||process.HasExited||process.MainWindowHandle==IntPtr.Zero)continue;
                        // Avoid collecting document titles, paths or command lines.
                        programs.Add(new AudioProgram(process.Id,process.StartTime.ToUniversalTime().Ticks,
                            process.ProcessName+" ("+process.Id+")"));
                    }
                    catch { /* Exited or inaccessible programs are not selectable. */ }
                }
            }
            return programs.OrderBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}
