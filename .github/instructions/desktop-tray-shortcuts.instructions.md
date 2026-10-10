---
description: "Keep ERP and AgentWatcher tray controls, safe restarts, and release shortcuts consistent"
applyTo: '**/App.xaml.cs,**/MainWindow.xaml.cs,**/Tools/AgentWatcher/**,**/Tools/Set-DesktopShortcut.ps1,**/EXE_OLUSTUR.cmd,**/Scripts/Build-Release.ps1,**/installer/ArsanGazERP.iss'
---

# ERP and AgentWatcher tray and shortcut rules

* Keep a notification-area icon visible for each running ERP and AgentWatcher process, including background AgentWatcher runs.
* Provide tray actions to open the window, restart, and exit. Hide or dispose the icon when its process exits.
* Restart only after the application's owned background work has drained and its single-instance mutex has been released. Preserve whether AgentWatcher was launched in the foreground or with `--background`.
* Update the canonical `Arsan Gaz ERP.lnk` and `Arsan Gaz AgentWatcher.lnk` desktop shortcuts only after both target executables have been published successfully. The AgentWatcher shortcut must launch with `--background`.
* Restrict shortcut changes to these application-managed shortcut names. Do not remove unrelated desktop links.
* Keep installer and release shortcuts aligned with the executable locations produced by their corresponding packaging flow.
* Add or update focused tests for launch-mode preservation and validate both Windows application projects after changing tray or restart behavior.
