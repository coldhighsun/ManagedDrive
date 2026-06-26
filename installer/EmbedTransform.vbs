' EmbedTransform.vbs — embeds a language transform (.mst) into an MSI as a named substorage.
' Usage: cscript EmbedTransform.vbs <target.msi> <transform.mst> <LCID>
'   e.g. cscript EmbedTransform.vbs setup.msi zh-CN.mst 2052

If WScript.Arguments.Count < 3 Then
    WScript.Echo "Usage: cscript EmbedTransform.vbs <msi> <mst> <lcid>"
    WScript.Quit 1
End If

Dim msiPath : msiPath = WScript.Arguments(0)
Dim mstPath : mstPath = WScript.Arguments(1)
Dim lcid    : lcid    = WScript.Arguments(2)

Const msiOpenDatabaseModeTransact = 1
Const MSIMODIFY_INSERT = 3

Dim installer : Set installer = CreateObject("WindowsInstaller.Installer")
Dim db        : Set db        = installer.OpenDatabase(msiPath, msiOpenDatabaseModeTransact)

' 1. Add the .mst file as a substorage named after the LCID
Dim view : Set view = db.OpenView("SELECT `Name`,`Data` FROM `_Storages`")
view.Execute

Dim rec : Set rec = installer.CreateRecord(2)
rec.StringData(1) = lcid
rec.SetStream 2, mstPath
view.Modify MSIMODIFY_INSERT, rec

' 2. Update SummaryInformation Template to list both languages
Dim si : Set si = db.SummaryInformation(1)
Dim tpl : tpl = si.Property(7)          ' Property 7 = Template (Platform;LangID)
WScript.Echo "Current Template: [" & tpl & "]"
' Append ,LCID to the language portion  e.g. "x64;1033" -> "x64;1033,2052"
Dim parts : parts = Split(tpl, ";")
If UBound(parts) >= 1 Then
    si.Property(7) = parts(0) & ";" & parts(1) & "," & lcid
Else
    si.Property(7) = tpl & "," & lcid
End If
WScript.Echo "New     Template: [" & si.Property(7) & "]"
si.Persist

db.Commit
WScript.Echo "Embedded transform " & lcid & " into " & msiPath
