{ Shared policy. Native registry and attribute access live in LegacyRuntimeCleanup.iss. }
var
  LegacyCleanupOwnershipChecked, LegacyCleanupOwnershipValid: Boolean;
  LegacyCleanupOwnedDirectory: String;

function LegacyCleanupSafeSegment(Segment: String): Boolean;
var
  Device: String;
  Dot: Integer;
begin
  Result := False;
  if (Segment = '') or (Segment = '.') or (Segment = '..') then
    Exit;
  if (Segment[Length(Segment)] = '.') or (Segment[Length(Segment)] = ' ') then
    Exit;
  Device := Uppercase(Segment);
  Dot := Pos('.', Device);
  if Dot > 0 then
    Device := Copy(Device, 1, Dot - 1);
  if (Device = 'CON') or (Device = 'PRN') or (Device = 'AUX') or (Device = 'NUL') then
    Exit;
  if (Length(Device) = 4) and
    ((Copy(Device, 1, 3) = 'COM') or (Copy(Device, 1, 3) = 'LPT')) and
    (Device[4] >= '1') and (Device[4] <= '9') then
    Exit;
  Result := True;
end;

function LegacyCleanupSafeRelative(RelativePath: String): Boolean;
var
  Scan, Segment: String;
  I, SegmentStart: Integer;
begin
  Result := False;
  if RelativePath = '' then
    Exit;
  for I := 1 to Length(RelativePath) do
    if (Ord(RelativePath[I]) < 32) or
      (Pos(RelativePath[I], '\:*?"<>|{}') > 0) then
      Exit;
  Scan := RelativePath + '/';
  SegmentStart := 1;
  for I := 1 to Length(Scan) do
    if Scan[I] = '/' then begin
      Segment := Copy(Scan, SegmentStart, I - SegmentStart);
      if not LegacyCleanupSafeSegment(Segment) then
        Exit;
      SegmentStart := I + 1;
    end;
  Result := True;
end;

function NormalizeLegacyCleanupDirectory(Value: String): String;
var
  Scan, Segment: String;
  I, SegmentStart: Integer;
begin
  Result := '';
  if (Length(Value) < 4) or (Value <> Trim(Value)) then
    Exit;
  if not (((Value[1] >= 'A') and (Value[1] <= 'Z')) or
    ((Value[1] >= 'a') and (Value[1] <= 'z'))) then
    Exit;
  if (Value[2] <> ':') or (Value[3] <> '\') then
    Exit;
  if Value[Length(Value)] = '\' then
    Delete(Value, Length(Value), 1);
  for I := 1 to Length(Value) do
    if (Ord(Value[I]) < 32) or (Pos(Value[I], '/*?"<>|{}') > 0) or
      ((Value[I] = ':') and (I <> 2)) then
      Exit;
  Scan := Value + '\';
  SegmentStart := 4;
  for I := 4 to Length(Scan) do
    if Scan[I] = '\' then begin
      Segment := Copy(Scan, SegmentStart, I - SegmentStart);
      if not LegacyCleanupSafeSegment(Segment) then
        Exit;
      SegmentStart := I + 1;
    end;
  Result := Value;
end;

function LegacyCleanupSafeExistingPath(Path: String; RequireDirectory: Boolean): Boolean;
var
  Scan, Current: String;
  I: Integer;
  Attributes: LongWord;
  DirectoryExpected: Boolean;
begin
  Result := False;
  Scan := Path + '\';
  for I := 3 to Length(Scan) do
    if Scan[I] = '\' then begin
      if I = 3 then
        Current := Copy(Scan, 1, 3)
      else
        Current := Copy(Scan, 1, I - 1);
      Attributes := LegacyCleanupGetFileAttributes(Current);
      if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then
        Exit;
      DirectoryExpected := (I < Length(Scan)) or RequireDirectory;
      if DirectoryExpected <> ((Attributes and $10) <> 0) then
        Exit;
    end;
  Result := True;
end;

function LegacyCleanupMatchingUninstall(Is64Bit: Boolean; AppDirectory: String): Boolean;
var
  Location, DisplayName, NormalizedLocation: String;
begin
  Result := False;
  if not LegacyCleanupReadUninstallData(Is64Bit, Location, DisplayName) then
    Exit;
  NormalizedLocation := NormalizeLegacyCleanupDirectory(Location);
  Result := (DisplayName = 'HSRUnlocker') and (NormalizedLocation <> '') and
    (CompareText(NormalizedLocation, AppDirectory) = 0);
end;

function LegacyCleanupOwnsDirectory(AppDirectory: String): Boolean;
begin
  if not LegacyCleanupOwnershipChecked then begin
    { InstallDelete evaluates each file separately; remember ownership before coreclr is removed. }
    LegacyCleanupOwnershipChecked := True;
    LegacyCleanupOwnedDirectory := AppDirectory;
    LegacyCleanupOwnershipValid := False;
    if not LegacyCleanupSafeExistingPath(AppDirectory, True) or
      not LegacyCleanupSafeExistingPath(AppDirectory + '\HSRUnlocker.exe', False) or
      not LegacyCleanupSafeExistingPath(AppDirectory + '\coreclr.dll', False) then begin
      Result := False;
      Exit;
    end;
    LegacyCleanupOwnershipValid :=
      LegacyCleanupMatchingUninstall(True, AppDirectory) or
      LegacyCleanupMatchingUninstall(False, AppDirectory);
  end;
  Result := LegacyCleanupOwnershipValid and
    (CompareText(LegacyCleanupOwnedDirectory, AppDirectory) = 0);
end;

function CanDeleteLegacyRuntimeFile(RelativePath: String): Boolean;
var
  AppDirectory, NativeRelative: String;
begin
  Result := False;
  try
    if not LegacyCleanupSafeRelative(RelativePath) then
      Exit;
    AppDirectory := NormalizeLegacyCleanupDirectory(LegacyCleanupAppDirectory);
    if AppDirectory = '' then
      Exit;
    if not LegacyCleanupOwnsDirectory(AppDirectory) then
      Exit;
    NativeRelative := RelativePath;
    StringChangeEx(NativeRelative, '/', '\', True);
    Result := LegacyCleanupSafeExistingPath(AppDirectory + '\' + NativeRelative, False);
  except
    { Missing metadata, inaccessible attributes, or unexpected errors preserve the candidate. }
    Result := False;
  end;
end;
