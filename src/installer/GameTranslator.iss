; Game Translator Inno Setup 安装脚本
; 用法: iscc GameTranslator.iss

#define AppName "Game Translator"
#define AppVersion "0.1.0"
#define AppPublisher "lhy12343"
#define AppURL "https://github.com/lhy12343/Game_Translator"
#define AppExeName "GameTranslator.exe"

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\GameTranslator
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=GameTranslator-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"

[Files]
Source: "release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 清理运行时生成的配置和缓存
Type: filesandordirs; Name: "{app}\Data"
Type: filesandordirs; Name: "{app}\Cache"
Type: filesandordirs; Name: "{app}\logs"
; 清理可能残留的其他文件，然后删除空目录
Type: filesandordirs; Name: "{app}\BepInEx"
Type: dirifempty; Name: "{app}"

[Code]
function IsProcessRunning(const ProcessName: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq ' + ProcessName + '" | find "' + ProcessName + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

procedure KillProcesses();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM GameTranslator.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C taskkill /F /IM GameTranslatorDebug.exe',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if IsProcessRunning('GameTranslator.exe') or IsProcessRunning('GameTranslatorDebug.exe') then
  begin
    if MsgBox('检测到 Game Translator 正在运行，需要先关闭程序才能继续安装。' + #13#10 + #13#10 +
              '是否立即关闭并继续？', mbConfirmation, MB_YESNO) = IDYES then
    begin
      KillProcesses();
      if IsProcessRunning('GameTranslator.exe') or IsProcessRunning('GameTranslatorDebug.exe') then
      begin
        MsgBox('无法自动关闭程序，请手动关闭后重试。', mbError, MB_OK);
        Result := False;
      end;
    end
    else
      Result := False;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if IsProcessRunning('GameTranslator.exe') or IsProcessRunning('GameTranslatorDebug.exe') then
  begin
    if MsgBox('检测到 Game Translator 正在运行，需要先关闭程序才能卸载。' + #13#10 + #13#10 +
              '是否立即关闭并继续卸载？', mbConfirmation, MB_YESNO) = IDYES then
    begin
      KillProcesses();
      if IsProcessRunning('GameTranslator.exe') or IsProcessRunning('GameTranslatorDebug.exe') then
      begin
        MsgBox('无法自动关闭程序，请手动关闭后重试。', mbError, MB_OK);
        Result := False;
      end;
    end
    else
      Result := False;
  end;
end;
