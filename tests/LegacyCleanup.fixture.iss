; Only the production policy runs; all registry/path/attribute callbacks are synthetic.
#ifndef LegacyCleanupStatePath
  #define LegacyCleanupStatePath "..\installer\LegacyCleanupState.iss"
#endif

[Setup]
AppName=HSRUnlocker Legacy Cleanup Fixture
AppVersion=1.0
DefaultDirName={tmp}\HSRUnlocker-LegacyFixture-NoApp
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
OutputBaseFilename=LegacyCleanup.fixture
Compression=none
CloseApplications=no
RestartApplications=no
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
SetupLogging=yes

[Code]
var
  FixturePaths: TArrayOfString;
  FixtureAttributes: array of LongWord;
  FixtureApp, Location64, Location32, Name64, Name32, FaultPath: String;
  Registry64, Registry32, RegistryFailure: Boolean;
  RegistryReads, AttributeReads, Checks, Failures: Integer;
  Report: String;

function LegacyCleanupAppDirectory: String;
begin
  Result := FixtureApp;
end;

function LegacyCleanupReadUninstallData(Is64Bit: Boolean;
  var Location, DisplayName: String): Boolean;
begin
  RegistryReads := RegistryReads + 1;
  if RegistryFailure then
    RaiseException('synthetic registry failure');
  if Is64Bit then begin
    Location := Location64;
    DisplayName := Name64;
    Result := Registry64;
  end else begin
    Location := Location32;
    DisplayName := Name32;
    Result := Registry32;
  end;
end;

function LegacyCleanupGetFileAttributes(Path: String): LongWord;
var
  I: Integer;
begin
  AttributeReads := AttributeReads + 1;
  if (FaultPath <> '') and (CompareText(FaultPath, Path) = 0) then
    RaiseException('synthetic attribute failure');
  Result := $FFFFFFFF;
  for I := 0 to GetArrayLength(FixturePaths) - 1 do
    if CompareText(FixturePaths[I], Path) = 0 then begin
      Result := FixtureAttributes[I];
      Exit;
    end;
end;

#ifdef LegacyRedStub
var
  LegacyCleanupOwnershipChecked, LegacyCleanupOwnershipValid: Boolean;
  LegacyCleanupOwnedDirectory: String;

function CanDeleteLegacyRuntimeFile(RelativePath: String): Boolean;
begin
  Result := False;
end;
#else
  #include LegacyCleanupStatePath
#endif

procedure Check(Condition: Boolean; Description: String);
begin
  Checks := Checks + 1;
  if Condition then
    Report := Report + 'PASS: ' + Description + #13#10
  else begin
    Failures := Failures + 1;
    Report := Report + 'FAIL: ' + Description + #13#10;
  end;
end;

procedure PutAttributes(Path: String; Attributes: LongWord);
var
  I: Integer;
begin
  for I := 0 to GetArrayLength(FixturePaths) - 1 do
    if CompareText(FixturePaths[I], Path) = 0 then begin
      FixtureAttributes[I] := Attributes;
      Exit;
    end;
  I := GetArrayLength(FixturePaths);
  SetArrayLength(FixturePaths, I + 1);
  SetArrayLength(FixtureAttributes, I + 1);
  FixturePaths[I] := Path;
  FixtureAttributes[I] := Attributes;
end;

