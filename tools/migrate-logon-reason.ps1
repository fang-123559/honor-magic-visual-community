param([switch]$Apply)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$name='Magic Community Desktop'
$powershell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$legacy='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $root 'tools/launch-magic.ps1')+'" -Mode Desktop -NonInteractive'
$arguments=$legacy+' -Logon'
$task=Get-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
# The CIM view can return a bare name that LSA cannot translate (including a
# local account named like the machine). Task Scheduler exports its canonical
# principal SID. Validate that exact identity instead of guessing an account domain.
$taskXml=Export-ScheduledTask -TaskName $name -TaskPath '\'
[xml]$identityXml=$taskXml
$ns=[Xml.XmlNamespaceManager]::new($identityXml.NameTable)
$ns.AddNamespace('t','http://schemas.microsoft.com/windows/2004/02/mit/task')
$principals=$identityXml.SelectNodes('/t:Task/t:Principals/t:Principal',$ns)
$users=$identityXml.SelectNodes('/t:Task/t:Principals/t:Principal/t:UserId',$ns)
if($principals.Count -ne 1 -or $users.Count -ne 1 -or $identityXml.SelectNodes('/t:Task/t:Principals/t:Principal/t:GroupId',$ns).Count -ne 0 -or $users[0].InnerText -notmatch '^S-1-'){throw 'Existing logon task must export one canonical user SID; nothing changed.'}
$taskSid=([Security.Principal.SecurityIdentifier]::new($users[0].InnerText)).Value
$preserved=@{};foreach($section in @('Triggers','Principals','Settings')){$node=$identityXml.SelectSingleNode('/t:Task/t:'+ $section,$ns);$preserved[$section]=if($node){$node.OuterXml}else{''}}
if($task.Description -cne ('Managed Magic Community entry: '+$root) -or @($task.Actions).Count -ne 1 -or $task.Actions[0].Execute -ne $powershell -or $task.Actions[0].Arguments -cnotin @($legacy,$arguments) -or $task.Actions[0].WorkingDirectory -ne $root -or $task.Principal.RunLevel.ToString() -ne 'Limited' -or $task.Principal.LogonType.ToString() -ne 'Interactive' -or $taskSid -ne $sid){throw 'Existing logon task ownership differs; nothing changed.'}
if(-not $Apply){@{task=$name;arguments=$arguments;needsMigration=($task.Actions[0].Arguments -cne $arguments);mode='plan_only';startsDesktop=$false;scope='Existing owned task action only; no task creation, startup enablement or runtime authorization'}|ConvertTo-Json;return}
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1');Assert-MagicReleaseWorkspace -Root $root
if($task.Actions[0].Arguments -ceq $arguments){@{task=$name;status='already_current';startsDesktop=$false}|ConvertTo-Json;return}
$backup=Join-Path $root ('evidence/startup/logon-reason-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($backup)
$taskXml|Set-Content (Join-Path $backup 'before.xml') -Encoding utf8
# Set only the action: preserve the existing triggers, principal, settings and
# enabled/disabled state. Never create, start or reauthorize a desktop session.
$action=New-ScheduledTaskAction -Execute $powershell -Argument $arguments -WorkingDirectory $root
Set-ScheduledTask -TaskName $name -TaskPath '\' -Action $action|Out-Null
$verified=Get-ScheduledTask -TaskName $name -TaskPath '\'
[xml]$afterXml=Export-ScheduledTask -TaskName $name -TaskPath '\'
$afterNs=[Xml.XmlNamespaceManager]::new($afterXml.NameTable);$afterNs.AddNamespace('t',$ns.LookupNamespace('t'))
foreach($section in $preserved.Keys){$node=$afterXml.SelectSingleNode('/t:Task/t:'+ $section,$afterNs);$value=if($node){$node.OuterXml}else{''};if($value -cne $preserved[$section]){throw ('Logon migration changed '+$section+'; backup retained: '+$backup)}}
if($verified.Actions[0].Arguments -cne $arguments -or $verified.Settings.Enabled -ne $task.Settings.Enabled -or $verified.Principal.RunLevel.ToString() -ne 'Limited'){throw ('Logon migration verification failed; backup retained: '+$backup)}
@{task=$name;status='migrated';backup=$backup;startsDesktop=$false;enabled=$verified.Settings.Enabled}|ConvertTo-Json
