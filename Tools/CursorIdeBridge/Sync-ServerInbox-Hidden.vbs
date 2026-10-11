' Runs Sync-ServerInbox.ps1 without a console window (used by the "ArsanGazERP ServerInboxSync" scheduled task).
Set shell = CreateObject("WScript.Shell")
scriptDir = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & scriptDir & "\Sync-ServerInbox.ps1"""
WScript.Quit shell.Run(command, 0, True)
