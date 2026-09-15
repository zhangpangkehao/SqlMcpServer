#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# dnet.sh - Build-time helper for the agent sandbox.
#
# WHY THIS EXISTS:
#   The sandboxed bash shell used by the AI agent does not expose a complete
#   Windows environment. Specifically PROGRAMFILES / PROGRAMFILES(X86) /
#   APPDATA are missing. NuGet's static ConfigurationDefaults class resolves
#   its machine-wide config directory from those variables, and a missing
#   value makes it throw:
#       The type initializer for 'NuGet.Configuration.ConfigurationDefaults'
#         threw an exception. Value cannot be null. (Parameter 'path1')
#   Re-injecting the variables below fixes `dotnet restore/build/publish`.
#
# USAGE:   ./build/dnet.sh build -c Release
# NOTE:    End users running dotnet from a normal Windows terminal do NOT
#          need this script. It is purely an agent-side workaround.
# ---------------------------------------------------------------------------
set -euo pipefail

export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

exec env \
  'PROGRAMFILES=C:\Program Files' \
  'PROGRAMFILES(X86)=C:\Program Files (x86)' \
  'CommonProgramFiles=C:\Program Files\Common Files' \
  'CommonProgramFiles(x86)=C:\Program Files (x86)\Common Files' \
  'APPDATA=C:\Users\Administrator\AppData\Roaming' \
  'LOCALAPPDATA=C:\Users\Administrator\AppData\Local' \
  'ProgramData=C:\ProgramData' \
  'ALLUSERSPROFILE=C:\ProgramData' \
  'SystemRoot=C:\WINDOWS' \
  'windir=C:\WINDOWS' \
  dotnet "$@"
