import { manageClockBridge } from "../extension/clock-bridge.js";
const host = document.getElementById("fixtures");
const checks = document.getElementById("checks");
const reports = [];
let assertions = 0;
const native = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "currentTime");
function equal(actual, expected, label) { assertions++;if (!Object.is(actual, expected)) throw new Error(label + ": expected " + expected + ", got " + actual); }
function fixture() {
  manageClockBridge("disable");host.replaceChildren();
  const container = document.createElement("div");container.id="fixture-player";container.className="playerFlvContainer mgp_container";
  const legacy=document.createElement("video");legacy.className="player-html5";
  const wrapper=document.createElement("div");wrapper.className="mgp_videoWrapper";
  const active=document.createElement("video");active.className="mgp_videoElement";
  const unrelated=document.createElement("video");unrelated.currentTime=9;
  wrapper.append(active);container.append(legacy,wrapper);host.append(container,unrelated);
  window.VIDEO_SHOW={videoId:container.id};legacy.currentTime=0;active.currentTime=270;
  return {container,legacy,wrapper,active,unrelated};
}
async function test(name, action) {
  try { await action();reports.push({name,pass:true}); }
  catch(error){reports.push({name,pass:false,error:String(error.message)});}
  finally { manageClockBridge("disable"); }
  const item=document.createElement("li");item.className=reports.at(-1).pass?"pass":"fail";item.textContent=(reports.at(-1).pass?"PASS · ":"FAIL · ")+name+(reports.at(-1).error?" — "+reports.at(-1).error:"");checks.append(item);
}
await test("Read-only detection and exact clock forwarding",()=>{
  const f=fixture();equal(manageClockBridge().status,"available","available");equal(Object.hasOwn(f.legacy,"currentTime"),false,"status does not install");
  equal(manageClockBridge("enable").enabled,true,"enabled");equal(f.legacy.currentTime,270,"forwarded");
  f.active.currentTime=273.5;equal(f.legacy.currentTime,273.5,"real clock changes");equal(f.unrelated.currentTime,9,"unrelated clock");
  equal(f.container.querySelector("video"),f.legacy,"tree order unchanged");equal(Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype,"currentTime").get,native.get,"prototype unchanged");
  const getter=Object.getOwnPropertyDescriptor(f.legacy,"currentTime").get;
  equal(getter.call(f.unrelated),9,"borrowed getter preserves receiver");
});
await test("Seek setter stays on original video and Disable restores descriptor",()=>{
  const f=fixture();manageClockBridge("enable");f.legacy.currentTime=12;
  equal(native.get.call(f.legacy),12,"legacy sought");equal(f.active.currentTime,270,"active not sought");equal(f.legacy.currentTime,270,"getter still follows active");
  equal(manageClockBridge("disable").restored,true,"restored");equal(Object.hasOwn(f.legacy,"currentTime"),false,"own property removed");equal(f.legacy.currentTime,12,"legacy native clock visible");
  equal(manageClockBridge("disable").enabled,false,"idempotent disable");
});
await test("Existing accessor is restored exactly",()=>{
  const f=fixture();let own=7;const descriptor={get(){return own},set(v){own=v},enumerable:false,configurable:true};Object.defineProperty(f.legacy,"currentTime",descriptor);
  manageClockBridge("enable");f.legacy.currentTime=18;equal(own,18,"custom setter preserved");equal(f.legacy.currentTime,270,"custom getter temporarily forwarded");
  manageClockBridge("disable");const restored=Object.getOwnPropertyDescriptor(f.legacy,"currentTime");
  equal(restored.get,descriptor.get,"get identity");equal(restored.set,descriptor.set,"set identity");equal(restored.enumerable,false,"enumerability");equal(restored.configurable,true,"configurability");equal(f.legacy.currentTime,18,"restored value");
});
await test("Ambiguous and unrelated layouts stay unchanged",()=>{
  const f=fixture();f.wrapper.append(f.active.cloneNode());equal(manageClockBridge("enable").enabled,false,"two active nodes rejected");equal(Object.hasOwn(f.legacy,"currentTime"),false,"untouched");
  f.wrapper.lastChild.remove();f.container.prepend(f.unrelated);equal(manageClockBridge("enable").enabled,false,"wrong first video rejected");
  f.unrelated.remove();f.container.classList.remove("mgp_container");equal(manageClockBridge("enable").enabled,false,"unrelated container rejected");
});
await test("Locked/custom data clocks stay unchanged",()=>{
  const f=fixture();Object.defineProperty(f.legacy,"currentTime",{value:3,configurable:true});equal(manageClockBridge("enable").status,"custom-clock","data property rejected");equal(f.legacy.currentTime,3,"untouched");
  delete f.legacy.currentTime;Object.defineProperty(f.legacy,"currentTime",{get(){return 4},configurable:false});equal(manageClockBridge("enable").enabled,false,"locked accessor rejected");equal(f.legacy.currentTime,4,"locked value untouched");
});
await test("Active node replacement tears down without automatic transfer",async()=>{
  const f=fixture();manageClockBridge("enable");const replacement=document.createElement("video");replacement.className="mgp_videoElement";replacement.currentTime=80;f.active.replaceWith(replacement);
  equal(f.legacy.currentTime,0,"immediate guarded read");await Promise.resolve();equal(Object.hasOwn(f.legacy,"currentTime"),false,"original restored");equal(manageClockBridge().enabled,false,"not transferred");equal(replacement.currentTime,80,"replacement unchanged");
});
await test("Third-party replacement wins over cleanup",()=>{
  const f=fixture();manageClockBridge("enable");const third=()=>44;Object.defineProperty(f.legacy,"currentTime",{get:third,configurable:true});
  equal(manageClockBridge().status,"superseded","detected replacement");equal(Object.getOwnPropertyDescriptor(f.legacy,"currentTime").get,third,"third-party getter preserved");equal(f.legacy.currentTime,44,"third-party value preserved");
});
await test("Navigation and pagehide restore original clocks",()=>{
  let f=fixture();manageClockBridge("enable");const before=location.href;history.pushState(null,"",location.pathname+"?clock-fixture-navigation");
  equal(f.legacy.currentTime,0,"navigation guarded immediately");equal(Object.hasOwn(f.legacy,"currentTime"),false,"navigation restored");history.replaceState(null,"",before);
  f=fixture();manageClockBridge("enable");window.dispatchEvent(new PageTransitionEvent("pagehide",{persisted:true}));equal(f.legacy.currentTime,0,"pagehide restored");equal(Object.hasOwn(f.legacy,"currentTime"),false,"pagehide property removed");
});
await test("Controller is idempotent and reports no page data",()=>{
  const f=fixture();manageClockBridge("enable");const getter=Object.getOwnPropertyDescriptor(f.legacy,"currentTime").get;equal(manageClockBridge("enable").enabled,true,"second enable");equal(Object.getOwnPropertyDescriptor(f.legacy,"currentTime").get,getter,"same getter");
  equal(Object.keys(manageClockBridge()).sort().join(","),"compatible,enabled,restored,status","fixed result shape");
});
manageClockBridge("disable");host.replaceChildren();delete window.VIDEO_SHOW;
const failed=reports.filter(x=>!x.pass).length;
const summary={passed:reports.length-failed,failed,assertions,cases:reports};
window.fixtureResults=summary;
document.getElementById("result").textContent=`${summary.passed}/${reports.length} cases passed · ${assertions} assertions`;
document.getElementById("result").className=failed?"fail":"pass";
document.documentElement.dataset.complete="true";
