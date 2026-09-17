"use strict";
// Host configuration form data. Exported to the Companion parity fixture.
module.exports = [
  { key: "capacity", label: "Capacity", rawOnly: [], fields: [
    {"key":"mode","label":"Admission mode","type":"enum","default":"observe","help":"Observe reports capacity; Enforce refuses requests exceeding available capacity.","options":["observe","enforce"]},
    {"key":"ramHeadroomBytes","label":"RAM headroom","type":"bytes","default":null,"help":"Automatic reserves the greater of one eighth of RAM and 1 GiB on Proxmox or 4 GiB on Hyper-V.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991,"nullable":true,"nullLabel":"Automatic"},
    {"key":"storageHeadroomBytes","label":"Storage headroom","type":"bytes","default":21474836480,"help":"Storage kept free for the host.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991},
    {"key":"cpuBudget","label":"Host CPU budget","type":"int","default":null,"help":"Maximum total active vCPUs.","min":0,"max":2147483647,"nullable":true},
    {"key":"maxVcpusPerVm","label":"Maximum vCPUs per VM","type":"int","default":null,"help":"Host ceiling for each VM's CPU count.","min":1,"max":2147483647,"nullable":true},
    {"key":"reconcileSeconds","label":"Reconcile interval","type":"seconds","default":60,"help":"How often capacity is reconciled with the hypervisor.","unit":"seconds","min":30,"max":2147483647},
    {"key":"orphanReservationTimeoutSeconds","label":"Orphan reservation timeout","type":"seconds","default":600,"help":"How long an orphan reservation is retained before cleanup.","unit":"seconds","min":30,"max":2147483647}
  ] },
  { key: "memoryPressure", label: "Memory pressure", rawOnly: [], fields: [
    {"key":"enabled","label":"Enable memory pressure handling","type":"bool","default":true,"help":"Save eligible VMs when measured memory pressure is too high."},
    {"key":"highWaterPercent","label":"High water","type":"percent","default":90,"help":"Start handling pressure above this RAM usage.","unit":"%","min":2,"max":100},
    {"key":"lowWaterPercent","label":"Low water","type":"percent","default":80,"help":"Stop handling pressure below this RAM usage; must be below high water.","unit":"%","min":1,"max":99,"lessThan":"highWaterPercent"},
    {"key":"swapHighWaterPercent","label":"Swap high water","type":"percent","default":50,"help":"Swap usage threshold. Zero ignores swap.","unit":"%","min":0,"max":100},
    {"key":"minSecondsBetweenSaves","label":"Minimum save interval","type":"seconds","default":60,"help":"Minimum time between pressure-triggered saves.","unit":"seconds","min":1,"max":2147483647},
    {"key":"cooldownMinutesAfterSave","label":"Cooldown after save","type":"minutes","default":10,"help":"Wait before handling pressure again after saving.","unit":"minutes","min":0,"max":2147483647}
  ] },
  { key: "userDefaults", label: "User defaults", rawOnly: [], fields: [
    {"key":"maxPrimaries","label":"Maximum primary VMs","type":"int","default":1,"help":"Default primary VM limit for a user.","min":0,"max":2147483647},
    {"key":"allowChildCreation","label":"Allow child creation","type":"bool","default":true,"help":"Allow users to create child VMs."},
    {"key":"maxRetainedChildren","label":"Retained children","type":"int","default":1,"help":"Default maximum retained children per user.","min":0,"max":2147483647},
    {"key":"cpuBudget","label":"CPU budget","type":"int","default":null,"help":"Total vCPUs allowed across the user's VMs.","min":0,"max":2147483647,"nullable":true},
    {"key":"ramBudgetBytes","label":"RAM budget","type":"bytes","default":null,"help":"Total reserved RAM allowed across the user's VMs.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991,"nullable":true},
    {"key":"storageBudgetBytes","label":"Storage budget","type":"bytes","default":null,"help":"Total reserved storage allowed across the user's VMs.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991,"nullable":true},
    {"key":"maxChildLifetimeSeconds","label":"Maximum child lifetime","type":"seconds","default":null,"help":"Maximum finite child lifetime. The minimum is five minutes.","unit":"seconds","min":300,"max":9007199254740991,"nullable":true},
    {"key":"allowNeverLifetime","label":"Allow no expiry","type":"bool","default":true,"help":"Allow children with a never-expiring lifetime."},
    {"key":"allowSharing","label":"Allow sharing","type":"bool","default":true,"help":"Allow children to be shared host-wide."}
  ] },
  { key: "userCaps", label: "Host caps on users", rawOnly: [], fields: [
    {"key":"maxRetainedChildren","label":"Retained children","type":"int","default":null,"help":"Hard ceiling on retained children per user.","min":0,"max":2147483647,"nullable":true},
    {"key":"cpuBudget","label":"CPU budget","type":"int","default":null,"help":"Total vCPUs allowed across the user's VMs. This host cap applies after user defaults and overrides.","min":0,"max":2147483647,"nullable":true},
    {"key":"ramBudgetBytes","label":"RAM budget","type":"bytes","default":null,"help":"Total reserved RAM allowed across the user's VMs. This host cap applies after user defaults and overrides.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991,"nullable":true},
    {"key":"storageBudgetBytes","label":"Storage budget","type":"bytes","default":null,"help":"Total reserved storage allowed across the user's VMs. This host cap applies after user defaults and overrides.","unit":"GiB","scale":1073741824,"min":0,"max":9007199254740991,"nullable":true},
    {"key":"maxChildLifetimeSeconds","label":"Maximum child lifetime","type":"seconds","default":null,"help":"Maximum finite child lifetime. The minimum is five minutes. This host cap applies after user defaults and overrides.","unit":"seconds","min":300,"max":9007199254740991,"nullable":true},
    {"key":"allowNeverLifetime","label":"Allow no expiry","type":"bool","default":null,"help":"Allow children with a never-expiring lifetime. This host cap applies after user defaults and overrides.","nullable":true},
    {"key":"allowSharing","label":"Allow sharing","type":"bool","default":null,"help":"Allow children to be shared host-wide. This host cap applies after user defaults and overrides.","nullable":true}
  ] },
  { key: "lifecycle", label: "Lifecycle", rawOnly: [], fields: [
    {"key":"gracefulShutdownTimeoutSeconds","label":"Graceful shutdown timeout","type":"seconds","default":300,"help":"Time allowed for a guest to shut down gracefully.","unit":"seconds","min":30,"max":2147483647},
    {"key":"leaseTickSeconds","label":"Lease check interval","type":"seconds","default":30,"help":"How often child leases are checked.","unit":"seconds","min":30,"max":2147483647},
    {"key":"leaseRetrySeconds","label":"Lease retry interval","type":"seconds","default":600,"help":"Time before retrying an overdue child's shutdown.","unit":"seconds","min":30,"max":2147483647}
  ] },
  { key: "media", label: "Media", rawOnly: [], fields: [
    {"key":"maxBytes","label":"Maximum media bytes per user","type":"bytes","default":17179869184,"help":"Maximum total media storage per user.","unit":"GiB","scale":1073741824,"min":1,"max":9007199254740991},
    {"key":"maxItemsPerUser","label":"Media items per user","type":"int","default":20,"help":"Maximum media items retained per user.","min":1,"max":2147483647},
    {"key":"uploadChunkBytes","label":"Upload chunk size","type":"bytes","default":8388608,"help":"Upload chunk size, from 1 to 64 MiB.","unit":"MiB","scale":1048576,"min":1048576,"max":67108864},
    {"key":"uploadTtlHours","label":"Upload TTL","type":"int","default":24,"help":"Time allowed to finish an upload.","min":1,"max":2147483647,"unit":"hours"},
    {"key":"acquireTimeoutMinutes","label":"Acquire timeout","type":"minutes","default":180,"help":"Time allowed to acquire media.","unit":"minutes","min":1,"max":2147483647},
    {"key":"allowHttp","label":"Allow HTTP downloads","type":"bool","default":true,"help":"Allow media downloads from HTTP URLs."},
    {"key":"unreferencedTtlHours","label":"Unreferenced media TTL","type":"int","default":null,"help":"Delete unreferenced media after this interval. Disabled retains it.","min":1,"max":2147483647,"unit":"hours","nullable":true,"nullLabel":"Disabled"}
  ] },
  { key: "network", label: "Network", rawOnly: [], fields: [
    {"key":"hostForwardsEnabled","label":"Enable host forwards","type":"bool","default":true,"help":"Disabling refuses new primary host forwards immediately."},
    {"key":"directAddressReporting","label":"Report direct addresses","type":"bool","default":true,"help":"Report guest direct addresses."},
    {"key":"defaultMode","label":"Default VM mode","type":"enum","default":"relayed","help":"Applies to VMs following the host default on their next full stop/start. Mode changes remove existing forwards.","options":["relayed","direct"],"platform":"proxmox","optional":true},
    {"key":"ownerMaySwitchMode","label":"Owners may switch VM mode","type":"bool","default":false,"help":"Allow owners to select relayed or direct mode for their VMs.","platform":"proxmox","optional":true}
  ] },
  { key: "virtualization", label: "Virtualization", rawOnly: [], fields: [
    {"key":"nestedDefault","label":"Enable nesting by default","type":"bool","default":false,"help":"Expose virtualization extensions to new VMs by default.","requires":"nested"},
    {"key":"nestedSelectable","label":"Allow users to select nesting","type":"bool","default":true,"help":"Users may choose nesting unless their user policy overrides this."}
  ] },
  { key: "updates", label: "Updates", rawOnly: [], fields: [
    {"key":"repository","label":"Update repository","type":"string","default":"permissionBRICK/The-Construct","help":"GitHub owner/repository. The host-local installation may pin this value.","pattern":"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"},
    {"key":"channel","label":"Update channel","type":"enum","default":"main","help":"Only the main channel is supported.","options":["main"]},
    {"key":"drainTimeoutMinutes","label":"Drain timeout","type":"minutes","default":60,"help":"Time allowed to drain host jobs before updating.","unit":"minutes","min":1,"max":2147483647},
    {"key":"healthTimeoutSeconds","label":"Health timeout","type":"seconds","default":120,"help":"Time allowed for the updated service to become healthy.","unit":"seconds","min":30,"max":2147483647}
  ] },
  { key: "usage", label: "Usage", rawOnly: [], fields: [
    {"key":"retentionDays","label":"Usage retention","type":"int","default":400,"help":"Daily usage retention. Cleanup runs every 24 hours.","min":1,"max":36500,"unit":"days"}
  ] }
];
