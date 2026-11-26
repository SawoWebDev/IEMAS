IEMAS Agent release
===================

This folder is mounted into the API container (see docker-compose.yml) and is what the server hands out:

  IemasAgent.exe         the Agent, one self-contained file (carries its own .NET runtime)
  version.txt            the version of that exe, for example 1.5.0 (written by Publish-Agent.ps1)
  installer\             Install-IEMAS-Agent.bat / .ps1 and README.txt, copied into the older zip installer

What the server does with it
  - CMS > People & Devices > Windows Agents > Download installer: the ONE-FILE installer. It is IemasAgent.exe with the
    setup (server address + installer key) attached to its end. On the PC the employee just double-clicks it: it removes
    any earlier IEMAS Agent, installs the new one under %LOCALAPPDATA%\Programs\IEMAS Agent, and starts it.
    "Download as zip instead" gives the older zip (exe + Install-IEMAS-Agent.bat + setup file).
  - Installed Agents check this folder about every 30 minutes and update themselves when version.txt is higher than
    theirs (switch: CMS > Settings > PC setup > "Update the Agent on PCs automatically").

To release a new Agent
  1. Raise <Version> in src\Iemas.Agent\Iemas.Agent.csproj (PCs only update to a HIGHER number).
  2. Run   windows-client\Publish-Agent.ps1   : it builds the exe and writes IemasAgent.exe and version.txt here.
  No API restart is needed; the server reads this folder on each request.

PCs that still have a version from before self-update (1.4.x and older) must run the new installer once. After that they
update by themselves.

Manual setup without the installer: run IemasAgent.exe, Settings > Server URL (for example
https://172.16.2.68:8443, tick "Allow self-signed certificates"), Save, Register, and approve the PC
in the CMS under Windows Agents.
