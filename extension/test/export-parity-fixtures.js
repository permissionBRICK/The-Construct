"use strict";
// One deterministic exporter per area. No timestamps, local home paths, or live I/O.
const fs = require("fs");
const path = require("path");
const scripts = require("../src/guest-scripts");
const forwards = require("../src/forwarder");
const notify = require("../src/notify");
const audio = require("../src/audio");
const ssh = require("../src/ssh");
const t3 = require("../src/t3code");
const usage = require("../src/usage");
const probe = require("../src/probe");
const q = forwards.shQuote;
const directory = path.resolve(__dirname, "../../test/fixtures/companion-parity");

function guestScripts() {
  const rows = [];
  const add = (name, values, output = scripts.render(name, values)) => rows.push({ name, values, output });
  for (const dir of ["/etc/construct/forwards", "/tmp/spool with 'quotes' {{dir}}"] ) {
    add("forwards-capability", { dir: q(dir) }, forwards.buildCapabilityScript({ dir }));
    add("forwards-reconcile", { dir: q(dir), me: q("cc-test"), ttl: "90", lockTtl: "60" }, forwards.buildReconcileScript({ dir, windowId: "cc-test" }));
    add("forwards-watch", { dir: q(dir), heartbeat: "60", fallback: "30" }, forwards.buildWatchScript({ dir }));
    const doc = { v: 1, id: "demo", status: "open", localPort: 5173 };
    add("forwards-ack", { dir: q(dir + "/acks"), id: "demo", b64: q(Buffer.from(JSON.stringify(doc) + "\n").toString("base64")) }, forwards.buildAckScript("demo", doc, { dir }));
    add("forwards-remove", { paths: q(dir + "/acks/demo.json") + " " + q(dir + "/close/demo.json") }, forwards.buildRemoveScript([{ sub: "acks", id: "demo" }, { sub: "close", id: "demo" }], { dir }));
    add("forwards-release", { own: q(dir + "/.owner"), me: q("cc-test") });
  }
  const claim = scripts.render("notify-claim-function");
  add("notify-claim-function", {});
  for (const dir of ["/run/construct/notify", "/tmp/notify with 'quotes'"]) {
    add("notify-claim", { dir: q(dir), claim }, notify.buildClaimScript(dir));
    add("notify-watch", { dir: q(dir), claim, heartbeat: "60", fallback: "3" }, notify.buildWatchScript({ dir }));
  }
  add("probe", {}, probe.REMOTE_PROBE);
  for (const text of ["", "#!/bin/bash\necho 'hello'\n# Unicode: ü\n"]) {
    add("audio-enable", { port: "8767", count: "8", shim: q(Buffer.from(text).toString("base64")), enable: q(Buffer.from(text).toString("base64")) }, audio.buildEnableScript(text, text));
    add("audio-disable", { self: "0", port: "8767", count: "8", disable: q(Buffer.from(text).toString("base64")) }, audio.buildDisableScript(text));
  }
  add("t3-pairing", {}, t3.buildPairingScript());
  for (const name of ["dev", "build-2"]) add("t3-pairing-instance", { instance: name }, t3.buildPairingScript({ name }));
  for (const report of ["daily", "monthly", "total"]) add("usage", { report }, usage.buildUsageScript(report));
  for (const name of scripts.names.filter(name => name.startsWith("construct-"))) add(name, {});
  return rows;
}

