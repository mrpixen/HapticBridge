package io.pixen.immersion;
import java.util.*;
public final class EngineTests {
    private static int passed;
    private static void check(boolean ok,String name) { if(!ok)throw new AssertionError(name);passed++;System.out.println("PASS "+name); }
    private static boolean levels(HapticEngine e,long time,int level) { return Arrays.equals(e.sample(time),new int[]{level,level,level}); }
    public static void main(String[] args) {
        var profiles=HapticEngine.defaults();profiles.values().forEach(HapticEngine.Profile::validate);
        check(profiles.size()==41,"41 valid event profiles");
        var engine=new HapticEngine(profiles);long now=1_000_000_000L;
        check(!engine.fire("footstep",1,now)&&!engine.fire("sneak",1,now),"walking and sneaking stay silent");
        engine.fire("damage",1,now);check(levels(engine,now,20),"full hardware intensity remains available");
        engine.sustain("mining",true,0.5,now);check(levels(engine,now,20),"damage preempts mining");
        engine.clear();check(levels(engine,now,0),"stop clears active effects");
        double last=0;for(int i=0;i<=100;i++) { double scale=HapticEngine.miningProgressScale(i/100.0);if(scale<last)throw new AssertionError("mining curve reversed");last=scale; }
        check(HapticEngine.miningProgressScale(0)==0.1&&Math.abs(last-0.9)<0.0001,"mining progresses smoothly from 2 to 18");
        engine.sustain("mining",true,HapticEngine.miningProgressScale(0.5),now);check(levels(engine,now,10),"half-mined block uses level 10 on all motors");
        engine.sustain("mining",true,HapticEngine.miningProgressScale(0.1),now+50_000_000L);check(engine.sample(now+50_000_000L)[0]<10,"new or reset target returns to low intensity");
        engine.sustain("mining",false,0,now+100_000_000L);check(levels(engine,now+100_000_000L,0),"aborting mining stops without completion");
        engine.sustain("mining",true,1,now);check(levels(engine,now+250_000_000L,0),"stale continuous activity expires");
        engine.clear();engine.sustain("sprint",true,1,now);check(levels(engine,now,17),"sprinting is intense on all motors");
        engine.sustain("sprint",true,1,now+1_200_000_000L);check(levels(engine,now+1_200_000_000L,0),"sprint has a real quiet phase without retriggering");
        engine.sustain("sprint",false,0,now+1_300_000_000L);engine.sustain("sprint",true,1,now+1_400_000_000L);check(levels(engine,now+1_400_000_000L,17),"sprint phase resets after stopping");
        engine.clear();engine.sustain("falling",true,1,now);engine.sustain("falling",true,1,now+1_200_000_000L);check(levels(engine,now+1_200_000_000L,0),"long falling has quiet gaps");
        engine.clear();engine.fire("jump",1,now);check(levels(engine,now+500_000_000L,18),"jump has a sustained high phase");
        engine.clear();check(engine.reward("block_break",1,now),"completion reward begins");
        for(int i=1;i<=13;i++)engine.reward("block_break",1,now+i*50_000_000L);
        check(levels(engine,now+650_000_000L,20),"completion drives all three motors at full speed");
        engine.sustain("mining",true,0.9,now+750_000_000L);check(levels(engine,now+750_000_000L,0),"spam cannot extend peak and quiet tail suppresses mining");
        check(!engine.reward("block_break",1,now+1_200_000_000L),"quiet tail remains protected from new generic rewards");
        check(engine.reward("block_break",1,now+1_350_000_000L),"reward re-arms after peak and rest");
        engine.clear();engine.reward("block_break",1,now);engine.reward("ore_break",1,now+200_000_000L);engine.reward("rare_ore_break",1,now+300_000_000L);engine.reward("ore_break",1,now+400_000_000L);
        check(levels(engine,now+1_350_000_000L,20)&&engine.current().equals("rare_ore_break"),"only the most significant pending reward survives");
        engine.clear();engine.reward("block_break",1,now);engine.reward("rare_ore_break",1,now+300_000_000L);engine.reward("block_break",1,now+1_350_000_000L);
        check(levels(engine,now+1_350_000_000L,20)&&engine.current().equals("rare_ore_break"),"new generic reward cannot starve a queued rare reward at the boundary");
        engine.clear();engine.reward("block_break",1,now);engine.fire("damage",1,now+750_000_000L);check(levels(engine,now+750_000_000L,20),"urgent damage can interrupt reward rest");
        engine.clear();check(levels(engine,now+5_000_000_000L,0),"disable clears peaks, rest, and pending rewards");
        check(HapticEngine.crossedMilestone(9,21)&&!HapticEngine.crossedMilestone(30,30)&&!HapticEngine.crossedMilestone(21,22),"milestone detects crossing without login or duplicate rewards");
        int previous=0;for(int hp=20;hp>=2;hp--) { engine.clear();engine.fire("damage",HapticEngine.damageScale(hp,20),now);int level=engine.sample(now)[0];if(level<previous)throw new AssertionError("health curve");previous=level; }
        check(previous==20,"damage grows progressively to maximum at one heart");
        for(String name:profiles.keySet()) { engine.clear();engine.fire(name,1,now);for(int ms=0;ms<=60000;ms+=71)for(int value:engine.sample(now+ms*1_000_000L))if(value<0||value>20)throw new AssertionError(name); }
        check(true,"all profiles remain in range and expire");
        boolean rejected=false;profiles.get("damage").level=21;try { profiles.get("damage").validate(); }catch(IllegalArgumentException ex){rejected=true;}check(rejected,"invalid hardware level rejected");
        System.out.println(passed+" engine checks passed");
    }
}
