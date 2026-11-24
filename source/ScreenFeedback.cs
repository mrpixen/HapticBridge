using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace LovenseConnector
{
    internal sealed class ScreenMotorFeedback
    {
        public bool Enabled { get; set; }=true;
        public int Maximum { get; set; }=20;
        public int Events { get; set; }=7;
        public bool Valid()=>Maximum>=0&&Maximum<=20&&Events>=0&&Events<=7;
        public static int EventMask(string kind)=>kind=="Damage"?1:kind=="Kill"?2:kind=="Death"?4:0;
    }

    internal sealed class ScreenFeedbackPreferences
    {
        public int Version { get; set; }=1;
        public int DamageLevel { get; set; }=14;
        public int KillLevel { get; set; }=20;
        public int DeathLevel { get; set; }=16;
        public Dictionary<string,ScreenMotorFeedback> Motors { get; set; }=new Dictionary<string,ScreenMotorFeedback>();
        public bool Valid()=>Version==1&&DamageLevel>=0&&DamageLevel<=20&&KillLevel>=0&&KillLevel<=20&&DeathLevel>=0&&DeathLevel<=20&&
            Motors!=null&&Motors.Count<=256&&Motors.All(p=>p.Key.Length==64&&p.Key.All(Uri.IsHexDigit)&&p.Value!=null&&p.Value.Valid());
    }

    internal sealed partial class ConnectorApp
    {
        ScreenFeedbackPreferences screenFeedback=new ScreenFeedbackPreferences();
        TextBlock screenFeedbackStatus;
        bool screenFeedbackLoadFailed;
        string ScreenFeedbackPath=>Path.Combine(dataDir??root,"screen-feedback.json");
        // Local matching only. Never store device names or raw addresses in this file.
        static string ScreenFeedbackKey(Device device,int channel)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(MixerKey(device,channel))));

        void LoadScreenFeedback()
        {
            screenFeedback=new ScreenFeedbackPreferences { DamageLevel=screenProfile.DamageLevel,KillLevel=screenProfile.KillLevel,DeathLevel=screenProfile.DeathLevel };
            screenFeedbackLoadFailed=false;
            try {
                if(!File.Exists(ScreenFeedbackPath))return;
                if(new FileInfo(ScreenFeedbackPath).Length>100000)throw new InvalidDataException();
                var loaded=json.Deserialize<ScreenFeedbackPreferences>(File.ReadAllText(ScreenFeedbackPath));
                if(loaded==null||!loaded.Valid())throw new InvalidDataException();
                screenFeedback=loaded;
                screenProfile.DamageLevel=loaded.DamageLevel;screenProfile.KillLevel=loaded.KillLevel;screenProfile.DeathLevel=loaded.DeathLevel;
            }catch {screenFeedbackLoadFailed=true;}
        }

        bool SaveScreenFeedback()
        {
            string temporary=ScreenFeedbackPath+".tmp";
            try {
                screenFeedback.DamageLevel=screenProfile.DamageLevel;screenFeedback.KillLevel=screenProfile.KillLevel;screenFeedback.DeathLevel=screenProfile.DeathLevel;
                if(!screenFeedback.Valid())throw new InvalidDataException();
                Directory.CreateDirectory(Path.GetDirectoryName(ScreenFeedbackPath));
                File.WriteAllText(temporary,json.Serialize(screenFeedback));
                File.Move(temporary,ScreenFeedbackPath,true);
                if(screenFeedbackStatus!=null)screenFeedbackStatus.Visibility=Visibility.Collapsed;
                return true;
            }catch {
                if(screenFeedbackStatus!=null){screenFeedbackStatus.Text="Could not save feedback. Changes apply for this session.";screenFeedbackStatus.Visibility=Visibility.Visible;}
                return false;
            }finally {try{if(File.Exists(temporary))File.Delete(temporary);}catch{}}
        }

        static int ScreenRouteEvents(ScreenRoute route)=>route.Events.Where(p=>p.Value.IsChecked==true).Aggregate(0,(mask,p)=>mask|ScreenMotorFeedback.EventMask(p.Key));
        void SaveScreenRoute(ScreenRoute route)
        {
            screenFeedback.Motors[ScreenFeedbackKey(route.Device,route.Channel)]=new ScreenMotorFeedback {
                Enabled=route.Include.IsChecked==true,Maximum=(int)route.Ceiling.Value,Events=ScreenRouteEvents(route)
            };
            SaveScreenFeedback();
        }
    }
}