function sshArgs() {
  const rows = [];
  // Replace the environment-dependent key prefix with an explicit fixture root.
  const keyPath = "/fixture/home/.ssh/test key";
  for (const hasKey of [false, true]) for (const port of [22, 2222, 0, 65536]) {
    const cfg = { ...ssh.DEFAULTS, keyName: "test key", vmHost: "vm.example", hostAlias: "vm-alias", sshPort: port };
    const stable = args => args.map(arg => arg === ssh.keyPath(cfg) ? keyPath : arg);
    for (const command of ["true", ssh.wrapScriptCommand("echo 'ü'\n")])
      rows.push({ kind: "run", cfg, keyPath: hasKey ? keyPath : null, command, output: stable(ssh.buildSshArgs(cfg, command, hasKey)) });
    for (const opts of [{}, { bindHost: "*" }, { bindHost: "::", connectAddress: "[2001:db8::1]", connectPort: 443 }, { bindHost: "bad", connectAddress: "child.example" }])
      rows.push({ kind: "forward", cfg, keyPath: hasKey ? keyPath : null, localPort: 18801, vmPort: 5173, opts, output: stable(ssh.buildLocalForwardArgs(cfg, 18801, 5173, hasKey, opts)) });
  }
  for (const input of [null, "", "127.0.0.1", "001.2.3.4", "[::1]", "::ffff:1.2.3.004", "fe80::1%eth0", "[fe80::1%3]", "fe80::1%a_b", "fe80::1%a:b", "--option", "a/b", "foo_bar", "-host", "a".repeat(65), "\uFEFFchild.example\uFEFF", "\u0085child.example"]) rows.push({ kind: "address", input, output: ssh.normalizeConnectAddress(input) });
  for (const input of [null, "", "0.0.0.0", "*", "::", "bad", "\uFEFF*\uFEFF", "\u0085*"]) rows.push({ kind: "bind", input, output: ssh.normalizeBindHost(input) });
  for (const input of [0, -1, 22, 2222, 65535, 65536]) rows.push({ kind: "port", input, output: ssh.normalizeSshPort(input) });
  return rows;
}

