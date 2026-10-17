; Executes only authored callbacks against the actual prerequisite state machine.
#ifndef RuntimeStatePath
  #define RuntimeStatePath "..\installer\RuntimeState.iss"
#endif

[Setup]
AppName=HSRUnlocker Runtime Workflow Fixture
AppVersion=1.0
DefaultDirName={tmp}\HSRUnlocker-RuntimeFixture-NoApp
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
OutputBaseFilename=RuntimePrerequisites.fixture
Compression=none
CloseApplications=no
RestartApplications=no
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
SetupLogging=yes

[CustomMessages]
RuntimeFailure=%1: %2
RuntimeInstallFailed=Runtime installer failed (%1).
RuntimeNotDetected=Runtime was not detected after installation.
RuntimeRestart=Restart Windows before continuing installation.

[Code]
var
  Installed: array[0..1] of Boolean;
  PostInstalled: array[0..1] of Boolean;
  ExitCodes: array[0..1] of Integer;
  DownloadFailure, StartFailure, CheckFailure: Integer;
  FailureText, Trace, Report: String;
  Checks: Integer;

function RuntimeName(Runtime: Integer): String;
begin
  Result := IntToStr(Runtime);
end;

function RuntimeIsInstalled(Runtime: Integer): Boolean;
begin
  Trace := Trace + 'I' + IntToStr(Runtime) + ';';
  if CheckFailure = Runtime then
    RaiseException('probe failed');
  Result := Installed[Runtime];
end;

procedure RuntimeDownload(Runtime: Integer);
begin
  Trace := Trace + 'D' + IntToStr(Runtime) + ';';
  if DownloadFailure = Runtime then
    RaiseException(FailureText);
end;

function RuntimeExecute(Runtime: Integer; var ExitCode: Integer): Boolean;
begin
  Trace := Trace + 'E' + IntToStr(Runtime) + ';';
  ExitCode := ExitCodes[Runtime];
  Result := StartFailure <> Runtime;
  if Result then
    Installed[Runtime] := PostInstalled[Runtime];
end;

#include RuntimeStatePath

procedure Check(Condition: Boolean; Description: String);
begin
  if not Condition then
    RaiseException('FAIL: ' + Description + ' / ' + Trace);
  Checks := Checks + 1;
  Report := Report + 'PASS: ' + Description + #13#10;
end;

procedure Reset;
begin
  Installed[0] := False;
  Installed[1] := False;
  PostInstalled[0] := True;
  PostInstalled[1] := True;
  ExitCodes[0] := 0;
  ExitCodes[1] := 0;
  DownloadFailure := -1;
  StartFailure := -1;
  CheckFailure := -1;
  FailureText := '';
  Trace := '';
  RuntimeRestartPending := False;
end;

procedure InitializeWizard;
var
  Restart: Boolean;
  Error, ResultFile: String;
  Runtime: Integer;