procedure Reset;
begin
  FixtureApp := 'C:\SyntheticOwned\App';
  Location64 := 'c:\syntheticowned\app\';
  Location32 := FixtureApp;
  Name64 := 'HSRUnlocker';
  Name32 := 'HSRUnlocker';
  Registry64 := True;
  Registry32 := False;
  RegistryFailure := False;
  FaultPath := '';
  RegistryReads := 0;
  AttributeReads := 0;
  LegacyCleanupOwnershipChecked := False;
  LegacyCleanupOwnershipValid := False;
  LegacyCleanupOwnedDirectory := '';
  SetArrayLength(FixturePaths, 0);
  SetArrayLength(FixtureAttributes, 0);
  PutAttributes('C:\', $10);
  PutAttributes('C:\SyntheticOwned', $10);
  PutAttributes(FixtureApp, $10);
  PutAttributes(FixtureApp + '\HSRUnlocker.exe', $80);
  PutAttributes(FixtureApp + '\coreclr.dll', $80);
  PutAttributes(FixtureApp + '\legacy-native.dll', $80);
  PutAttributes(FixtureApp + '\locales', $10);
  PutAttributes(FixtureApp + '\locales\en-US', $10);
  PutAttributes(FixtureApp + '\locales\en-US\legacy.resources.dll', $80);
  PutAttributes(FixtureApp + '\user.cfg', $80);
  PutAttributes(FixtureApp + '\unrelated.dll', $80);
  PutAttributes('C:\SharedRuntime\coreclr.dll', $80);
end;

procedure InvalidRelative(Path: String);
begin
  Reset;
  Check(not CanDeleteLegacyRuntimeFile(Path), 'reject unsafe relative path "' + Path + '"');
  Check((AttributeReads = 0) and (RegistryReads = 0),
    'unsafe relative path never probes filesystem or registry');
end;

procedure InitializeWizard;
var
  I, ReadsBefore: Integer;
  Targets: TArrayOfString;
  Candidate: String;
begin
  Reset;
  Check(CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'matching x64 uninstall metadata recognizes owned prior self-contained app');
  Reset;
  Registry64 := False;
  Registry32 := True;
  Check(CanDeleteLegacyRuntimeFile('legacy-native.dll') and (RegistryReads = 2),
    'matching 32-bit uninstall view also recognizes the same AppId');
  Reset;
  Registry64 := False;
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'an unregistered directory is preserved');
  Reset;
  Location64 := 'C:\SyntheticOwned\App-Other';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'prefix-similar unrelated InstallLocation is preserved');
  Reset;
  Name64 := 'Unrelated Application';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'unrelated registered display name is preserved');
  Reset;
  Location64 := '';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'missing InstallLocation fails closed');
  Reset;
  Name64 := '';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'missing registered DisplayName fails closed');
  Reset;
  PutAttributes(FixtureApp + '\HSRUnlocker.exe', $FFFFFFFF);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'missing application executable prevents legacy ownership');
  Reset;
  PutAttributes(FixtureApp + '\coreclr.dll', $FFFFFFFF);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'missing coreclr prevents ownership of framework-dependent installs');
  Reset;
  PutAttributes(FixtureApp + '\HSRUnlocker.exe', $10);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'a directory named HSRUnlocker.exe cannot establish ownership');
  Reset;
  PutAttributes(FixtureApp + '\coreclr.dll', $10);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'),
    'a directory named coreclr.dll cannot establish ownership');

  Reset;
  Check(CanDeleteLegacyRuntimeFile('coreclr.dll'),
    'ownership is established before the first legacy deletion');
  ReadsBefore := RegistryReads;
  PutAttributes(FixtureApp + '\coreclr.dll', $FFFFFFFF);
  Check(CanDeleteLegacyRuntimeFile('locales/en-US/legacy.resources.dll') and
    (RegistryReads = ReadsBefore),
    'cached ownership allows subsequent exact entries after coreclr is removed');
  FixtureApp := 'C:\SharedRuntime';
  Check(not CanDeleteLegacyRuntimeFile('coreclr.dll'),
    'cached ownership never authorizes a changed application directory');

  Reset;
  PutAttributes('C:\', $410);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse drive root fails closed');
  Reset;
  PutAttributes('C:\SyntheticOwned', $410);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse app ancestor fails closed');
  Reset;
  PutAttributes(FixtureApp, $410);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse app directory fails closed');
  Reset;
  PutAttributes(FixtureApp + '\HSRUnlocker.exe', $480);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse ownership executable fails closed');
  Reset;
  PutAttributes(FixtureApp + '\coreclr.dll', $480);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse ownership runtime fails closed');
  Reset;
  PutAttributes(FixtureApp + '\legacy-native.dll', $480);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'reparse target file fails closed');
  Reset;
  PutAttributes(FixtureApp + '\locales', $410);
  Check(not CanDeleteLegacyRuntimeFile('locales/en-US/legacy.resources.dll'),
    'reparse target ancestor fails closed');
  Reset;
  PutAttributes(FixtureApp + '\locales\en-US', $410);
  Check(not CanDeleteLegacyRuntimeFile('locales/en-US/legacy.resources.dll'),
    'nested reparse target ancestor fails closed');
  Reset;
  Check(CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'safe target authorizes before a later reparse change');
  PutAttributes(FixtureApp + '\locales', $410);
  Check(not CanDeleteLegacyRuntimeFile('locales/en-US/legacy.resources.dll'),
    'cached ownership never bypasses fresh target ancestor checks');
  Reset;
  PutAttributes('C:\SyntheticOwned', $FFFFFFFF);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'missing app ancestor fails closed');
  Reset;
  PutAttributes(FixtureApp + '\locales', $80);
  Check(not CanDeleteLegacyRuntimeFile('locales/en-US/legacy.resources.dll'),
    'a non-directory target ancestor fails closed');
  Reset;
  PutAttributes(FixtureApp + '\legacy-native.dll', $10);
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'directory target is never recursively cleaned');
  Reset;
  Check(not CanDeleteLegacyRuntimeFile('missing.dll'), 'missing target fails closed');
  Reset;
  FaultPath := FixtureApp;
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'attribute callback errors fail closed');
  Reset;
  RegistryFailure := True;
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'uninstall metadata errors fail closed');
  Reset;
  FixtureApp := 'relative\app';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'relative application root fails closed');
  Reset;
  FixtureApp := '\\server\share\app';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'unsupported UNC application root fails closed');
  Reset;
  Location64 := 'C:\SyntheticOwned\App\..\App';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'traversal-bearing uninstall location fails closed');
  Reset;
  Location64 := '"C:\SyntheticOwned\App"';
  Check(not CanDeleteLegacyRuntimeFile('legacy-native.dll'), 'quoted malformed uninstall location fails closed');

  InvalidRelative('');
  InvalidRelative('../user.cfg');
  InvalidRelative('locales/../user.cfg');
  InvalidRelative('./legacy-native.dll');
  InvalidRelative('locales//legacy-native.dll');
  InvalidRelative('/legacy-native.dll');
  InvalidRelative('C:/legacy-native.dll');
  InvalidRelative('locales\legacy-native.dll');
  InvalidRelative('legacy-native.dll:stream');
  InvalidRelative('*.dll');
  InvalidRelative('legacy-?.dll');
  InvalidRelative('legacy-native.dll/');
  InvalidRelative('legacy-native.dll.');
  InvalidRelative('legacy-native.dll ');
  InvalidRelative('locales/ /legacy-native.dll');
  InvalidRelative('legacy' + #1 + '.dll');
  InvalidRelative('locales/"legacy".dll');
  InvalidRelative('locales/<legacy>.dll');
  InvalidRelative('locales/legacy|name.dll');
  InvalidRelative('CON.dll');

  { Model only exact authored allowlist entries; deletion mutates synthetic attributes. }
  Reset;
  SetArrayLength(Targets, 3);
  Targets[0] := 'coreclr.dll';
  Targets[1] := 'legacy-native.dll';
  Targets[2] := 'locales/en-US/legacy.resources.dll';
  for I := 0 to GetArrayLength(Targets) - 1 do begin
    Candidate := Targets[I];
    if CanDeleteLegacyRuntimeFile(Candidate) then begin
      StringChangeEx(Candidate, '/', '\', True);
      PutAttributes(FixtureApp + '\' + Candidate, $FFFFFFFF);
    end;
  end;
  Check(LegacyCleanupGetFileAttributes(FixtureApp + '\legacy-native.dll') = $FFFFFFFF,
    'exact planned native runtime file is removed');
  Check(LegacyCleanupGetFileAttributes(FixtureApp + '\locales\en-US\legacy.resources.dll') = $FFFFFFFF,
    'exact planned nested runtime resource is removed');
  Check(LegacyCleanupGetFileAttributes(FixtureApp + '\user.cfg') = $80,
    'configuration outside the exact deletion plan is preserved');
  Check(LegacyCleanupGetFileAttributes(FixtureApp + '\unrelated.dll') = $80,
    'unknown unrelated file outside the exact deletion plan is preserved');
  Check(LegacyCleanupGetFileAttributes('C:\SharedRuntime\coreclr.dll') = $80,
    'shared runtime outside the app directory is preserved');
  Check(LegacyCleanupGetFileAttributes(FixtureApp + '\HSRUnlocker.exe') = $80,
    'application executable outside the exact runtime deletion plan is preserved');
  Report := Report + 'CHECKS=' + IntToStr(Checks) + #13#10 +
    'FAILURES=' + IntToStr(Failures) + #13#10;
  if not SaveStringToFile(ExpandConstant('{param:ResultFile}'), Report, False) then
    RaiseException('Fixture report could not be written.');
end;
