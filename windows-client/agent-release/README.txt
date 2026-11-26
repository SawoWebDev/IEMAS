IEMAS Agent release
===================

This folder is mounted into the API container (see docker-compose.yml). The CMS zips it into the
one-click installer: People & Devices > Windows Agents > Download installer.

  IemasAgent.exe         the Agent, one self-contained file (carries its own .NET runtime)
  installer\              Install-IEMAS-Agent.bat / .ps1 and README.txt, copied into every zip

The CMS adds iemas-agent-setup.json (server address + a one-time installer key) to each zip.

Rebuild IemasAgent.exe after changing the Agent:
  dotnet publish windows-client/src/Iemas.Agent/Iemas.Agent.csproj -c Release -r win-x64 --self-contained true
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o <temp folder>
  then copy IemasAgent.exe here.

Manual setup without the installer: run IemasAgent.exe, Settings > Server URL (for example
https://172.16.2.68:8443, tick "Allow self-signed certificates"), Save, Register, and approve the PC
in the CMS under Windows Agents.
