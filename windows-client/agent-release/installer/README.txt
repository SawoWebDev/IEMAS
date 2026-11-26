IEMAS Agent installer
=====================

1. Right-click this zip and choose "Extract All...".
2. In the extracted folder, double-click Install-IEMAS-Agent.bat.
   If Windows asks whether to run it, choose Run (or More info > Run anyway).
3. Nothing else needs installing: the Agent carries its own .NET runtime, and no administrator
   rights are needed.
4. The IEMAS Agent opens. Type your work email address and its password, then click Connect.
   That is all: IEMAS checks your mailbox and sets everything else up by itself.
   Your password is used once to check the mailbox and is never kept on this PC.

If your email is Gmail or Microsoft 365 and the password is refused, use an app password.
Updating: run a newer installer the same way. It first looks for an earlier IEMAS Agent on the PC
(running, set to start with Windows, in a shortcut, or in the usual folders), stops it, removes the old
copy and its shortcuts, then installs the new one, so two versions never run side by side. The PC's
registration is kept, so no new approval is needed.
