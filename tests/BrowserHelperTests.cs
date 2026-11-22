using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using LovenseConnector;

internal static class BrowserHelperTests
{
    static int assertions;
    static void Check(bool condition,string message){assertions++;if(!condition)throw new Exception(message);}

    public static int Main()
    {
        string temporaryRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        string directory=Path.GetFullPath(Path.Combine(temporaryRoot,"HapticBridge-helper-tests-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            string destination=Path.Combine(directory,"helper.zip");
            var expected=new[]{"README.txt","clock-bridge.js","manifest.json","popup.css","popup.html","popup.js"};
            var assembly=typeof(BrowserHelper).Assembly;
            BrowserHelper.Export(destination);
            using(var file=File.OpenRead(destination))
            using(var archive=new ZipArchive(file,ZipArchiveMode.Read))
            {
                Check(archive.Entries.Select(entry=>entry.FullName).OrderBy(name=>name,StringComparer.Ordinal).SequenceEqual(expected),"Export contains only the reviewed flat helper files.");
                foreach(var entry in archive.Entries)
                {
                    using var resource=assembly.GetManifestResourceStream(BrowserHelper.ResourcePrefix+entry.FullName);
                    using var exported=entry.Open();using var left=new MemoryStream();using var right=new MemoryStream();
                    resource.CopyTo(left);exported.CopyTo(right);
                    Check(left.ToArray().SequenceEqual(right.ToArray()),"Export preserves the embedded helper exactly.");
                }
                using var manifestStream=archive.GetEntry("manifest.json").Open();using var manifest=JsonDocument.Parse(manifestStream);
                var root=manifest.RootElement;
                Check(root.GetProperty("manifest_version").GetInt32()==3,"Helper uses its reviewed MV3 manifest.");
                Check(root.GetProperty("permissions").EnumerateArray().Select(v=>v.GetString()).OrderBy(v=>v,StringComparer.Ordinal).SequenceEqual(new[]{"activeTab","scripting"}),"Helper permissions are limited to deliberate active-tab actions.");
                Check(!root.TryGetProperty("host_permissions",out _)&&!root.TryGetProperty("content_scripts",out _)&&!root.TryGetProperty("background",out _),"Export adds no broad site access or automatic injection.");
            }
            Check(Directory.GetFiles(directory).Length==1,"Successful export leaves no intermediate files.");

            var marker=Encoding.UTF8.GetBytes("original user-selected destination");
            File.WriteAllBytes(destination,marker);
            var resources=expected.Select(name=>BrowserHelper.ResourcePrefix+name).ToArray();
            void MustPreserve(IEnumerable<string> names,Func<string,Stream> open,string message)
            {
                bool failed=false;try{BrowserHelper.Export(destination,names,open);}catch{failed=true;}
                Check(failed,message);
                Check(File.ReadAllBytes(destination).SequenceEqual(marker),"Failure preserves the existing selected file.");
                Check(Directory.GetFiles(directory).Length==1,"Failure leaves no extra files.");
            }
            MustPreserve(resources.Skip(1),name=>new MemoryStream(new byte[]{1}),"Missing embedded resources fail before replacing a file.");
            MustPreserve(resources.Append(BrowserHelper.ResourcePrefix+"../private.txt"),name=>new MemoryStream(new byte[]{1}),"Unexpected or traversal-named resources are refused.");
            MustPreserve(resources,name=>name.EndsWith("popup.js",StringComparison.Ordinal)?null:new MemoryStream(new byte[]{1}),"Unreadable embedded resource fails safely.");
            MustPreserve(resources,name=>new MemoryStream(new byte[256*1024+1]),"Oversized embedded files are refused.");
            MustPreserve(resources,name=>throw new IOException("synthetic resource read failure"),"Resource read failures preserve the selected file.");
            BrowserHelper.Export(destination);
            using(var archive=ZipFile.OpenRead(destination))Check(archive.Entries.Count==expected.Length,"An existing destination is replaced with the complete valid archive.");
            Check(Directory.GetFiles(directory).Length==1,"Replacement leaves no backup or partial archive.");

            string blocked=Path.Combine(directory,"existing-directory.zip");Directory.CreateDirectory(blocked);
            bool blockedFailed=false;try{BrowserHelper.Export(blocked);}catch{blockedFailed=true;}
            Check(blockedFailed&&Directory.Exists(blocked),"An unwritable final destination fails without damaging it.");
            Check(Directory.GetFiles(directory).Length==1,"A final rename failure removes its temporary archive.");
            Console.WriteLine("Browser helper: "+assertions+" assertions passed.");return 0;
        }
        finally
        {
            if(!string.Equals(Path.GetDirectoryName(directory),temporaryRoot,StringComparison.OrdinalIgnoreCase)
                ||!Path.GetFileName(directory).StartsWith("HapticBridge-helper-tests-",StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test directory.");
            Directory.Delete(directory,true);
        }
    }
}
