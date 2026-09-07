#!/usr/bin/env bash
# contracts-compile.test.sh -- proves that the stage-1 seam signatures published in
# docs/plans/host-administration-contracts.md compile against the REAL Constructd.Core
# project, with warnings as errors, without adding any production code.
#
# The document marks each C# block that is part of the frozen contract with the HTML
# comment `<!-- stage1-contract -->` on the line before its ```csharp fence. This script
# extracts those blocks into a throwaway class library that references Constructd.Core,
# builds it, and fails when the build fails. A signature that names a type the Core does
# not have, duplicates a parameter name, or uses an impossible default is caught here.
#
#   bash test/contracts-compile.test.sh
#
# Skips cleanly when the .NET SDK is unavailable.
set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOC="${ROOT}/docs/plans/host-administration-contracts.md"
CORE="${ROOT}/service/src/Constructd.Core/Constructd.Core.csproj"

pass=0
fail=0
ok() {
  local name="$1"
  shift
  if "$@"; then pass=$((pass + 1)); printf '  PASS  %s\n' "${name}"
  else fail=$((fail + 1)); printf '  FAIL  %s\n' "${name}"; fi
}

printf '\n=== Host-administration contract signatures compile against Constructd.Core ===\n'

if ! command -v dotnet >/dev/null 2>&1; then
  printf '  SKIP  the .NET SDK is not installed\n\n  0 passed, 0 failed, 1 skipped\n\n'
  exit 0
fi

tmp="$(mktemp -d)"
cleanup() { rm -r "${tmp}"; }
trap cleanup EXIT

# Extract every ```csharp block whose preceding non-empty line is the marker.
extract() {
  awk -v out="${tmp}/Contract.cs" '
    /^<!-- stage1-contract -->[[:space:]]*$/ { armed = 1; next }
    /^```csharp[[:space:]]*$/ && armed { inblock = 1; armed = 0; next }
    /^```[[:space:]]*$/ && inblock { inblock = 0; print "" >> out; next }
    inblock { print >> out; next }
    /^[[:space:]]*$/ { next }
    { armed = 0 }
  ' "${DOC}"
  [[ -s "${tmp}/Contract.cs" ]]
}
ok "the document carries at least one marked contract block" extract

blocks="$(grep -c '^<!-- stage1-contract -->' "${DOC}" || true)"
ok "marked blocks were found (${blocks})" test "${blocks}" -ge 1

cat > "${tmp}/ContractCheck.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <!-- The frozen declarations intentionally shadow the now-implemented Core types. -->
    <NoWarn>CS0436</NoWarn>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${CORE}" />
  </ItemGroup>
</Project>
PROJ

cat > "${tmp}/Program.cs" <<'CS'
using System.Reflection;

var actual = typeof(Constructd.Core.Domain.Vm).Assembly;
var expected = Assembly.GetExecutingAssembly();
static string Shape(Type t) => t.IsByRef ? Shape(t.GetElementType()!) + "&" : t.IsArray ? Shape(t.GetElementType()!) + "[]" : t.IsGenericType
    ? t.GetGenericTypeDefinition().FullName + "[" + string.Join(",", t.GetGenericArguments().Select(Shape)) + "]"
    : t.FullName!;
static string[] Members(Type t) => t.IsEnum
    ? Enum.GetNames(t).Select(n => n + "=" + Convert.ToInt64(Enum.Parse(t, n))).Order().ToArray()
    : t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Where(m => m is PropertyInfo || m is MethodInfo { IsSpecialName: false } || m is ConstructorInfo)
        .Select(m => m switch {
            PropertyInfo p => p.Name + ":" + Shape(p.PropertyType),
            MethodInfo method => method.Name + ":" + Shape(method.ReturnType) + "(" + string.Join(",", method.GetParameters().Select(p => Shape(p.ParameterType))) + ")",
            ConstructorInfo ctor => ".ctor(" + string.Join(",", ctor.GetParameters().Select(p => Shape(p.ParameterType) + (p.HasDefaultValue ? "=" + p.DefaultValue : ""))) + ")",
            _ => throw new Exception()
        }).Order().ToArray();
foreach (var type in expected.GetExportedTypes().Where(t => t.Namespace?.StartsWith("Constructd.Core.") == true)) {
    var real = actual.GetType(type.FullName!);
    if (real is null || !Members(type).SequenceEqual(Members(real))) {
        Console.Error.WriteLine("Contract differs: " + type.FullName);
        return 1;
    }
}
return 0;
CS

build_log="${tmp}/build.log"
build() {
  (cd "${tmp}" && dotnet build ContractCheck.csproj -nologo -v q >"${build_log}" 2>&1)
}
ok "the contract block compiles against Constructd.Core with warnings as errors" build
if [[ "${fail}" -gt 0 ]]; then
  printf '\n--- build output ---\n'
  grep -E "error|warning" "${build_log}" | head -40
fi

parity() { dotnet "${tmp}/bin/Debug/net10.0/ContractCheck.dll"; }
ok "the production Core types match the frozen public signatures" parity

# The block must not smuggle in behaviour: interfaces, records and enums only.
no_bodies() { ! grep -Eq '\)[[:space:]]*\{[[:space:]]*$' "${tmp}/Contract.cs" || ! grep -Eq '^[[:space:]]*(return|await|throw) ' "${tmp}/Contract.cs"; }
ok "the contract block declares no method bodies" no_bodies

printf '\n  %d passed, %d failed\n\n' "${pass}" "${fail}"
[[ "${fail}" -eq 0 ]]
