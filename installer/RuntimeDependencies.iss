#ifndef DotNetRuntimeUrl
  #error Pass verified DotNetRuntimeUrl from build-installer.ps1.
#endif
#ifndef DotNetRuntimeSha256
  #error Pass verified DotNetRuntimeSha256 from build-installer.ps1.
#endif
#ifndef WinAppRuntimeUrl
  #error Pass verified WinAppRuntimeUrl from build-installer.ps1.
#endif
#ifndef WinAppRuntimeSha256
  #error Pass verified WinAppRuntimeSha256 from build-installer.ps1.
#endif
#ifndef WinAppRuntimeMajorMinor
  #error Pass WinAppRuntimeMajorMinor from the validated SDK identity.
#endif
#ifndef WinAppRuntimeMinVersion
  #error Pass WinAppRuntimeMinVersion from the validated SDK identity.
#endif

[Files]
; Only the application's own native probe DLL is extracted before prerequisites.
Source: "{#PublishDir}\Microsoft.WindowsAppRuntime.Bootstrap.dll"; Flags: dontcopy

[CustomMessages]
english.RuntimeDownloadTitle=Required Microsoft runtimes
english.RuntimeDownloadDescription=Download and verify missing runtime components.
english.RuntimeFailure=Could not prepare %1. %2
english.RuntimeInstallFailed=A runtime installer failed (code %1). Retry Setup to continue.
english.RuntimeNotDetected=A required runtime is still unavailable. Retry Setup after resolving the runtime installation.
english.RuntimeRestart=Restart Windows, then run Setup again to continue. Application files have not been changed.
chinesesimplified.RuntimeDownloadTitle=必需的 Microsoft 运行时
chinesesimplified.RuntimeDownloadDescription=下载并验证缺少的运行时组件。
chinesesimplified.RuntimeFailure=无法准备 %1。%2
chinesesimplified.RuntimeInstallFailed=运行时安装失败（代码 %1）。请重试安装程序。
chinesesimplified.RuntimeNotDetected=必需的运行时仍不可用。请解决运行时安装问题后重试。
chinesesimplified.RuntimeRestart=请重启 Windows 后重新运行安装程序。应用文件尚未更改。
japanese.RuntimeDownloadTitle=必要な Microsoft ランタイム
japanese.RuntimeDownloadDescription=不足しているランタイムをダウンロードして検証します。
japanese.RuntimeFailure=%1 を準備できませんでした。%2
japanese.RuntimeInstallFailed=ランタイムのインストールに失敗しました（コード %1）。セットアップを再試行してください。
japanese.RuntimeNotDetected=必要なランタイムを利用できません。問題を解決してからセットアップを再試行してください。
japanese.RuntimeRestart=Windows を再起動してからセットアップを再実行してください。アプリのファイルは変更されていません。
korean.RuntimeDownloadTitle=필수 Microsoft 런타임
korean.RuntimeDownloadDescription=누락된 런타임 구성 요소를 다운로드하고 확인합니다.
korean.RuntimeFailure=%1 준비에 실패했습니다. %2
korean.RuntimeInstallFailed=런타임 설치에 실패했습니다(코드 %1). 설치를 다시 시도하세요.
korean.RuntimeNotDetected=필수 런타임을 사용할 수 없습니다. 런타임 설치 문제를 해결한 후 다시 시도하세요.
korean.RuntimeRestart=Windows를 다시 시작한 후 설치를 다시 실행하세요. 앱 파일은 변경되지 않았습니다.

[Code]
var
  RuntimeDownloadPage: TDownloadWizardPage;

function CoInitializeEx(Reserved: NativeInt; CoInit: LongWord): Integer;
  external 'CoInitializeEx@ole32.dll stdcall';
procedure CoUninitialize;
  external 'CoUninitialize@ole32.dll stdcall';
function MddBootstrapInitialize2(MajorMinorVersion: LongWord; VersionTag: String;
  MinimumVersion: UInt64; Options: LongWord): Integer;
  external 'MddBootstrapInitialize2@files:Microsoft.WindowsAppRuntime.Bootstrap.dll stdcall delayload';
procedure MddBootstrapShutdown;
  external 'MddBootstrapShutdown@files:Microsoft.WindowsAppRuntime.Bootstrap.dll stdcall delayload';

function DotNet8VersionSupported(Version: String): Boolean;
  forward;

function RuntimeName(Runtime: Integer): String;
begin
  if Runtime = 0 then
    Result := 'Microsoft .NET 8 Runtime (x64)'
  else
    Result := 'Microsoft Windows App Runtime (x64)';
end;

function WindowsAppRuntimeInstalled: Boolean;
var
  CoResult, BootstrapResult: Integer;
  OwnCom: Boolean;
begin
  Result := False;
  OwnCom := False;
  try
    CoResult := CoInitializeEx(0, 2);
    OwnCom := CoResult >= 0;
    { RPC_E_CHANGED_MODE means COM already exists; this call owns no reference. }
    if not OwnCom and (CoResult <> -2147417850) then
      Exit;
    try
      BootstrapResult := MddBootstrapInitialize2({#WinAppRuntimeMajorMinor}, '',
        {#WinAppRuntimeMinVersion}, 0);
      if BootstrapResult >= 0 then begin
        MddBootstrapShutdown;
        Result := True;
      end;
    finally
      if OwnCom then
        CoUninitialize;
    end;
  except
    { Missing, unloadable or incompatible native probes are treated as unavailable. }
    Result := False;
  end;
end;

function RuntimeIsInstalled(Runtime: Integer): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if Runtime = 1 then begin
    Result := WindowsAppRuntimeInstalled;
    Exit;
  end;
  if RegGetValueNames(HKLM32,
    'Software\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.NETCore.App', Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if DotNet8VersionSupported(Versions[I]) then begin
        Result := True;
        Exit;
      end;
end;

procedure RuntimeDownload(Runtime: Integer);
var
  Url, FileName, Hash: String;
begin
  if Runtime = 0 then begin
    Url := '{#DotNetRuntimeUrl}';
    FileName := 'dotnet-runtime-x64.exe';
    Hash := '{#DotNetRuntimeSha256}';
  end else begin
    Url := '{#WinAppRuntimeUrl}';
    FileName := 'windows-app-runtime-x64.exe';
    Hash := '{#WinAppRuntimeSha256}';
  end;
  RuntimeDownloadPage.Clear;
  RuntimeDownloadPage.Add(Url, FileName, Hash);
  RuntimeDownloadPage.Show;
  try
    RuntimeDownloadPage.Download;
  finally
    RuntimeDownloadPage.Hide;
  end;
end;

function RuntimeExecute(Runtime: Integer; var ExitCode: Integer): Boolean;
var
  FileName, Parameters: String;
begin
  if Runtime = 0 then begin
    FileName := 'dotnet-runtime-x64.exe';
    Parameters := '/install /quiet /norestart';
  end else begin
    FileName := 'windows-app-runtime-x64.exe';
    Parameters := '--quiet';
  end;
  Result := Exec(ExpandConstant('{tmp}\') + FileName, Parameters, '',
    SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

#include "RuntimeState.iss"

procedure InitializeWizard;
begin
  RuntimeDownloadPage := CreateDownloadPage(CustomMessage('RuntimeDownloadTitle'),
    CustomMessage('RuntimeDownloadDescription'), nil);
  RuntimeDownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := EnsureRuntimeDependencies(NeedsRestart);
end;
