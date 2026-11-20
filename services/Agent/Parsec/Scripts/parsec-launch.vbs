' KamenRaider Agent - Parsec desktop script launcher.
' Ported from 0jay-v2 agent/parsec_script.go buildParsecLauncher: scheduling
' powershell.exe directly opens a console window on Windows 11, steals the
' foreground from Parsec and sends every click and key there. Window style 0 keeps
' PowerShell invisible inside the user's desktop. The launcher deletes itself when
' PowerShell exits, and runs a script that never contains credentials.
Option Explicit
Dim shell, fso, exitCode
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
exitCode = shell.Run("__COMMAND__", 0, True)
On Error Resume Next
fso.DeleteFile WScript.ScriptFullName, True
WScript.Quit exitCode
