package io.pixen.immersion;
import java.nio.file.*;
public final class ConfigTests {
    public static void main(String[] args) throws Exception {
        Path path=Path.of(args[0]);Files.createDirectories(path.getParent());
        Files.writeString(path,"""
            {"enabled":false,"gatewayConnectionFile":"retained-connection.json","events":{
            "footstep":{"level":4,"durationMs":260,"pulses":1,"priority":10,"cooldownMs":100,"shape":"pulse","route":"alternate"},
            "drinking":{"level":6,"durationMs":1800,"pulses":3,"priority":5,"cooldownMs":1700,"shape":"pulse","route":"alternate"}}}
            """);
        var config=ImmersionConfig.load(path);
        if(config.profileRevision!=2||config.events.get("footstep").level!=0||!config.events.get("footstep").route.equals("gush")||config.events.get("mining").level!=20) throw new AssertionError("old profile migration");
        System.out.println("PASS previous profiles migrate to requested walking and mining behavior");
        if(config.enabled||!config.gatewayConnectionFile.equals("retained-connection.json")||config.events.get("drinking").level!=6) throw new AssertionError("unrelated settings changed");
        System.out.println("PASS migration retains other settings");
        config.events.get("footstep").level=2;config.save(path);config=ImmersionConfig.load(path);
        if(config.events.get("footstep").level!=2) throw new AssertionError("migration repeated");
        System.out.println("PASS later customization survives reload");
        if(!config.deviceChannels.isEmpty()||!config.allowsChannel("legacy",7))throw new AssertionError("old configs must keep all channels");
        config.deviceChannels.put("synthetic",java.util.List.of(1,7,9));config.deviceChannels.put("muted",java.util.List.of());config.save(path);config=ImmersionConfig.load(path);
        if(!config.allowsChannel("synthetic",7)||config.allowsChannel("synthetic",5)||config.allowsChannel("muted",0)||!config.allowsChannel("unlisted",4))throw new AssertionError("channel selections did not survive reload");
        System.out.println("PASS per-device channel selection round trip and legacy defaults");
        for(String value:new String[]{"null","{\"test\":null}","{\"test\":[10]}","{\"test\":[-1]}","{\"test\":[1,1]}","{\"test\":[null]}"}) {
            Files.writeString(path,"{\"deviceChannels\":"+value+"}");boolean rejected=false;
            try{ImmersionConfig.load(path);}catch(IllegalArgumentException ex){rejected=true;}
            if(!rejected)throw new AssertionError("invalid channel selection accepted: "+value);
        }
        System.out.println("PASS malformed channel selections rejected");
    }
}
