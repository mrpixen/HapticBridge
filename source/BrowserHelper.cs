using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LovenseConnector
{
    internal static class BrowserHelper
    {
        internal const string ResourcePrefix="HapticBridge.BrowserHelper.";
        static readonly string[] Files={"manifest.json","popup.html","popup.css","popup.js","clock-bridge.js","README.txt"};
        const int MaximumFileBytes=256*1024;

        internal static void Export(string destination)
        {
            var assembly=typeof(BrowserHelper).Assembly;
            Export(destination,assembly.GetManifestResourceNames(),assembly.GetManifestResourceStream);
        }

        // The manifest is fixed at build time. No profile, browser, device or session data is read.
        internal static void Export(string destination,IEnumerable<string> resourceNames,Func<string,Stream> openResource)
        {
            if(string.IsNullOrWhiteSpace(destination)||resourceNames==null||openResource==null)
                throw new InvalidOperationException("The browser helper could not be saved.");
            var expected=new HashSet<string>(Files.Select(name=>ResourcePrefix+name),StringComparer.Ordinal);
            var actual=resourceNames.Where(name=>name.StartsWith(ResourcePrefix,StringComparison.Ordinal)).ToArray();
            if(actual.Length!=expected.Count||!expected.SetEquals(actual))
                throw new InvalidOperationException("The browser helper files are unavailable.");

            // Validate and read every resource before creating anything beside the selected file.
            var payloads=new List<(string Name,byte[] Bytes)>();
            foreach(string name in Files)
            {
                if(name!=Path.GetFileName(name)||name.IndexOfAny(new[]{'/', '\\', ':'})>=0)
                    throw new InvalidOperationException("The browser helper files are unavailable.");
                using var input=openResource(ResourcePrefix+name);
                if(input==null)throw new InvalidOperationException("The browser helper files are unavailable.");
                using var bytes=new MemoryStream();
                var buffer=new byte[8192];
                int count;
                while((count=input.Read(buffer,0,buffer.Length))>0)
                {
                    if(bytes.Length+count>MaximumFileBytes)
                        throw new InvalidOperationException("The browser helper files are unavailable.");
                    bytes.Write(buffer,0,count);
                }
                if(bytes.Length==0)throw new InvalidOperationException("The browser helper files are unavailable.");
                payloads.Add((name,bytes.ToArray()));
            }

            string target=Path.GetFullPath(destination);
            string directory=Path.GetDirectoryName(target);
            if(string.IsNullOrEmpty(directory)||!Directory.Exists(directory))
                throw new InvalidOperationException("The selected folder is unavailable.");
            string temporary=Path.Combine(directory,".hapticbridge-helper-"+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    using(var archive=new ZipArchive(file,ZipArchiveMode.Create,true))
                        foreach(var payload in payloads)
                        {
                            var entry=archive.CreateEntry(payload.Name,CompressionLevel.Optimal);
                            entry.LastWriteTime=new DateTimeOffset(2026,1,1,0,0,0,TimeSpan.Zero);
                            using var output=entry.Open();output.Write(payload.Bytes,0,payload.Bytes.Length);
                        }
                    file.Flush(true);
                }
                if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
            }
            finally
            {
                try { if(File.Exists(temporary))File.Delete(temporary); } catch { /* Keep the original error. */ }
            }
        }
    }
}
