# Evaluates the statements of Provision-AgentVM.ps1 that build provision.sh's env prefix, verbatim from
# the script, for one scenario, and prints the prefix and the deferred project phase command.
# Driven by ProvisionEnvironmentTests.
param([string]$Script, [string]$ScenarioJson, [string]$Bound = "1")
$ErrorActionPreference = 'Stop'
$s = $ScenarioJson | ConvertFrom-Json
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$null, [ref]$null)
$fns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('ConvertTo-PosixSingleQuoted','Get-ExternalEnvSuffix','Get-ServiceEnvSuffix') }, $true)
foreach ($f in $fns) { . ([scriptblock]::Create($f.Extent.Text)) }
# The script's own inputs, named as in Provision-AgentVM.ps1.
foreach ($p in $s.vars.PSObject.Properties) { Set-Variable -Name $p.Name -Value $p.Value }
$script:UseRootKey = [bool]$s.useRootKey
$deferProjectCommands = [bool]$s.defer
$explicitEndpoint = ($Bound -eq "1"); $savedExtHost = ''; $savedExtPort = ''
# The top-level statements that build the env prefix, verbatim from the script.
$wanted = '^(\$externalEnv = |\$guestInstanceName = ""|if \(\$ServiceUrl\) \{ \$guestInstanceName|\$serviceEnv = Get-ServiceEnvSuffix|\$envPrefix = "env AI_TOOLS|if \(\$deferProjectCommands\) \{ \$envPrefix|\$rebootAfterArg = |\$envPrefix \+= " REBOOT)'
$found = 0
function Test-InFunction($n) { for ($p = $n.Parent; $p; $p = $p.Parent) { if ($p -is [System.Management.Automation.Language.FunctionDefinitionAst]) { return $true } }; $false }
$statements = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StatementAst] -and $n.Extent.Text -match $wanted -and -not (Test-InFunction $n) }, $true) | Sort-Object { $_.Extent.StartOffset }
foreach ($st in $statements) { . ([scriptblock]::Create($st.Extent.Text)); $found++ }
if ($found -ne 8) { throw "expected 8 statements, found $found" }
$deferred = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] -and $n.Value -like 'env PROVISION_PHASE=project-commands*' }, $true) | Select-Object -First 1
$phase = $ExecutionContext.InvokeCommand.ExpandString($deferred.Value)
"MAIN:$envPrefix"
"PHASE:$phase"
