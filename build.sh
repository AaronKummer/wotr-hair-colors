#!/usr/bin/env bash
set -e
CSC="C:/Program Files/dotnet/sdk/9.0.304/Roslyn/bincore/csc.dll"
GAME="C:/Program Files (x86)/Steam/steamapps/common/Pathfinder Second Adventure"
MG="$GAME/Wrath_Data/Managed"
UMM="$MG/UnityModManager"
OUT="$(dirname "$0")/bin/VVHairColors.dll"
mkdir -p "$(dirname "$OUT")"

REFS=(
  "$MG/mscorlib.dll" "$MG/System.dll" "$MG/System.Core.dll" "$MG/netstandard.dll"
  "$UMM/UnityModManager.dll" "$UMM/0Harmony.dll"
  "$MG/Assembly-CSharp.dll" "$MG/Assembly-CSharp-firstpass.dll"
  "$MG/UnityEngine.dll" "$MG/UnityEngine.CoreModule.dll" "$MG/UnityEngine.IMGUIModule.dll"
  "$MG/Owlcat.Runtime.Core.dll" "$MG/Owlcat.Runtime.Validation.dll"
  "$MG/Newtonsoft.Json.dll"
)
ARGS=(-nostdlib -noconfig -nologo -target:library -langversion:latest -unsafe -debug:portable -out:"$OUT")
for r in "${REFS[@]}"; do [ -f "$r" ] && ARGS+=(-reference:"$r") || echo "WARN missing ref: $r"; done
for f in "$(dirname "$0")"/*.cs; do ARGS+=("$f"); done

echo "Compiling -> $OUT"
dotnet "$CSC" "${ARGS[@]}"
echo "OK: $(ls -la "$OUT" | awk '{print $5}') bytes"
