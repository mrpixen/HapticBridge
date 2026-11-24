using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using LovenseConnector;
internal static partial class ScreenSyncTests
{
    static ScreenPixels BadgeFrame(int appearance)
    {
        var bytes=new byte[32*32*4];
        for(int y=2;y<30;y++)for(int x=2;x<30;x++)if(x==2||x==29||y==2||y==29||x==6+appearance*2&&y>8&&y<24) {
            int at=(y*32+x)*4;bytes[at]=bytes[at+1]=bytes[at+2]=255;bytes[at+3]=255;
        }
        return new ScreenPixels(bytes,32,32,0);
    }
    static async Task BadgeLearning(ConnectorApp app)
    {
        var profile=Get<ScreenProfile>(app,"screenProfile");var saved=ScreenCalibrationData.Capture(profile).Snapshot();
        var frame=Get<ScreenPixels>(app,"screenFrame");bool frozen=Get<bool>(app,"screenFrozen");
        try {
            profile.Badge=new HudRegion {Width=1,Height=1};profile.BadgeMask=null;profile.BadgeVariants.Clear();Set(app,"screenFrozen",true);
            for(int i=0;i<8;i++) {
                Set(app,"screenFrame",BadgeFrame(i));await Invoke(app,"LearnScreenBadge");
            }
            Check(profile.BadgeVariants.Count==7,"learning eight badge appearances retains all previous templates");
            int undo=((ICollection)Get<object>(app,"screenCalibrationUndo")).Count;
            await Invoke(app,"LearnScreenBadge");
            Check(profile.BadgeVariants.Count==7&&((ICollection)Get<object>(app,"screenCalibrationUndo")).Count==undo,"learning a duplicate does not add a template or consume Undo");
            string original=ScreenCalibrationFiles.Encode(profile);Set(app,"screenFrame",BadgeFrame(8));bool rejected=false;
            try{await Invoke(app,"LearnScreenBadge");}catch(TargetInvocationException ex)when(ex.InnerException is ArgumentException){rejected=true;}
            Check(rejected&&ScreenCalibrationFiles.Encode(profile)==original,"ninth appearance rejected without evicting a learned badge");
            await Invoke(app,"ClearScreenBadges");Check(profile.BadgeMask==null&&profile.BadgeVariants.Count==0,"clear removes every badge appearance");
            await Invoke(app,"UndoScreenCalibration");Check(ScreenCalibrationFiles.Encode(profile)==original,"Undo restores the complete badge bank");
            await Invoke(app,"UndoScreenCalibration");Check(profile.BadgeVariants.Count==6,"Undo learning restores the previous badge bank");
        } finally {
            profile.Badge=saved.Badge;profile.BadgeMask=saved.BadgeMask;profile.BadgeVariants=saved.BadgeVariants;
            Set(app,"screenFrame",frame);Set(app,"screenFrozen",frozen);Call(app,"RefreshScreenCalibration");
        }
    }
}
