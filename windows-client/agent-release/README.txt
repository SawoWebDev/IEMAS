IEMAS Agent
===========

Needs once on the PC: .NET 8 Desktop Runtime (x64)
  winget install Microsoft.DotNet.DesktopRuntime.8
  or download ".NET Desktop Runtime 8.0.x - Windows x64" from https://dotnet.microsoft.com/download/dotnet/8.0

Install:
  1. Create the folder C:\Program Files\IEMAS Agent and copy IemasAgent.exe into it.
  2. Start IemasAgent.exe.
  3. Settings -> Server URL: the address your administrator gives you, for example
       https://172.16.2.68:8443   (production server, tick "Allow self-signed certificates")
     Save.
  4. Register with your company email address. An administrator approves the PC in the
     IEMAS CMS (People & Devices -> Windows Agents). The app then connects by itself.
  5. Leave "Start with Windows" ticked.

Update:
  1. Right-click the IEMAS tray icon -> Exit
  2. Replace IemasAgent.exe with the new one
  3. Start IemasAgent.exe again (registration is kept)
