IEMAS Agent installer
=====================

1. Right-click this zip and choose "Extract All...".
2. In the extracted folder, double-click Install-IEMAS-Agent.bat.
   If Windows asks whether to run it, choose Run (or More info > Run anyway).
3. If the PC has no .NET 8 Desktop Runtime yet, the installer adds it. Windows may ask
   you to allow the change: click Yes.
4. The IEMAS Agent starts. What happens next depends on the installer:
   - Made for one person: the Agent connects by itself. Nothing to type.
   - Made for everyone: type your work email address and your email password in the
     Agent, click Test connection, then Connect. IEMAS checks the password with the
     company mail server, connects this PC and sends a confirmation email to your inbox.
   - If the Agent says the PC must wait, an administrator will approve it.

Passwords are sent to the IEMAS server only over https. The Agent asks before it
would send a password over plain http. After 5 wrong passwords for one address, the
Agent waits 15 minutes before it accepts another try.

Keep this zip private. Anyone who runs it can connect a PC to IEMAS until an
administrator withdraws it. Do not forward it.
Updating: run a newer installer the same way; the registration is kept.
