// The production installer and isolated fixture share this workflow.
var
  RuntimeDependenciesReady, RuntimeRestartPending: Boolean;

function DotNet8VersionSupported(Version: String): Boolean;
var
  Patch: String;
  I: Integer;
begin
  Result := False;
  if Copy(Version, 1, 4) <> '8.0.' then
    Exit;
  Patch := Copy(Version, 5, Length(Version));
  if (Length(Patch) = 0) or (Length(Patch) > 10) then
    Exit;
  for I := 1 to Length(Patch) do
    if (Patch[I] < '0') or (Patch[I] > '9') then
      Exit;
  Result := StrToIntDef(Patch, -1) >= 0;
end;

function EnsureRuntimeDependencies(var NeedsRestart: Boolean): String;
var
  Runtime, ExitCode: Integer;
  Detected: Boolean;
begin
  Result := '';
  NeedsRestart := False;
  RuntimeDependenciesReady := False;
  if RuntimeRestartPending then begin
    NeedsRestart := True;
    Result := CustomMessage('RuntimeRestart');
    Exit;
  end;
  Runtime := 0;
  try
    try
      for Runtime := 0 to 1 do begin
        if not RuntimeIsInstalled(Runtime) then begin
          RuntimeDownload(Runtime);
          ExitCode := -1;
          if not RuntimeExecute(Runtime, ExitCode) then begin
            Result := FmtMessage(CustomMessage('RuntimeInstallFailed'), [IntToStr(ExitCode)]);
            Exit;
          end;
          if (Runtime = 0) and (ExitCode = 3010) then begin
            NeedsRestart := True;
            { A reboot request must stop before the other runtime or application files. }
            RuntimeIsInstalled(Runtime);
            Result := CustomMessage('RuntimeRestart');
            Exit;
          end;
          if ExitCode <> 0 then begin
            Result := FmtMessage(CustomMessage('RuntimeInstallFailed'), [IntToStr(ExitCode)]);
            Exit;
          end;
          Detected := RuntimeIsInstalled(Runtime);
          if not Detected then begin
            Result := CustomMessage('RuntimeNotDetected');
            Exit;
          end;
        end;
      end;
      RuntimeDependenciesReady := True;
    except
      Result := FmtMessage(CustomMessage('RuntimeFailure'), [RuntimeName(Runtime), GetExceptionMessage]);
    end;
  finally
    RuntimeRestartPending := NeedsRestart;
  end;
end;

function RuntimeCanLaunchApplication: Boolean;
begin
  Result := RuntimeDependenciesReady and not RuntimeRestartPending;
end;
