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
function settingsMapping() {
  const host = require("../src/host");
  const rows = [];
  const raw = { gitUserName: " ü ", gitEmail: "a@b", gitCredentialStore: false, vmMemoryGB: 8, vmDiskGB: 80, vmCpuCount: 4, ubuntuRelease: "24.04", vsCodeServeWeb: true, vsCodeTunnel: false, smbShare: true, micPassthrough: false, claudePartialStreaming: true, opencodeBackgroundWatcher: true, t3code: true, t3codeChannel: "nightly", t3codeLimitResume: true, vmAutoCheckpoints: false };
  for (const input of [null, {}, raw, Object.fromEntries(Object.keys(raw).map(k => [k, null])), Object.fromEntries(Object.keys(raw).map(k => [k, "wrong"])), { gitUserName: [1, null, "x"], vmMemoryGB: {}, t3codeChannel: "future" }]) rows.push({ kind: "to", input, output: host.mapToForm(input), outputJson: JSON.stringify(host.mapToForm(input)) });
  for (const input of [null, {}, host.mapToForm(raw), { password: "DO-NOT-PERSIST", ram: " 1e3 ", disk: "+8", cpu: "-4", gitName: " ", gitCred: "false" }, { ram: ".5", disk: "bad", cpu: "1e999", t3codeChannel: "future" }, { ram: "0x10", disk: "", cpu: null, mic: false }]) rows.push({ kind: "from", input, output: host.mapFromForm(input) });
  for (const previous of [{}, {t3codeLimitResume:true,t3code:true,t3codeChannel:" "}, { t3codeLimitResume: true, t3code: true, t3codeChannel: "stable" }]) for (const next of [{}, { t3codeLimitResume: true, t3code: true, t3codeChannel: "nightly" }, { opencodeBackgroundWatcher: true }]) rows.push({ kind: "patch", previous, next, output: host.patchReprovisionChanges(previous, next) });
  return rows;
}
function instanceIdentity() {
  const m = require("../src/instances");
  const rows = [];
  for (const name of ["agent-vm", "dev", "constructor", "a".repeat(63), "a".repeat(64), "bad-", "Construct-x", "construct-x", "../x"]) {
    for (const raw of [{}, { backend: "hyperv-remote", sshHost: "host.example", sshPort: "2222", vmName: name }, { backend: "HYPERV-LOCAL", vmName: "other", sshHost: "-x", hostAlias: "../bad", keyName: "CON.txt", configBranch: "main" }, { backend: 42, vmName: 42, sshPort: "1e3" }, { backend: "hyperv-remote" }, { backend: "proxmox", sshHost: "::1", keyName: "key.", publicHost: "web.example" }]) {
      const output = m.deriveDefaults(name, raw);
      rows.push({ name, raw, output, valid: m.isValidName(name), problems: m.identityProblems(output, raw), local: m.localIdentityProblems(output), remote: m.remoteIdentityProblems(output, raw), backend: m.backendProblems(raw.backend), fingerprint: m.targetFingerprint(output) });
    }
  }
  return rows;
}
function registryState() {
  const m = require("../src/instances");
  const docs = [null, {}, {version:2}, {version:"1"}, {instances:[]}, {defaultInstance:"constructor",instances:{}}, {defaultInstance:"dev",instances:{dev:{}, "agent-vm":null}}, {instances:{a:{configBranch:"same"},b:{configBranch:"same"}}}, {instances:{bad:{backend:"hyperv-remote"},a:{backend:"proxmox",publicHost:"bad/host"}}}, {instances:{dev:{sshPort:"wrong",service:{auth:"wrong",url:42},scriptsDir:42}}}];
  return docs.map(input => { const text = input === null ? "" : JSON.stringify(input); const p=m.parseRegistry(text); return {text, problems:p.problems, document:m.toFileDocument(p.registry), active:m.resolveActive({registry:p.registry,setting:"missing",workspaceValue:"dev"})}; });
}
function lifecycleInvocations() {
  const m=require("../src/lifecycle"), i=require("../src/instances"); const rows=[];
  const settings={gitName:"-someone 'ü'",gitEmail:"a@b",ram:"12",disk:"100",cpu:"6",ubuntu:"24.04",serveWeb:true,tunnel:false,smb:false,partialStreaming:true,mic:false,opencodeBackgroundWatcher:true,t3code:true,t3codeChannel:"nightly",t3codeLimitResume:false,autoCheckpoints:false};
  const targets=[null,i.deriveDefaults("agent-vm",{}),i.deriveDefaults("dev",{}),i.deriveDefaults("dev",{configBranch:"custom"}),i.deriveDefaults("dev",{backend:"hyperv-remote",sshHost:"host",service:{url:"https://host:7462",auth:"token"}}),i.deriveDefaults("dev",{backend:"hyperv-remote",sshHost:"host"}),i.deriveDefaults("dev",{backend:"unknown"})];
  for(const action of ["reprovision","exportConfig","reinstall","redownload","setCheckpoints","removeInstance","unknown"]) for(const instance of targets) for(const declared of [null,[],["VmName"],["VmHost","HostAlias","SshPort","LocalKeyName"],["VmHost","HostAlias","SshPort","LocalKeyName","VmName","ConfigBranch"],["InstanceName"],["InstanceName","ConfigBranch"],["Backend","ServiceUrl","InstanceName"],["Backend","ServiceUrl","InstanceName","ConfigBranch"]]) for(const legacy of [false,true]) {
    const opts={instance,instanceParams:declared,settings,projects:["api","ui"],backupDir:"C:/Backup dir",backupMode:legacy?"wipe":"save",enabled:!legacy,confirmation:"dev",supportsCheckpoints:!legacy,supportsVmCpuCount:!legacy,supportsT3CodeChannel:!legacy,supportsT3CodeLimitResume:!legacy,supportsOpenCodeBackgroundWatcher:!legacy};
    rows.push({action,opts,output:m.buildInvocation(action,opts),args:m.instanceArgs(action,instance,declared),params:m.paramsForAction(action,instance,declared)||[]});
  }
  return rows;
}
function lifecycleLaunches() {
  const m=require("../src/lifecycle");const rows=[];
  for(const script of ["C:\\Construct dir\\script.ps1","C:/it's here/script.ps1"])for(const elevate of [false,true])for(const keepOpen of [false,true])for(const argSpec of [null,[{flag:"-FromPanel"},{flag:"-GitUserName",value:"-user 'quote' \"double\""},{flag:"-Path",value:"C:\\space here\\"}]]){
    const args=argSpec?m.flattenArgPairs(argSpec):["-FromPanel","-Name","a'b", "", "C:\\space here\\"];
    const opts={elevate,keepOpen,argSpec}; rows.push({script,args,opts,output:m.buildHostLaunch(script,args,opts),child:m.buildChildCommandLine(script,args,opts)});
  }rows.push({kind:"installGit",output:{file:"cmd.exe",spawnArgs:["/c","start","","powershell.exe","-EncodedCommand",Buffer.from("winget install --id Git.Git -e --source winget","utf16le").toString("base64")],command:"winget install --id Git.Git -e --source winget"}});return rows;
}
function vmPower() {
  const m=require("../src/vmpower");const rows=[];
  for(const name of [null,"Agent-VM","dev","a'b", "name with space"])for(const kind of ["state","checkpoints","start"]){const output=kind==="state"?m.buildStateProbeLaunch(name):kind==="checkpoints"?m.buildAutoCheckpointProbeLaunch(name):m.buildElevatedCommandLaunch(m.buildStartCommand(name));rows.push({kind,name,output});}
  for(const input of ["","noise\nVMSTATE=Running\n","VMSTATE=Off","VMSTATE=Saved","VMSTATE=Paused","VMSTATE=Starting","VMSTATE=absent","VMSTATE=unknown","VMAUTOCHK=True","VMAUTOCHK=False","VMAUTOCHK=unsupported","VMAUTOCHK=absent"]) rows.push({kind:"parse",input,state:m.parseVmState(input),checkpoints:m.parseAutoCheckpoints(input)});
  rows.push({kind:"shutdown",output:m.SHUTDOWN_CMD}); return rows;
}
function probeParsing() {
  const m=require("../src/probe");
  const texts=["", "AGENT_NAME\tdev\nAI_TOOLS\tclaude-code,codex,opencode\nV_CLAUDE\tClaude 2.1.3\nMEM_GB\t7.6\nDISK_DEV_BYTES\t85899345920\nVM_CPUS\t6\nDISK_PCT\t41%\nPROJECTS\tapi, ui\nCONSTRUCT_COMMIT\t'ABCDEF1234'\nINSTALLED_AT\t2026-09-11T00:00:00Z", "T3CODE\ttrue\nV_T3\t0.0.12-nightly.1.2\nT3CODE_CHANNEL\tnightly\nT3CODE_PUBLIC_BASE_URL\t'https://[::1]:5178'\nT3_INSTALLATION_MODE\tprebuilt\nT3_BUILD_HASH\tabc\nT3_ACTIVE\tactive\nT3CODE_LIMIT_RESUME\t'TRUE'", "T3CODE\ttrue\nT3CODE_PUBLIC_BASE_URL\thttps://bad/path\nMEM_GB\t-1\nVM_CPUS\twrong\nDISK_PCT\t101%\nOPENCODE_BACKGROUND_WATCHER\tfalse", "T3CODE\ttrue\nT3CODE_PUBLIC_BASE_URL\thttps://host\nT3CODE_HTTPS_PORT\t\nMEM_GB\t0x10\nPROJECTS\ta,a\nUBUNTU\told\nUBUNTU\t24.04"];
  return texts.flatMap(input=>[null,"vm.example","::1"].map(host=>{const map=m.parseProbe(input);return{input,host,map,output:m.toState(map,{host})};}));
}
function usageParsing() {
 const m=require("../src/usage"); const rows=[];
 for(const input of [null,{}, {tools:{claude:{error:0,totals:{totalTokens:123}}}}, {tools:{}},{tools:{claude:{totals:{totalTokens:12345,totalCost:12.34}},codex:{totals:{totalTokens:"2000000",costUSD:2000.1}},opencode:{totals:{totalTokens:42,totalCost:-1}}}},{tools:{claude:{error:"no",totals:{totalTokens:1}},codex:{totals:{totalTokens:0}},opencode:{totals:{totalTokens:"bad"}}}}]) rows.push({kind:"parse",input,output:m.parseUsage(input)});
 for(const input of [0,-1,1,999,1000,1500,999999,1000000,1e9,1234.567,1.005,2.675])rows.push({kind:"format",input,tokens:m.formatTokens(input),cost:m.formatCost(input)});
 return rows;
}
function updatesPlanning() {
 const m=require("../src/updates");const rows=[];
 for(const raw of [{},{installedCommit:false,constructRepo:0,constructRef:false,provisionedCommit:0},{installedCommit:"abcdef1234",provisionedCommit:"old",constructRef:"dev",constructRepo:"owner/repo"}])for(const state of [null,{provisionedCommit:"abcdef1234"},{provisionedCommit:"1234567"}])for(const guest of [null,"ABCDEF1234","bad"]){const markers=m.readMarkers(raw,state);rows.push({kind:"markers",raw,state,guest,markers,stale:m.isProvisionStale(markers,guest),effective:m.effectiveProvisionedCommit(markers,guest),args:m.constructRefreshArgs(markers)});}
 for(const input of [null,{}, {notFound:true},{ahead_by:0},{ahead_by:5},{ahead_by:-1},{ahead_by:"4"}]) rows.push({kind:"compare",input,output:m.constructUpdateFromCompare(input)});
 for(const latest of ["1.2.3","1.3.0","1.2.3-nightly.2.1","1.2.3-beta.2","bad"])for(const installed of ["1.2.3","1.2.2","1.2.3-nightly.1.9","1.2.3-beta.1","bad"])rows.push({kind:"version",latest,installed,newer:m.isNewer(latest,installed),nightly:m.isNewerNightly(latest,installed)});
 return rows;
}
function remoteIdentity() {
 const m=require("../src/remotehost");const rows=[];
 for(const input of [null,{}, {sshHost:"host",sshPort:"1e3"},{sshHost:"host",sshPort:true},{sshHost:"host",sshPort:65536},{sshHost:"host",sshPort:"2222",publicHost:"web"}])rows.push({kind:"endpoint",input,output:m.readEndpoint(input)});
 for(const input of ["host.example","HTTPS://HOST.EXAMPLE/path","https://host:443","http://localhost:8080","https://[::1]:7462","https://[2001:db8::1]:7443"]) rows.push({kind:"url",input,normalized:m.normalizeServiceUrl(input),slug:m.hostSlug(input),pin:m.pinPath(input,{LOCALAPPDATA:"/local"})});
 for(const input of [null,"", "ab".repeat(32),"AB:".repeat(31)+"AB","aa".repeat(31),"zz".repeat(32)]) rows.push({kind:"fingerprint",input,output:m.formatFingerprint(input)});
 return rows;
}
function t3Pure() {
 const m=require("../src/t3code");const rows=[];
 for(const channel of ["stable","nightly","bad"])rows.push({kind:"install",channel,output:m.buildInstallScript(channel)});
 rows.push({kind:"disable",output:m.buildDisableScript()});
 for(const input of [null,"",'{"pairUrl":"https://host/pair?token=example"}','noise {"pairUrl":"https://host"}'])rows.push({kind:"pair",input,output:m.extractPairUrl(input)});
 return rows;
}
function remoteRoutes() {
 const m=require("../src/remotehost"), rows=[]; const value="name /?ü";
 const calls=[
 ["whoami"],["listVms"],["getVm",value],["getState",value],["getEndpoint",value],["power",value,"start"],["createVm",{name:"dev"}],["deleteVm",value,{force:true}],["getJob",value],["health"],["hostCapabilities"],["vmIdentity",value],["vmCapabilities",value],["hostStatus"],["hostCapacity",true],["hostConfig"],["putHostConfig",{mode:"x"}],["isoCatalog"],
 ["users"],["getUser",value],["createUser",{name:"x"}],["updateUser",value,{role:"admin"}],["deleteUser",value],["userAllowance",value],["putUserAllowance",value,{maxVms:2}],["userTokens",value],["issueUserToken",value,null],["revokeUserToken",value,"id /"],
 ["vms",{all:true,empty:"",owner:value}],["sharedVms"],["children",value],["overrides",value],["putOverrides",value,{ram:8}],["deleteOverrides",value],["lifecycle",value,{action:"reprovision"}],["setVmSharing",value,{shared:true}],["renewVmLease",value,{minutes:30}],["rotateVmToken",value,null],["revokeVmToken",value],
 ["media",{type:"iso"}],["mediaItem",value],["mediaReferences",value],["deleteMedia",value],["mediaCleanup"],["jobs",{state:"running"}],["cancelJob",value],["audit",{limit:20}],["forwardsVia",value],["updatesStatus"],["updatesCheck",null],["updatesStage",{releaseTag:"x"}],["updatesApply",{updateId:"x"}],["updatesCancel",{updateId:"x"}],["updatesResolve",{updateId:"x"}]];
 for(const [method,...args] of calls){let captured;const client=m.createClient({baseUrl:"https://host:7462",pin:"ab".repeat(32),auth:{kind:"token",token:"fixture"},fetchImpl:(url,init)=>{captured={url,method:init.method,body:init.body?JSON.parse(init.body):null};return Promise.resolve({status:200,text:"{}"});}});client[method](...args);rows.push({method,args,output:captured});}
 return rows;
}
async function t3Discovery() {
 const m=require("../src/updates"), rows=[];
 const hash="a".repeat(64), other="b".repeat(64);
 const assets=["manifest.json","SHA256SUMS","T3Code-Construct-Setup.exe","t3code-server-linux-x64.tar.gz"].map(name=>({name}));
 const release={draft:false,prerelease:true,tag_name:"t3-1.2.3-nightly.1.2-"+hash,published_at:"2026-09-11",assets};
 for(const channel of ["stable","nightly"])for(const scenario of ["new","same","invalid","missing","page2"]){
  const agent={id:"t3code",version:"1.2.2",channel,installationMode:"prebuilt",buildHash:hash};
  const manifest={channel,version:"1.2.3",buildHash:scenario==="same"?hash:other}; if(scenario==="invalid")manifest.channel="wrong";
  const replies=channel==="nightly"?(scenario==="missing"?[[]]:scenario==="page2"?[Array.from({length:100},()=>({draft:true})),[release],manifest]:[[release],manifest]):[scenario==="missing"?null:manifest];
  const queue=replies.slice(),requests=[];const output=(await m.augmentAgents([agent],{noCache:true,fetchJson:async url=>{requests.push(url);return queue.shift();}}))[0];
  rows.push({channel,scenario,agent,replies,requests,output});
 }return rows;
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
function configSync() {
  const c = require('../src/configsync');
  const rows = [];
  const add = (kind, input, output) => rows.push({kind, input, output});
  for (const name of ['', 'vm', 'VM', 'main', 'master', 'HEAD', 'WORK', 'vm-other', 'CON.txt', 'con-work', 'a..b', 'a.lock', 'a.', 'topic/x', 'topic/.hidden', '-bad']) {
    add('vmBranch', name, c.isValidVmBranch(name)); add('publishBranch', name, c.isValidPublishBranch(name)); add('safeName', name, c.isSafeProfileName(name));
  }
  for (const url of ['', ' https://alice:fixture-secret@host/x ', 'https://alice@host/x', 'ssh://git@host/x', 'git@host:x', '--upload-pack=x', 'https://ggpat_fixture@host/x', 'https://gitgud-project.long.name@host/x']) {
    add('credentials', url, c.urlHasCredentials(url)); add('url', url, c.validateConfigRemoteUrl(url, require('../src/remote').isLikelyGitUrl)); add('redact', url, c.redactGitOutput(url)); add('slug', url, c.remoteSlug(url));
  }
  for (const input of [ {mainFiles:{a:'new',b:'new'},vmFiles:{a:'old',c:'old'}}, {mainFiles:{a:'same'},vmFiles:{a:'same'}}, {mainFiles:{},vmFiles:{}} ]) add('writeBack', input, c.planWriteBack(input));
  for (const root of ['/opt/construct/projects', "/tmp/store 'quoted'"]) {
    add('readScript', root, c.buildReadStoreScript(root));
    const ops = c.planWriteBack({mainFiles:{a:'new',b:'ü\n'},vmFiles:{a:'old',c:'old'}});
    add('writeScript', {root,ops}, c.buildWriteStoreScript(ops,root));
  }
  for (const raw of ['END\n','STORE_ABSENT\nEND\n','a\te30K\nEND\n','a\te30K!!\nEND\n','a\te30\nEND\n','a\t!\nEND\n','a\te30K\n']) add('readResult', raw, c.parseReadStore(raw));
  for (const raw of ['a\tdone\nb\tskipped\nEND\n','a\tdone\n']) add('writeResult', raw, c.parseWriteResult(raw));
  const objects = [{name:'a'}, {name:'a',repos:[{directory:'dir',url:'https://host/x'}],sdks:{node:['22'],empty:[]},mcp:[{name:'m',command:'npx',args:[],env:{},agents:['codex'],enabled:false}],hostPackages:['git'],provisionCommands:['echo ü'],tests:{z:true}}, {name:'b'}, {name:'a',extra:1}, {name:'a',repos:[{url:' '}]}, {name:'a',mcp:[{name:'m',type:'http',url:'https://host',headers:{Z:'z',A:'a'},bearerTokenEnvVar:'TOKEN'}]}, {name:'a',mcp:[{name:'m',type:'bad'}]}, {name:'a',sdks:4,tests:[]}, null];
  for(const obj of objects) { const input={name:'a',raw:JSON.stringify(obj)}; add('canonical',input,c.canonicalizeProfileText(input.name,input.raw)); }
  for (const raw of ['{"name":"a","tests":{"a":1.0,"b":1e2,"c":-0,"d":1.5e300,"e":1e-7,"f":1e-6,"g":1e20}}', JSON.stringify({name:'a',tests:{del:'\x7f',other:'\u2028'}})]) add('canonical',{name:'a',raw},c.canonicalizeProfileText('a',raw));
  for(const raw of ['{"name":"a","tests":{"10":1,"2":2},"sdks":{"10":"v","2":"v"}}',JSON.stringify({name:'a',tests:{controls:'\x0b\x1b'}})]) add('canonical',{name:'a',raw},c.canonicalizeProfileText('a',raw));
  const selections=[{remoteUrl:'https://host/repo',ref:'HEAD',relPath:'projects/a.json',name:'a',content:'{}'}];
  for(const input of [{selected:selections,manifest:{},existingNames:[]},{selected:selections,manifest:{},existingNames:['a','a-2']},{selected:selections,manifest:{a:{remoteUrl:'https://host/repo',pathInRemote:'projects/a.json'}},existingNames:['a']},{selected:[...selections,...selections],manifest:{},existingNames:[]}]) add('import',input,c.planUpstreamImport(input));
  const profiles=[{name:'a',raw:'{"name":"a"}'},{name:'default',raw:'{}'},{name:'../bad',raw:'{}'},{name:'invalid',raw:'{"name":"invalid","repos":0}'},{name:'tracked',raw:'{}'}];
  for(const remoteFiles of [{},{a:'{"name":"a"}'},{A:'{}'},{a:'{"name":"a","repos":[{"url":"x"}]}'}]) {
    const input={profiles,manifest:{tracked:{remoteUrl:'https://user:fixture-secret@host/repo'}},remoteFiles}; const plan=c.planPublish(input); add('publish',input,plan); add('picker',plan,c.buildPublishPickerItems(plan));
  }
  const subset={profiles,manifest:{},remoteFiles:{},selected:['a']}; add('publish',subset,c.planPublish(subset));
  const picker=[{label:'yes',blocked:false},{label:'no',blocked:true},{label:'heading',kind:'separator'}]; add('filterPicker',picker,c.filterPublishSelection(picker));
  for(const wanted of ['https://host/x','https://***@host/x','missing']) { const remotes=[{url:'https://host/x'},{url:'https://user:fixture-secret@host/x'}]; add('resolveRemote',{remotes,wanted},c.resolveRemoteUrl(remotes,wanted)); }
  for(const name of ['vm','vm-other','main']) { const warnings=[]; const branch=c.resolveVmBranch(name,s=>warnings.push(s)); add('resolveBranch',name,{branch,warnings}); }
  const projects=require('../src/projects');
  for(const installRepo of ['permissionBRICK/The-Construct','fork/construct']) for(const installRef of ['main','topic/config']) { const input={configRepoUrl:'https://host/config',names:['a','b'],installRepo,installRef}; add('share',input,projects.buildShareCommand(input)); add('deploy',input,projects.buildDeployPs1(input)); }
  const quotedShare={configRepoUrl:"https://host/team's/config",names:["a'b",'second'],installRepo:'fork/construct',installRef:"topic/it's-config"}; add('share',quotedShare,projects.buildShareCommand(quotedShare)); add('deploy',quotedShare,projects.buildDeployPs1(quotedShare));
  const manifestInput={remoteUrl:'https://host/repo',ref:'main',name:'a',baseCommit:'a'.repeat(40),baseBlobSha:'b'.repeat(40)};
  add('manifest',manifestInput,JSON.stringify(c.publishManifestEntry(manifestInput),null,2)+'\n');
  return rows;
}
function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value && typeof value === "object") return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortKeys(value[key])]));
  return value;
}
function serialize(value) { return JSON.stringify(sortKeys(value), null, 2) + "\n"; }
async function agentUpdates() {
 const m=require("../src/updates"),rows=[];
 for(const id of ["claude-code","codex","opencode","t3code"])for(const channel of id==="t3code"?["stable","nightly"]:[null])for(const scenario of ["new","same","old","invalid","missing"]){
  const nightly=channel==="nightly"; const version=nightly?"1.2.3-nightly.1.2":"1.2.3";
  const latest=scenario==="new"?(nightly?"1.2.3-nightly.2.1":"1.3.0"):scenario==="same"?version:scenario==="old"?(nightly?"1.2.3-nightly.1.1":"1.2.2"):"invalid";
  const agent={id,version,channel,updateAvailable:false},reply=scenario==="missing"?null:{version:latest,tag_name:"v"+latest},requests=[];
  const output=await m.augmentAgents([agent],{noCache:true,fetchJson:async url=>{requests.push(url);return reply;}}); rows.push({agents:[agent],reply,requests,output});
 }
 for(const agent of [null,{}, {id:"unknown",version:"1.2.3"},{id:"codex",version:"—"},{id:"opencode"}]){const requests=[];const output=await m.augmentAgents([agent],{noCache:true,fetchJson:async url=>{requests.push(url);return null;}});rows.push({agents:[agent],reply:null,requests,output});}
 return rows;
}
function agentUpdateScripts() {
 const m=require("../src/updates"),ids=["claude-code","codex","opencode","t3code"];
 return [null,["unknown"],...Array.from({length:16},(_,mask)=>ids.filter((_,i)=>mask&(1<<i)))].map(input=>({input,output:m.buildAgentUpdateScript(input)}));
}
function usageExports() {
 const m=require("../src/usage"),savedAt="2026-09-11T12:00:00.000Z";
 return [JSON.stringify({report:"daily🧱\u2028",tools:{claude:{totals:{totalTokens:1234,totalCost:1.23},note:"\u001f"}}}),"invalid",null].map(input=>({input,savedAt,output:m.buildExportPayload(input,{savedAt})}));
}
function instanceFingerprints() {
 const m=require("../src/instances");return ["1e3",0,"bad",true].map(port=>{const input={...m.deriveDefaults("dev",{}),sshPort:port,service:{url:42,auth:false}};return{input,output:m.targetFingerprint(input)};});
}
function stateJsonBytes() {
 return [{z:"🧱ü\u2028",a:"\u0001\n\t\\\""},{values:[1e-7,1e-6,1e20,1e21,-0,1.0,123.45]}, {"10":"ten","2":"two",version:1,instance:"dev"}].map(value=>{const input=sortKeys(value);return{input,output:JSON.stringify(input,null,2)+"\n"};});
}
function hostAdminIpc() {
 const m=require("../src/hostadmin"), f=require("../src/forwarder-ui"), rows=[], now=Date.parse("2026-09-11T12:00:00Z");
 const add=(kind,input,output)=>rows.push({kind,input,output,now});
 for(const input of [null,{}, {repos:[]},{repos:[{url:"https://example.test/a.git"}]},{repos:[{url:"git@host:repo.git"}]},{repos:[{directory:"a/b",url:"x"}]},{repos:[{directory:"../escape",url:"x"}]},{repos:[{directory:"a#b",url:"x"}]},{repos:[{url:"a"},{url:"b"}]}])add("projectOpenPath",input,require("../src/remote").projectOpenPath(input));
 for(const code of ["cascade-confirmation-required","cascade-scope-changed","cascade-token-expired","other"])for(const status of [409,400]) add("cascadeKind",{code,status},m.cascadeKindOf({code,status}));
 for(const problem of [{},{code:"cascade-scope-changed",children:["child"]},{children:[{name:"one",sharing:"host",state:"Running",diskGb:4,mediaCount:2},{name:"two",sharing:"private"}],cascadeToken:"fake",expiresAt:"2026-09-11T13:00:00Z"}])add("cascade",{primary:"agent-vm",problem},m.cascadeConfirmation({primary:"agent-vm",problem}));
 for(const backend of ["hyperv-local","hyperv-remote"]) for(const health of [{},{apiFeatures:["host-admin"]},{apiFeatures:["host-admin","updates"],status:"maintenance",maintenance:{phase:"draining",retryAfterSeconds:5}}]) for(const whoami of [{name:"alice",role:"admin"},{name:"bob",role:"user"},{known:false},{enabled:false}]) {
  const input={backend,host:"host.example",health,whoami};add("classify",input,m.classifyHost(input));
 }
 for(const status of [0,401,403,404,503])for(const source of ["healthError","whoamiError"]) {
  const input={host:"host.example",health:{apiFeatures:["host-admin"]},[source]:{status,message:"Host request failed.",code:status===503?"maintenance":""}};add("classify",input,m.classifyHost(input));
 }
 for(const input of [{},{mode:"admin",activeTab:"vms"},{mode:"admin",features:{updates:true}},{mode:"user"},{maintenance:{phase:"draining"}},{updatePending:{id:"one"}}])add("poll",input,m.pollIntervalMs(input));
 for(const input of [null,"", "5m", "4m", "24h", "2d", "never", "NEVER", " 12h ", "0m", "-5h", "99999999999999999999d", "five"])
  add("lifetime",input,m.parseLifetime(input));
 for(const features of [[],["host-admin"],["host-admin","children","updates"],["host-admin","children","media","updates","network","console"]]) {
  const input={apiFeatures:features}; add("features",input,m.featureSet(input)); add("tabs",input,m.tabsFor({features:m.featureSet(input)}));
 }
 for(const form of [{},{name:"alice",role:"admin",enabled:"false",maxVms:"3",allowHostForwards:"true"},{name:"",role:"root",enabled:"invalid",maxVms:"-1"},{allowChildCreation:"true",maxRetainedChildren:"4",cpuBudget:"8",ramBudgetGiB:"1.5",storageBudgetGiB:"100",maxChildLifetime:"24h",allowNeverLifetime:"false",allowSharing:"inherit"},{maxChildLifetime:"never",ramBudgetGiB:"bad",maxRetainedChildren:"1.5"}])
  for(const [kind,fn] of [["allowance","parseAllowanceForm"],["overrides","parseOverridesForm"],["userForm","parseUserForm"],["newUser","parseNewUserForm"]]) add(kind,form,m[fn](form));
 for(const timeoutMinutes of [0,1,5.8,99,-5,"bad","45"])for(const action of ["shutdown","save","SAVE","unknown"]) {
  const input={policy:{timeoutMinutes,action},max:30}; add("idleClamp",input,f.clampIdlePolicy(input.policy,input.max)); add("idle",{timeoutMinutes,action,maxTimeoutMinutes:30},f.toPanelIdlePolicy({timeoutMinutes,action,maxTimeoutMinutes:30}));
 }
 const vm={name:"build",kind:"child",parent:"agent-vm",owner:"alice",sharing:"host",state:"running",hardware:{cpus:4,ramMb:2048,diskGb:80},lease:{state:"active",requested:"12h",expiresAt:"2026-09-11T13:00:00Z"},allowedActions:["shutdown","delete","invented"],resourceUsage:{cpuUsagePercent:25.5,memoryDemandBytes:1073741824,memoryAssignedBytes:2147483648,diskFileBytes:123456789,observedAt:"2026-09-11T11:59:55Z"}};
 for(const input of [{},vm,{...vm,state:"paused",deleting:true,lease:{state:"overdue",lastOutcome:"no integration"}},{name:"agent-vm",kind:"primary",children:["build"],guest:{constructCommit:"abcdef0123456789",provisionedAt:"2026-09-11T11:30:00Z"}}]) {
  add("vm",input,m.toVmRow(input,now)); add("childDelete",input,m.childDeleteConfirmation(input)); add("children",[input],m.childRows([input],now));
 }
 add("vms",[vm,{name:"agent-vm"},{name:"orphan",kind:"child",parent:"gone"}],m.toVmRows([vm,{name:"agent-vm"},{name:"orphan",kind:"child",parent:"gone"}],now));
 for(const [kind,fn] of [["overview","toOverview"],["capacity","toCapacityBars"],["media","toMediaRow"],["iso","toIsoCatalogView"],["job","toJobRow"],["audit","toAuditRow"],["config","toConfigView"],["capabilities","toCapabilityRows"],["updates","toUpdateView"],["updateActions","updateActionsFor"],["user","toUserRow"],["allowanceForm","allowanceForm"],["allowanceText","allowanceText"]]) {
  add(kind,{},m[fn]({}));
 }
 for(const state of ["checking","staged","draining","handedOff","applying","interrupted","recoveryFailed","succeeded","failed","cancelled"]) {
  const input={current:{id:"u1",state},installed:{commit:"1234567"},latestKnown:{commit:"abcdef0",compatible:true,reasons:[]}}; add("updates",input,m.toUpdateView(input)); add("updateActions",input,m.updateActionsFor(input));
  const job={id:"j1",kind:"child-delete",state,vmName:"build"};add("job",job,m.toJobRow(job));
 }
 return rows;
}
function desktopWebviews() {
  const themes = require('../src/themes');
  const rows = [];
  for (const theme of [null, '', 'classic', 'terminal', 'native', ' NATIVE ', '../bad'])
    rows.push({kind:'theme', input:theme, output:themes.cssFileFor(theme)});
  const nonce = '0123456789abcdef0123456789abcdef', cspSource = 'https://construct.media';
  for (const cards of [[], themes.THEMES.map(c=>({...c, previewUri:cspSource+'/'+themes.previewFileFor(c.id)})), [{id:'"<&',label:"A's & B",blurb:'<description>',previewUri:'https://construct.media/x?a=1&b=2'}]])
    rows.push({kind:'picker',cards,nonce,output:themes.buildPickerHtml({cspSource,nonce,cards})});
  for (const surface of ['panel','launcher','hostadmin']) for (const theme of ['classic','native','terminal']) {
    const template = fs.readFileSync(path.join(__dirname,'../media',surface+'.html'),'utf8');
    const output = template.replace(/{{cspSource}}/g,cspSource).replace(/{{nonce}}/g,nonce)
      .replace(/{{styleUri}}/g,cspSource+'/panel.css').replace(/{{themeUri}}/g,cspSource+'/'+themes.cssFileFor(theme))
      .replace(/{{scriptUri}}/g,cspSource+'/'+surface+'.js').replace(/{{adminStyleUri}}/g,cspSource+'/hostadmin.css');
    rows.push({kind:'document',surface,template,theme,nonce,output});
  }
  return rows;
}
async function exportAll() { return { "desktop-webviews": desktopWebviews(), "hostadmin-ipc": hostAdminIpc(), "config-sync": configSync(), "notify-runtime": notifyRuntime(), "audio-runtime": audioRuntime(), "repatch-runtime": repatchRuntime(), "forward-runtime": forwardRuntime(), "guest-scripts": guestScripts(), "ssh-args": sshArgs(), "host-label": hostLabel(), "shell-quoting": shellQuoting(), "settings-mapping": settingsMapping(), "instance-identity": instanceIdentity(), "registry-state": registryState(), "lifecycle-invocations": lifecycleInvocations(), "lifecycle-launches": lifecycleLaunches(), "vm-power": vmPower(), "probe-parsing": probeParsing(), "usage-parsing": usageParsing(), "updates-planning": updatesPlanning(), "remote-identity": remoteIdentity(), "t3-pure": t3Pure(), "remote-routes": remoteRoutes(), "t3-discovery": await t3Discovery(), "state-json-bytes": stateJsonBytes(), "agent-updates": await agentUpdates(), "agent-update-scripts": agentUpdateScripts(), "usage-exports": usageExports(), "instance-fingerprints": instanceFingerprints() }; }
if (require.main === module) (async () => {
  fs.mkdirSync(directory, { recursive: true });
  for (const [area, value] of Object.entries(await exportAll())) fs.writeFileSync(path.join(directory, area + ".json"), serialize(value));
})().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { guestScripts, sshArgs, hostLabel, shellQuoting, exportAll, serialize, directory };
