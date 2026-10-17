[Code]
function LegacyCleanupNativeFileAttributes(FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function LegacyCleanupAppDirectory: String;
begin
  Result := ExpandConstant('{app}');
end;

function LegacyCleanupGetFileAttributes(Path: String): LongWord;
begin
  Result := LegacyCleanupNativeFileAttributes(Path);
end;

function LegacyCleanupReadUninstallData(Is64Bit: Boolean;
  var Location, DisplayName: String): Boolean;
var
  Root: Integer;
begin
  if Is64Bit then
    Root := HKLM64
  else
    Root := HKLM32;
  Result := RegQueryStringValue(Root,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{928328BA-7DAB-4F7F-9E3D-CC87061C2460}_is1',
    'InstallLocation', Location) and
    RegQueryStringValue(Root,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{928328BA-7DAB-4F7F-9E3D-CC87061C2460}_is1',
    'DisplayName', DisplayName);
end;

#include "LegacyCleanupState.iss"