function hostLabel() {
  // Same IPv6 matrix as forwarder.test.js / construct-expose.test.sh / service ForwardHostTests.
  const valid = ["::", "::1", "fe80::1", "2001:db8::8a2e:370:7334", "1:2:3:4:5:6:7:8", "0:0:0:0:0:0:0:0", "1::", "::2", "0::0", "::ffff:10.0.0.1", "1:2:3:4:5:6:1.2.3.4", "::1.2.3.4", "1::1.2.3.4", "1:2:3:4:5:6:7::", "fe80::0204:61ff:fe9d:f156", "ABCD::1"];
  const invalid = ["::::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1.2.3:4", "....:", ":::", ":1", "1:", "12345::1", "::ffff:999.1.1.1", "::ffff:1.2.3.004", "1:2", "1:2:3:4:5:6:7", "1:::", "1::2:3:4:5:6:7:8", "::1.2.3.4.5", "1:2:3:4:5:6:7:1.2.3.4", "192.0.2.1::", "192.0.2.1::1", "1:192.0.2.1::", "1.2.3.4::1:2"];
  return [null, "", "localhost", " PC name ", "host.example", "127.0.0.1", "a_b-c", "a/b", "[]", "[fe80::1", "fe80::1]", "[[::1]]", "fe80::1%eth0", "a\u0085b", "\u0085host", "host\u0085", "a\uFEFFb", "x".repeat(201), ...valid, ...valid.map(v => `[${v}]`), ...invalid].map(input => ({ input, normalized: forwards.sanitizeHostLabel(input), urlHost: forwards.urlHostFor(input), bindHost: forwards.bindHostFor(input) }));
}
function shellQuoting() {
  return [null, "", "plain", "a'b", "'", "a\nline\r\n", "$(id); `id` \\ \"", "ü🧱", "{{dir}}"].map(input => ({ input, output: q(input) }));
}
function forwardRuntime() {
  const rows = [];
  const add = (kind, input, output) => {
    rows.push({kind, input, output});
    if (kind === "snapshot") rows.push({kind:"panel", input:output, output:require("../src/forwarder-ui").toPanelForwards(output)});
  };
  const ids = ["a", "child-2", "../bad", "", "x".repeat(129)];
  const documents = [null, {}, {v:2,id:"a",vmPort:80}, {v:1,id:"b",vmPort:80}];
  for (const v of [undefined, 1, "1", 2]) for (const port of [0, 1, "80", "1e2", 65535, 65536, true])
    documents.push({v,id:"a",vmPort:port,label:" Hello\n ü ",status:"OPEN",localPort:port,hostLabel:"[::1]",message:" hello\n world "});
  for (const target of [undefined,null,"","client","CLIENT","host"]) documents.push({v:1,id:"a",vmPort:80,target});
  for (const id of ids) for (const doc of documents)
    add("wire", {id,doc}, {request:forwards.parseRequest(id,doc),ack:forwards.parseAck(id,doc),close:forwards.parseClose(id,doc)});
  for (const name of [null,"","agent-vm","dev","build","ü🧱"," work "])
    add("slice", name, forwards.instancePortSlice(name));
  for (const attempt of [-1,0,0.5,1,2,3,5,6,10,99]) add("delay",attempt,forwards.reconnectDelayMs(attempt));
  for (const opts of [{},{prefer:18801,taken:[80,18802]},{base:65530,count:16},{base:19000,count:2},{prefer:80,taken:[80]}])
    add("ports",{vmPort:80,opts},forwards.portCandidates(80,opts));
  const request = {id:"a",vmPort:80,label:"web",target:"client"};
  const dest = {vmName:"child",via:"primary",connectAddress:"10.0.0.2",connectPort:8080,verified:false};
  for (const owner of [false,true]) for (const reopenAcked of [false,true])
    for (const ack of [null,{id:"a",status:"open",localPort:80,hostLabel:"",message:""},{id:"a",status:"open",localPort:81,hostLabel:"pc",message:""},{id:"a",status:"error",localPort:null,message:"failed"}])
      for (const tunnel of [null,...["starting","up","failed"].flatMap(state => [false,true].map(acked => ({id:"a",vmPort:80,localPort:80,state,acked,message:"failed"})))])
        for (const closes of [[],["a"]]) for (const hostLabel of ["","pc","[::1]"]) {
          const input = {owner,reopenAcked,requests:[request],acks:ack?[ack]:[],tunnels:tunnel?[tunnel]:[],closes,hostLabel,mode:reopenAcked?"local":"remote"};
          add("plan",input,forwards.planActions(input)); add("snapshot",input,forwards.toSnapshot(input));
        }
  for (const input of [{requests:[{...request,destination:dest}],acks:[],tunnels:[]}, {requests:[],acks:[{id:"a"}],tunnels:[{id:"a"}]}, {requests:[{id:"../bad"}],closes:["../bad"]}]) add("plan",input,forwards.planActions(input));
  for (const destination of [undefined,null,{},dest,{...dest,connectAddress:""},{...dest,vmName:"../bad"},{...dest,connectAddress:"[::1]"}])
    for (const status of ["queued","open","error","closed"]) {
      const list = [{...request,destination,status,localPort:18800,message:"guest address changed"},{id:"host",vmPort:443,target:"host",url:"https://host/"}];
      const read = forwards.readForwardList(list); add("remote",list,read); add("snapshot",{...read,mode:"remote"},forwards.toSnapshot({...read,mode:"remote"}));
    }
  for (const enabled of [false,true]) for (const online of [false,true]) for (const armed of [null,"dev","other"]) for (const vmState of ["off","saved","running","unknown"])
    { const input={enabled,online,armed,vmState,name:"dev"}; add("lifecycle",input,forwards.planLifecycle(input)); }
  for (const outcome of ["supported","unsupported","unanswered","stood-down","running",""]) for (const current of [false,true])
    add("outcome",{outcome,current},forwards.planStartOutcome({outcome,current}));
  for (const ack of [{},{status:"error",message:"bad\nline"},{status:"open",localPort:80,hostLabel:"[::1]"}]) add("ack",{id:"a",ack},forwards.ackDocument("a",ack));
  return rows;
}
function notifyRuntime() {
  const rows = [];
  const add=(kind,input,output)=>rows.push({kind,input,output});
  for(const dir of ["/run/construct/notify","/tmp/a'b"]) {
    add("claim",dir,notify.buildClaimScript(dir)); add("watch",dir,notify.buildWatchScript({dir}));
  }
  const entries = [ {}, {body:"hello",title:"title",source:"build"}, {body:"<>&\"'",level:"critical",title:" <error> ",source:"a&b"},
    {body:"x".repeat(450),title:"y".repeat(120),source:"z".repeat(80)}, {body:" a\n b\u0085c\uFEFFd ",level:"warn",ts:4} ];
  for(const entry of entries) {
    add("parse",JSON.stringify(entry),notify.parseEntries(JSON.stringify(entry)));
    for(const launchUri of [notify.LAUNCH_URI,"construct://open?instance=dev%202"])
      add("toast",{entry,launchUri},notify.toastXml(entry,launchUri));
  }
  for(const max of [1,5]) {
    const entries=Array.from({length:10},(_,i)=>({ts:i===0?0:1000000*i,body:"message "+i}));
    const opts={now:9000000,ttlMs:3600000,max}; add("select",{entries,opts},notify.selectDeliverable(entries,opts));
  }
  return rows;
}
function audioRuntime() {
  const rows=[];const add=(kind,input,output)=>rows.push({kind,input,output});
  for(const text of ["","#!/bin/bash\necho 'ü'\n"])
    for(const port of [0,8767,65535,-1]) for(const count of [0,8,17]) {
      add("enable",{text,port,count},audio.buildEnableScript(text,text,port,count));
      add("disable",{text,port,count,self:8770},audio.buildDisableScript(text,8770,port,count));
    }
  for(const stdout of ["","CONSTRUCT_PORTS_BUSY=8767,8769,8774","CONSTRUCT_PORTS_BUSY=0,99999,42,42,bad","CONSTRUCT_GATE_PATCHED=10","CONSTRUCT_GATE_PATCHED=1"])
    add("parse",stdout,{busy:audio.parseBusyPorts(stdout),patched:require("../src/repatch").confirmPatched("CONSTRUCT_GATE_PATCHED",stdout)});
  for(const busy of [[],[8767,8768],[8767,8768,8769,8770,8771,8772,8773,8774]]) add("ports",busy,audio.portCandidates(8767,8,busy));
  const keyPath="/fixture/home/.ssh/test key";
  for(const hasKey of [false,true]) for(const port of [22,2222]) {
    const cfg={...ssh.DEFAULTS,keyName:"test key",vmHost:"vm.example",hostAlias:"vm-alias",sshPort:port};
    const stable=args=>args.map(arg=>arg===ssh.keyPath(cfg)?keyPath:arg);
    add("tunnel",{cfg,keyPath:hasKey?keyPath:null},stable(audio.buildTunnelArgs(ssh,cfg,8767,30000,hasKey)));
    add("watchArgs",{cfg,keyPath:hasKey?keyPath:null},stable(notify.buildWatchArgs(ssh,cfg,hasKey,"echo test")));
  }
  const extensionSource=fs.readFileSync(path.join(__dirname,"../extension.js"),"utf8");
  const messageSource=extensionSource.match(/function audioMessage\(status, instanceName\) \{([\s\S]*?)\n\}/)[1];
  const audioMessage=new Function("status","instanceName",messageSource);
  for(const status of [{},{enabled:true,capturing:false},{enabled:true,capturing:true,tunnel:"vm:8767 → host mic (:30000)",gatePatched:true},{enabled:false,capturing:false,gatePatched:false}])
    add("message",{status,instance:"dev"},audioMessage(status,"dev"));
  return rows;
}
function repatchRuntime() {
  const r=require("../src/repatch"); const rows=[];const add=(kind,input,output)=>rows.push({kind,input,output});
  for(const partial of [null,"stock","patched","unknown","absent"]) for(const gate of [null,"stock","patched","unknown","absent"]) {
    const status={partial,gate};
    const stdout=(partial?"CONSTRUCT_PARTIAL_STATUS="+partial+"\n":"")+(gate?"CONSTRUCT_GATE_STATUS="+gate+"\n":"");
    add("parse",stdout,r.parsePatchStatus(stdout));
    for(const streamingOn of [false,true]) for(const micOn of [false,true]) add("repairs",{status,streamingOn,micOn},r.decideRepairs({status,streamingOn,micOn}));
  }
  for(const streamingOn of [false,true]) for(const micOn of [false,true]) for(const micLive of [false,true]) for(const hasHostAudio of [false,true]) {
    const input={streamingOn,micOn,micLive,hasHostAudio};add("startup",input,r.planStartupActions(input));
  }
  add("parse","CONSTRUCT_GATE_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n",r.parsePatchStatus("CONSTRUCT_GATE_STATUS=stock\nCONSTRUCT_GATE_STATUS=patched\n"));
  return rows;
}
function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value && typeof value === "object") return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortKeys(value[key])]));
  return value;
}
function serialize(value) { return JSON.stringify(sortKeys(value), null, 2) + "\n"; }
function exportAll() { return { "notify-runtime": notifyRuntime(), "audio-runtime": audioRuntime(), "repatch-runtime": repatchRuntime(), "forward-runtime": forwardRuntime(), "guest-scripts": guestScripts(), "ssh-args": sshArgs(), "host-label": hostLabel(), "shell-quoting": shellQuoting() }; }
if (require.main === module) {
  fs.mkdirSync(directory, { recursive: true });
  for (const [area, value] of Object.entries(exportAll())) fs.writeFileSync(path.join(directory, area + ".json"), serialize(value));
}
module.exports = { guestScripts, sshArgs, hostLabel, shellQuoting, exportAll, serialize, directory };