begin
  Reset;
  Installed[0] := True;
  Installed[1] := True;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error = '') and not Restart and (Trace = 'I0;I1;'),
    'installed runtimes require detection only');
  Check(RuntimeCanLaunchApplication, 'updater launch is allowed only after prerequisite success');

  Reset;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error = '') and not Restart and
    (Trace = 'I0;D0;E0;I0;I1;D1;E1;I1;'),
    'missing runtimes download execute and recheck in order');

  for Runtime := 0 to 1 do begin
    Reset;
    Installed[1 - Runtime] := True;
    Error := EnsureRuntimeDependencies(Restart);
    Check((Error = '') and Installed[Runtime] and
      (Pos('D' + IntToStr(1 - Runtime), Trace) = 0),
      'only missing runtime ' + IntToStr(Runtime) + ' downloads');
  end;

  Reset;
  DownloadFailure := 0;
  FailureText := 'download unavailable';
  Error := EnsureRuntimeDependencies(Restart);
  Check((Pos(FailureText, Error) > 0) and (Trace = 'I0;D0;') and not Restart,
    'download failure aborts before execution');
  Check(not RuntimeCanLaunchApplication, 'download failure keeps updater launch disabled');
  DownloadFailure := -1;
  Trace := '';
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error = '') and (Trace = 'I0;D0;E0;I0;I1;D1;E1;I1;'),
    'retry rechecks state and succeeds after download failure');

  Reset;
  DownloadFailure := 0;
  FailureText := 'cancelled by user';
  Error := EnsureRuntimeDependencies(Restart);
  Check((Pos(FailureText, Error) > 0) and (Trace = 'I0;D0;') and not Restart,
    'cancelled download cannot execute a prerequisite');
  Reset;
  DownloadFailure := 1;
  FailureText := 'SHA256 mismatch';
  Error := EnsureRuntimeDependencies(Restart);
  Check((Pos(FailureText, Error) > 0) and
    (Trace = 'I0;D0;E0;I0;I1;D1;') and not Restart,
    'hash-verification failure cannot execute the downloaded file');

  for Runtime := 0 to 1 do begin
    Reset;
    Installed[1 - Runtime] := True;
    StartFailure := Runtime;
    Error := EnsureRuntimeDependencies(Restart);
    Check((Error <> '') and not Restart and not Installed[Runtime],
      'runtime ' + IntToStr(Runtime) + ' process start failure blocks setup');
    Reset;
    Installed[1 - Runtime] := True;
    ExitCodes[Runtime] := 1603;
    Error := EnsureRuntimeDependencies(Restart);
    Check((Pos('1603', Error) > 0) and not Restart,
      'runtime ' + IntToStr(Runtime) + ' nonzero exit blocks setup');
    Reset;
    Installed[1 - Runtime] := True;
    PostInstalled[Runtime] := False;
    Error := EnsureRuntimeDependencies(Restart);
    Check((Error <> '') and not Restart and
      (Pos('E' + IntToStr(Runtime) + ';I' + IntToStr(Runtime) + ';', Trace) > 0),
      'runtime ' + IntToStr(Runtime) + ' false success fails postcheck');
  end;

  Reset;
  ExitCodes[0] := 3010;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error <> '') and Restart and (Trace = 'I0;D0;E0;I0;'),
    'NET restart request rechecks and stops before remaining installs');
  Check(not RuntimeCanLaunchApplication, 'restart request keeps updater launch disabled');
  Trace := '';
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error <> '') and Restart and (Trace = '') and not RuntimeCanLaunchApplication,
    'same-process retry cannot bypass a requested reboot');
  { A fresh Setup process after reboot starts with no restart latch. }
  RuntimeRestartPending := False;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error = '') and not Restart and (Trace = 'I0;I1;D1;E1;I1;'),
    'retry after restart detects NET and installs only the remaining runtime');
  Reset;
  ExitCodes[0] := 3010;
  PostInstalled[0] := False;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error <> '') and Restart and (Trace = 'I0;D0;E0;I0;'),
    'NET restart request with pending detection never proceeds to payload');
  Reset;
  Installed[0] := True;
  ExitCodes[1] := 3010;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error <> '') and not Restart, 'Windows App Runtime accepts only exit zero');

  Reset;
  CheckFailure := 0;
  Error := EnsureRuntimeDependencies(Restart);
  Check((Error <> '') and (Trace = 'I0;') and not Restart,
    'unexpected detection errors fail closed');

  Check(DotNet8VersionSupported('8.0.0'), 'NET 8 baseline is supported');
  Check(DotNet8VersionSupported('8.0.32'), 'NET 8 servicing runtime is supported');
  Check(not DotNet8VersionSupported('7.0.99'), 'old NET runtime is insufficient');
  Check(not DotNet8VersionSupported('9.0.0'), 'NET 9 alone is insufficient');
  Check(not DotNet8VersionSupported('10.0.0'), 'NET 10 alone is insufficient');
  Check(not DotNet8VersionSupported('8.1.0'), 'NET shared framework must be version 8.0');
  Check(not DotNet8VersionSupported('8.0.-1'), 'negative runtime patch is invalid');
  Check(not DotNet8VersionSupported('8.0.0-preview'), 'preview runtime is insufficient');
  Check(not DotNet8VersionSupported('8.0'), 'incomplete runtime version is invalid');
  Check(not DotNet8VersionSupported('8.0.2147483648'), 'overflowed runtime version is invalid');
  Report := Report + 'CHECKS=' + IntToStr(Checks) + #13#10;
  ResultFile := ExpandConstant('{param:ResultFile}');
  if (ResultFile = '') or not SaveStringToFile(ResultFile, Report, False) then
    RaiseException('Fixture result file could not be written.');
end;
