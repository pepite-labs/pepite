; Pépite — installer script (plan 12, D6, D11, D13, D27 amended, D28). Inno Setup 7.1.1 (innosetup.lock.json).
; GPL-3.0-or-later like the rest of Pépite: an installation script is part of the Corresponding Source (GPL-3 § 1).
; Built by build_installer.ps1, which passes the defines below; do not compile it by hand without them:
;   /DVersion=0.11.0 /DStageDir=<stage> /DOutDir=<release> /DTouchDate=YYYY-MM-DD /DTouchTime=HH:MM:SS
;   [/DPrivate]  (private test build: « -test » suffix, keeps any "public": false voice pack, D28)
;   [/DSIGN]     (code signing, prepared, off: Q4 — SignPath later)
; No network: no download of any kind; the URLs below are only shown as text.

#if VER < EncodeVer(7, 1, 1)
  #error Inno Setup 7.1.1 or later is required (installer\innosetup.lock.json)
#endif
#ifndef Version
  #error Pass /DVersion=x.y.z (build_installer.ps1)
#endif
#ifndef StageDir
  #error Pass /DStageDir=<publish folder> (build_installer.ps1)
#endif
#ifndef OutDir
  #define OutDir "."
#endif
#ifndef TouchDate
  #error Pass /DTouchDate=YYYY-MM-DD (the commit date, for reproducible builds)
#endif
#ifndef TouchTime
  #define TouchTime "00:00:00"
#endif
#ifdef Private
  #define Suffix "-test"
#else
  #define Suffix ""
#endif
; RAM below which the « Prêt à installer » page shows a warning (never blocking). PLACEHOLDER: the plan's 4 GB — the
; measurement of plan 12 step 10 was deferred by the owner (2026-11-15); Windows sees a little less than the nominal
; amount, hence 3.5 GB
#ifndef MinRamMB
  #define MinRamMB "3584"
#endif

#define AppIdGuid "999AA879-43C2-4C22-9785-691AA95A3455"
#define SiteUrl "https://github.com/pepite-labs/pepite"

[Setup]
; the fixed AppId (committed once, never changed; InstallInfo.AppId in the app reads the same uninstall entry)
AppId={{{#AppIdGuid}}
AppName=Pépite
AppVersion={#Version}
AppVerName=Pépite {#Version}
AppPublisher=Les auteurs de Pépite
AppPublisherURL={#SiteUrl}
AppSupportURL={#SiteUrl}
AppUpdatesURL={#SiteUrl}
AppCopyright=© 2026 Les auteurs de Pépite
; per-user by default (%LOCALAPPDATA%\Programs), « Pour tous les utilisateurs » / /ALLUSERS -> Program Files (D6, D11)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\Pepite-Prediction
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
; x64 only (ARM64 refused with the French message below), Windows 11 only (owner Q7)
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
; French only (D14)
ShowLanguageDialog=no
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
ChangesAssociations=yes
; a running Pépite is asked to quit first ([Code]); Restart Manager is the fallback, and Pépite is never restarted by it
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\src\Predict.Keyboard\Assets\app.ico
UninstallDisplayIcon={app}\Pepite.exe
UninstallDisplayName=Pépite
OutputDir={#OutDir}
OutputBaseFilename=Pepite-{#Version}{#Suffix}-installation
VersionInfoVersion={#Version}.0
VersionInfoProductName=Pépite
VersionInfoProductVersion={#Version}
VersionInfoCompany=Les auteurs de Pépite
VersionInfoDescription=Installation de Pépite — prédiction de mots
VersionInfoCopyright=© 2026 Les auteurs de Pépite
; reproducible builds: UTC time stamps, every installed file dated with the commit date (« touch »)
TimeStampsInUTC=yes
TouchDate={#TouchDate}
TouchTime={#TouchTime}
SetupLogging=yes
InfoBeforeFile=avant-installation.txt
#ifdef SIGN
; prepared for the public release (Q4): define the « pepite » sign tool in the compiler (ISCC /Spepite=…)
SignTool=pepite
SignedUninstaller=yes
#endif

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"

[Messages]
; T14: the refusal on Windows 10 (MinVersion) and on a non-x64 PC, in plain French
WinVersionTooLowError=Pépite fonctionne seulement sous Windows 11. Cette version de Windows n'est pas prise en charge : l'installation va s'arrêter.
OnlyOnTheseArchitectures=Pépite s'installe seulement sur un Windows 11 64 bits pour processeur Intel ou AMD (x64). Les ordinateurs à processeur ARM ne sont pas encore pris en charge.
PrivilegesRequiredOverrideText1=%1 peut être installé pour tous les utilisateurs de cet ordinateur (il faut un compte administrateur ; conseillé dans une école), ou seulement pour vous.
PrivilegesRequiredOverrideText2=%1 peut être installé seulement pour vous, ou pour tous les utilisateurs de cet ordinateur (il faut un compte administrateur ; conseillé dans une école).
; the default speaks of « icônes créées sur le Bureau », wrong when that task is left unticked (the default): the Start
; menu shortcut is always made, the desktop one only on request
FinishedLabel=L'assistant a terminé l'installation de [name] sur votre ordinateur. Pépite se trouve dans le menu Démarrer.
FinishedLabelNoIcons=L'assistant a terminé l'installation de [name] sur votre ordinateur.

[Types]
Name: "complete"; Description: "Installation complète"
Name: "custom"; Description: "Installation personnalisée"; Flags: iscustom

[Components]
Name: "app"; Description: "Pépite (programme, dictionnaire, grammaire, polices, noms propres)"; Types: complete custom; Flags: fixed
Name: "images"; Description: "Images des mots (pictogrammes ARASAAC)"; Types: complete custom
Name: "voix"; Description: "Voix naturelles (sinon, la voix de Windows)"; Types: complete custom
Name: "voix\siwis"; Description: "Siwis (voix féminine)"; Types: complete custom
; D28 changed 2026-11-16 (owner): pierre ships in the public build too. build_installer.ps1 defines HasPierre only when
; the stage holds voices\pierre; withdrawal = its card says "public": false again, the public stage drops the folder,
; and an installer without HasPierre removes voices\pierre from an earlier install
#ifdef HasPierre
Name: "voix\pierre"; Description: "Pierre (voix masculine)"; Types: complete custom
#endif

[Tasks]
Name: "demarrage"; Description: "Démarrer Pépite avec Windows"
Name: "pepite"; Description: "Ouvrir les fichiers .pepite (profils des élèves) avec Pépite"
Name: "bureau"; Description: "Créer une icône sur le Bureau"; Flags: unchecked

[Files]
; the fixed part: the exe, the self-contained .NET runtime, prediction.sqlite + prediction-SOURCES.txt, grammar.sqlite + notices,
; exclusions\ (0.11.0: fr.txt + SOURCE.txt, the « mots exclus »; part of the app component, no switch), fonts\, noms\,
; espeak-ng, licenses\, COPYING, THIRD-PARTY-NOTICES.txt, LISEZ-MOI.txt (PDBs are in symbols-<ver>.zip, never installed);
; ARASAAC-LICENCE.txt too (0.10.2): the help's screenshots show ARASAAC pictograms even when the images component is off,
; so their licence is always installed (an upgrade from 0.10.1 with images off adds it)
Source: "{#StageDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,\pictos.sqlite,\voices"; Flags: ignoreversion recursesubdirs createallsubdirs touch; Components: app
Source: "{#StageDir}\pictos.sqlite"; DestDir: "{app}"; Flags: ignoreversion touch; Components: images
Source: "{#StageDir}\voices\siwis\*"; DestDir: "{app}\voices\siwis"; Flags: ignoreversion recursesubdirs createallsubdirs touch; Components: voix\siwis
#ifdef HasPierre
Source: "{#StageDir}\voices\pierre\*"; DestDir: "{app}\voices\pierre"; Flags: ignoreversion recursesubdirs createallsubdirs touch; Components: voix\pierre
#endif

[InstallDelete]
; the embedded help is replaced as a whole at an upgrade (a page renamed or removed must not stay behind)
Type: filesandordirs; Name: "{app}\aide"
; 0.11.0: the « mots exclus » folder too (only the shipped list, never an older one, at an upgrade)
Type: filesandordirs; Name: "{app}\exclusions"
; a component unticked at an upgrade loses its files
Type: files; Name: "{app}\pictos.sqlite"; Components: not images
Type: filesandordirs; Name: "{app}\voices\siwis"; Components: not voix\siwis
#ifdef HasPierre
Type: filesandordirs; Name: "{app}\voices\pierre"; Components: not voix\pierre
#else
; no pierre in this build (withdrawn, D28): never leave it behind from an earlier install
Type: filesandordirs; Name: "{app}\voices\pierre"
#endif
; files of older layouts, listed one by one (never a wildcard over {app})
Type: files; Name: "{app}\Pepite.pdb"
Type: files; Name: "{app}\Predict.Core.pdb"
Type: filesandordirs; Name: "{app}\cs"
Type: filesandordirs; Name: "{app}\de"
Type: filesandordirs; Name: "{app}\es"
Type: filesandordirs; Name: "{app}\it"
Type: filesandordirs; Name: "{app}\ja"
Type: filesandordirs; Name: "{app}\ko"
Type: filesandordirs; Name: "{app}\pl"
Type: filesandordirs; Name: "{app}\pt-BR"
Type: filesandordirs; Name: "{app}\ru"
Type: filesandordirs; Name: "{app}\tr"
Type: filesandordirs; Name: "{app}\zh-Hans"
Type: filesandordirs; Name: "{app}\zh-Hant"
; 0.10.2: the .NET 6 runtime files that the .NET 10 self-contained publish no longer ships (45, from the file lists
; of stage-0.10.1 and stage-0.10.2). Left in {app}, the app-local ucrtbase.dll and api-ms-win-*.dll forwarders of
; .NET 6 would sit next to the .NET 10 runtime. At the next runtime update, add the old
; mscordaccore_amd64_amd64_<version>.dll here too (its name carries the runtime build number).
Type: files; Name: "{app}\api-ms-win-core-console-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-console-l1-2-0.dll"
Type: files; Name: "{app}\api-ms-win-core-datetime-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-debug-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-errorhandling-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-fibers-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-file-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-file-l1-2-0.dll"
Type: files; Name: "{app}\api-ms-win-core-file-l2-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-handle-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-heap-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-interlocked-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-libraryloader-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-localization-l1-2-0.dll"
Type: files; Name: "{app}\api-ms-win-core-memory-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-namedpipe-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-processenvironment-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-processthreads-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-processthreads-l1-1-1.dll"
Type: files; Name: "{app}\api-ms-win-core-profile-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-rtlsupport-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-string-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-synch-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-synch-l1-2-0.dll"
Type: files; Name: "{app}\api-ms-win-core-sysinfo-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-timezone-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-core-util-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-conio-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-convert-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-environment-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-filesystem-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-heap-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-locale-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-math-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-multibyte-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-private-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-process-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-runtime-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-stdio-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-string-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-time-l1-1-0.dll"
Type: files; Name: "{app}\api-ms-win-crt-utility-l1-1-0.dll"
Type: files; Name: "{app}\dbgshim.dll"
Type: files; Name: "{app}\mscordaccore_amd64_amd64_6.0.3224.31407.dll"
Type: files; Name: "{app}\ucrtbase.dll"

[Registry]
; start with Windows (D13, Q2): HKA = HKCU for a per-user install, HKLM for all users. The contract of the app
; (StartupPolicy): value « Pepite-Prediction » = "<app>\Pepite.exe" --autostart. Explorer\StartupApproved is never touched.
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Pepite-Prediction"; ValueData: """{app}\Pepite.exe"" --autostart"; Flags: uninsdeletevalue; Tasks: demarrage
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Pepite-Prediction"; Flags: deletevalue; Tasks: not demarrage
; .pepite -> « Profils Pépite » (Q3): Pepite.exe --import "<file>" (the path is never taken as the prediction database)
Root: HKA; Subkey: "Software\Classes\.pepite"; ValueType: string; ValueName: ""; ValueData: "Pepite-Prediction.Profils"; Flags: uninsdeletevalue; Tasks: pepite
Root: HKA; Subkey: "Software\Classes\.pepite\OpenWithProgids"; ValueType: string; ValueName: "Pepite-Prediction.Profils"; ValueData: ""; Flags: uninsdeletevalue; Tasks: pepite
Root: HKA; Subkey: "Software\Classes\Pepite-Prediction.Profils"; ValueType: string; ValueName: ""; ValueData: "Profils Pépite"; Flags: uninsdeletekey; Tasks: pepite
Root: HKA; Subkey: "Software\Classes\Pepite-Prediction.Profils\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\Pepite.exe"",0"; Tasks: pepite
Root: HKA; Subkey: "Software\Classes\Pepite-Prediction.Profils\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Pepite.exe"" --import ""%1"""; Tasks: pepite
Root: HKA; Subkey: "Software\Classes\Pepite-Prediction.Profils"; ValueType: none; Flags: deletekey; Tasks: not pepite
Root: HKA; Subkey: "Software\Classes\.pepite\OpenWithProgids"; ValueType: none; ValueName: "Pepite-Prediction.Profils"; Flags: deletevalue; Tasks: not pepite
Root: HKA; Subkey: "Software\Classes\.pepite"; ValueType: none; ValueName: ""; Flags: deletevalue; Tasks: not pepite; Check: PepiteAssocIsOurs

[Icons]
Name: "{autoprograms}\Pépite"; Filename: "{app}\Pepite.exe"; Comment: "Pépite — prédiction de mots"
Name: "{autodesktop}\Pépite"; Filename: "{app}\Pepite.exe"; Comment: "Pépite — prédiction de mots"; Tasks: bureau

[Run]
; never elevated by itself (runasoriginaluser), never after a silent install: users get Pépite at their next logon
; through the Run key; a silent upgrade restarts a copy that was running for this account (RestartAfterSilentInstall)
Filename: "{app}\Pepite.exe"; Description: "Lancer Pépite"; Flags: postinstall nowait skipifsilent runasoriginaluser

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppIdGuid}}_is1';
  InstanceMutex = 'Local\Pepite-Prediction.Instance';
  QuitEvent = 'Local\Pepite-Prediction.Quit';
  EVENT_MODIFY_STATE = $0002;
  MinRamMB = {#MinRamMB};

type
  TMemoryStatusEx = record
    dwLength: DWORD;
    dwMemoryLoad: DWORD;
    ullTotalPhys: UInt64;
    ullAvailPhys: UInt64;
    ullTotalPageFile: UInt64;
    ullAvailPageFile: UInt64;
    ullTotalVirtual: UInt64;
    ullAvailVirtual: UInt64;
    ullAvailExtendedVirtual: UInt64;
  end;

function GlobalMemoryStatusEx(var lpBuffer: TMemoryStatusEx): BOOL;
  external 'GlobalMemoryStatusEx@kernel32.dll stdcall';
function OpenEvent(dwDesiredAccess: DWORD; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributes(lpFileName: String): DWORD;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function GetNamedSecurityInfo(pObjectName: String; ObjectType: Integer; SecurityInfo: DWORD; var ppsidOwner: NativeUInt;
  ppsidGroup: NativeUInt; ppDacl: NativeUInt; ppSacl: NativeUInt; var ppSecurityDescriptor: NativeUInt): DWORD;
  external 'GetNamedSecurityInfoW@advapi32.dll stdcall';
function IsWellKnownSid(pSid: NativeUInt; WellKnownSidType: Integer): BOOL;
  external 'IsWellKnownSid@advapi32.dll stdcall';
function LocalFree(hMem: NativeUInt): NativeUInt;
  external 'LocalFree@kernel32.dll stdcall';

const
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;
  SE_FILE_OBJECT = 1;
  OWNER_SECURITY_INFORMATION = 1;
  WinBuiltinAdministratorsSid = 26;
  WinLocalSystemSid = 22;
  // owner Administrators, no inherited entry, Administrators and SYSTEM full control, Users read (this folder only:
  // never /T — icacls would follow a junction planted inside)
  ProtectedAcl = ' /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX';

var
  ConfigPage: TInputFileWizardPage;
  ConfigFailed: Boolean;
  { plan installer-upgrade: the installed version (uninstall entry DisplayVersion), read in InitializeSetup }
  PrevVersion: String;
  { the tasks of the previous install (« Inno Setup: Selected Tasks » of the uninstall entry): the tasks list is only
    filled when its page is reached, so WizardIsTaskSelected says nothing on the upgrade page (VM, 2026-11-27) }
  PrevTasks: String;
  PrevTasksKnown: Boolean;
  { the tasks page was shown: from then on its ticks are the truth (Back to the upgrade page after a change) }
  TasksPageSeen: Boolean;
  IsUpgrade, IsRepair: Boolean;
  UpgradePage: TWizardPage;
  UpgradeText: TNewStaticText;
  ChangeChoices: TNewCheckBox;
  { a Pépite of this session was running when the install began (R6) }
  PepiteWasRunning: Boolean;

{ ---------------------------------------------------------------- file-system trust }

{ a link (junction, symbolic link, mount point): never written through, never deleted into }
function IsReparsePoint(const Path: String): Boolean;
var
  A: DWORD;
begin
  A := GetFileAttributes(Path);
  Result := (A <> INVALID_FILE_ATTRIBUTES) and ((A and FILE_ATTRIBUTE_REPARSE_POINT) <> 0);
end;

{ owned by BUILTIN\Administrators or SYSTEM (False when unreadable). SettingsLayering.TrustedOwners also accepts
  TrustedInstaller, which never owns a folder this installer creates or must trust: left out on purpose. }
function OwnedByAdmins(const Path: String): Boolean;
var
  Owner, Sd: NativeUInt;
begin
  Result := False;
  Owner := 0; Sd := 0;
  if GetNamedSecurityInfo(Path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, Owner, 0, 0, 0, Sd) = 0 then
  begin
    if Owner <> 0 then
    begin
      if IsWellKnownSid(Owner, WinBuiltinAdministratorsSid) then Result := True;
      if IsWellKnownSid(Owner, WinLocalSystemSid) then Result := True;
    end;
    LocalFree(Sd);
  end;
end;

{ .pepite still points to our ProgID (the task unticked at an upgrade removes only our own association) }
function PepiteAssocIsOurs(): Boolean;
var
  V: String;
  Root: Integer;
begin
  if IsAdminInstallMode then Root := HKLM else Root := HKCU;
  Result := RegQueryStringValue(Root, 'Software\Classes\.pepite', '', V) and (CompareText(V, 'Pepite-Prediction.Profils') = 0);
end;

{ ---------------------------------------------------------------- command line }

{ /NAME (a switch without a value), case-insensitive }
function CmdSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
      Result := True;
end;

{ /NAME=value, case-insensitive name; '' when absent }
function CmdValue(const Name: String): String;
var
  I: Integer;
  P: String;
begin
  Result := '';
  for I := 1 to ParamCount do
  begin
    P := ParamStr(I);
    if CompareText(Copy(P, 1, Length(Name) + 2), '/' + Name + '=') = 0 then
      Result := RemoveQuotes(Copy(P, Length(Name) + 3, MaxInt));
  end;
end;

{ ---------------------------------------------------------------- running copy }

{ Ask a running Pépite to quit (the app's --quit event), then wait for its single-instance mutex to go: every 250 ms,
  up to 10 s. Restart Manager (CloseApplications) remains the fallback for a copy we may not signal. }
procedure CloseRunningPepite(const Why: String);
var
  H: THandle;
  I: Integer;
begin
  if not CheckForMutexes(InstanceMutex) then
  begin
    Log('Pepite: not running (' + Why + ')');
    Exit;
  end;
  if Why = 'install' then
    PepiteWasRunning := True;
  H := OpenEvent(EVENT_MODIFY_STATE, False, QuitEvent);
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandle(H);
    Log('Pepite: quit requested (' + Why + ')');
  end else
    Log('Pepite: running, but its quit event cannot be opened (' + Why + ')');
  for I := 1 to 40 do
  begin
    if not CheckForMutexes(InstanceMutex) then
    begin
      Log(Format('Pepite: closed after %d ms', [I * 250 - 250]));
      Exit;
    end;
    Sleep(250);
  end;
  Log('Pepite: still running after 10 s; Restart Manager decides');
end;

{ ---------------------------------------------------------------- setup }

{ Plan installer-upgrade (R1, R2): the version already installed in this mode, from the uninstall entry. An older one
  -> an upgrade (its own page, two clicks); the same -> a repair; a newer one -> refused before any change (exit 1,
  owner 2026-11-26: going back = uninstall first, the data is kept). A version that cannot be read is not compared. }
function CheckInstalledVersion(): Boolean;
var
  Root, Cmp: Integer;
  P, N: Int64;
  Msg: String;
begin
  Result := True;
  if IsAdminInstallMode then Root := HKLM64 else Root := HKCU;
  IsUpgrade := RegKeyExists(Root, UninstallKey);
  IsRepair := False;
  PrevVersion := '';
  if not IsUpgrade then
  begin
    Log('install: Pepite {#Version}, no earlier install in this mode');
    Exit;
  end;
  RegQueryStringValue(Root, UninstallKey, 'DisplayVersion', PrevVersion);
  PrevTasksKnown := RegQueryStringValue(Root, UninstallKey, 'Inno Setup: Selected Tasks', PrevTasks);
  PrevVersion := Trim(PrevVersion);
  if not (StrToVersion(PrevVersion, P) and StrToVersion('{#Version}', N)) then
  begin
    Log('upgrade: previous version unreadable (''' + PrevVersion + ''') -> {#Version}');
    PrevVersion := '';
    Exit;
  end;
  Cmp := ComparePackedVersion(P, N);
  if Cmp > 0 then
  begin
    Log('downgrade refused: installed ' + PrevVersion + ', this setup {#Version}');
    Msg := 'Pépite ' + PrevVersion + ' est déjà installé sur cet ordinateur : c''est une version plus récente que celle ' +
      'de cette installation ({#Version}).' + #13#10 + 'L''installation s''arrête sans rien changer.' + #13#10#13#10 +
      'Pour revenir à la version {#Version} : désinstallez d''abord Pépite (Paramètres > Applications) en répondant ' +
      '« Non » à la question sur les réglages, ils sont gardés. Puis relancez cette installation.';
    if not WizardSilent then
      MsgBox(Msg, mbError, MB_OK);
    Result := False;
  end else if Cmp = 0 then
  begin
    IsRepair := True;
    Log('repair: Pepite ' + PrevVersion + ' reinstalled');
  end else
    Log('upgrade: ' + PrevVersion + ' -> {#Version}');
end;

function InitializeSetup(): Boolean;
var
  Other: Boolean;
  Msg: String;
begin
  Result := True;
  ConfigFailed := False;
  PepiteWasRunning := False;
  { other-mode guard: one install per PC, per-user OR all-users }
  if IsAdminInstallMode then
  begin
    Other := RegKeyExists(HKCU, UninstallKey);
    Msg := 'Pépite est déjà installé pour ce compte seulement. Désinstallez-le d''abord (Paramètres > Applications), puis relancez cette installation.';
  end else begin
    Other := RegKeyExists(HKLM64, UninstallKey);
    Msg := 'Pépite est déjà installé pour tous les utilisateurs de cet ordinateur. Désinstallez-le d''abord (il faut un compte administrateur), ou demandez à l''administrateur de l''ordinateur.';
  end;
  if Other then
  begin
    Log('other-mode guard: ' + Msg);
    if not WizardSilent then
      MsgBox(Msg, mbError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := CheckInstalledVersion();
end;

{ the upgrade page's subtitle (R1) }
function UpgradeSubtitle(): String;
begin
  if IsRepair then
    Result := 'Pépite {#Version} est déjà installé : il sera réinstallé (réparation).'
  else if PrevVersion <> '' then
    Result := 'Pépite ' + PrevVersion + ' est déjà installé : il sera mis à jour en {#Version}.'
  else
    Result := 'Pépite est déjà installé : il sera mis à jour en {#Version}.';
end;

{ Next on the upgrade page: « Mettre à jour » goes straight to the installation (R4); with « Modifier les choix » the
  usual pages come back, so it is « Suivant » }
procedure UpdateUpgradeNextButton();
begin
  if (UpgradePage = nil) or (WizardForm.CurPageID <> UpgradePage.ID) then
    Exit;
  if ChangeChoices.Checked then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext)
  else if IsRepair then
    WizardForm.NextButton.Caption := 'Réinstaller'
  else
    WizardForm.NextButton.Caption := 'Mettre à jour';
end;

procedure ChangeChoicesClick(Sender: TObject);
begin
  UpdateUpgradeNextButton();
end;

procedure InitializeWizard();
begin
  { High DPI (owner 2026-11-18, reproduced in the VM at 150 % and 200 %, 2026-11-27): in Inno Setup 7.1.1,
    TNewCheckListBox takes the check box width from LoadBitmap(OBM_CHECKBOXES) — the 96 DPI size, 13 px — but draws
    the themed box at its real size (26 px at 200 %), placed from a text rectangle computed with 13 px: a top-level
    box starts at 13 + Offset + 2 - 26 = -9 px at 200 % and looks cut to half its width (children are indented,
    hence whole; tasks only -1 px at 150 %). Same with WizardStyle=classic. Workaround: widen the gap (Offset) by 8
    scaled pixels. Upstream: commit 5b4c39b6 (Inno Setup 7.2.0-preview-1) removes the LoadBitmap call — at 7.2.0
    final, test and drop this workaround (TODO). Not reported upstream: already fixed there (owner 2026-11-27). }
  WizardForm.ComponentsList.Offset := WizardForm.ComponentsList.Offset + ScaleX(8);
  WizardForm.TasksList.Offset := WizardForm.TasksList.Offset + ScaleX(8);
  WizardForm.RunList.Offset := WizardForm.RunList.Offset + ScaleX(8);
  if IsUpgrade then
  begin
    UpgradePage := CreateCustomPage(wpWelcome, 'Mise à jour de Pépite', UpgradeSubtitle());
    UpgradeText := TNewStaticText.Create(UpgradePage);
    UpgradeText.Parent := UpgradePage.Surface;
    UpgradeText.AutoSize := False;
    UpgradeText.WordWrap := True;
    UpgradeText.Left := 0;
    UpgradeText.Top := 0;
    UpgradeText.Width := UpgradePage.SurfaceWidth;
    UpgradeText.Height := UpgradePage.SurfaceHeight - ScaleY(36);
    ChangeChoices := TNewCheckBox.Create(UpgradePage);
    ChangeChoices.Parent := UpgradePage.Surface;
    ChangeChoices.Caption := 'Modifier les choix (composants et tâches)';
    ChangeChoices.Left := 0;
    ChangeChoices.Width := UpgradePage.SurfaceWidth;
    ChangeChoices.Height := ScaleY(24);
    ChangeChoices.Top := UpgradePage.SurfaceHeight - ChangeChoices.Height;
    ChangeChoices.Checked := False;
    ChangeChoices.OnClick := @ChangeChoicesClick;
  end;
  ConfigPage := CreateInputFilePage(wpSelectTasks,
    'Configuration de l''établissement (facultatif)',
    'Des réglages proposés ou imposés à tous les comptes de cet ordinateur.',
    'Si votre établissement vous a donné un fichier de configuration (settings.json), choisissez-le ici. ' +
    'Sinon, laissez la case vide et cliquez sur Suivant. Un fichier déjà installé est gardé.');
  ConfigPage.Add('Fichier de configuration :', 'Fichiers de configuration (*.json)|*.json|Tous les fichiers|*.*', '.json');
  ConfigPage.Values[0] := CmdValue('CONFIG');
end;

{ At an upgrade (R4): the information page is never shown again; components, tasks, configuration and the ready page
  only with « Modifier les choix ». A first install sees no upgrade page. }
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ConfigPage.ID) and not IsAdminInstallMode;
  if not IsUpgrade then
    Exit;
  if PageID = wpInfoBefore then
    Result := True;
  if not ChangeChoices.Checked and ((PageID = wpSelectComponents) or (PageID = wpSelectTasks) or
    (PageID = ConfigPage.ID) or (PageID = wpReady)) then
    Result := True;
end;

function TotalRamMB(): Integer;
var
  M: TMemoryStatusEx;
begin
  Result := 0;
  M.dwLength := SizeOf(M);
  if GlobalMemoryStatusEx(M) then
    Result := M.ullTotalPhys div (1024 * 1024);
end;

{ the low-memory warning of the ready page and of the upgrade page ('' when there is enough) }
function RamWarning(): String;
var
  Ram: Integer;
begin
  Result := '';
  Ram := TotalRamMB();
  Log(Format('RAM: %d MB (warning below %d MB)', [Ram, MinRamMB]));
  if (Ram > 0) and (Ram < MinRamMB) then
    Result := 'Attention : cet ordinateur a ' + IntToStr(Ram) + ' Mo de mémoire. ' +
      'Pépite demande au moins 4 Go ; il peut être lent ici (l''installation reste possible).';
end;

procedure AddMemoPart(var Memo: String; const Part, NewLine: String);
begin
  if Part = '' then
    Exit;
  if Memo <> '' then
    Memo := Memo + NewLine + NewLine;
  Memo := Memo + Part;
end;

{ a task of the previous install (the registry), else the wizard's current state }
function PrevTaskOn(const Name: String): Boolean;
begin
  if PrevTasksKnown and not TasksPageSeen and (CmdValue('TASKS') = '') then
    Result := Pos(',' + Lowercase(Name) + ',', ',' + Lowercase(PrevTasks) + ',') > 0
  else
    Result := WizardIsTaskSelected(Name);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo,
  MemoGroupInfo, MemoTasksInfo: String): String;
var
  Warn: String;
begin
  { the folder line is empty at an upgrade (the folder page is skipped): no blank paragraphs }
  Result := '';
  AddMemoPart(Result, MemoDirInfo, NewLine);
  AddMemoPart(Result, MemoComponentsInfo, NewLine);
  AddMemoPart(Result, MemoTasksInfo, NewLine);
  if IsUpgrade then
  begin
    if PrevVersion <> '' then
      Result := 'Mise à jour : Pépite ' + PrevVersion + ' → {#Version}' + NewLine + NewLine + Result
    else
      Result := 'Mise à jour : Pépite → {#Version}' + NewLine + NewLine + Result;
  end;
  if IsAdminInstallMode and (Trim(ConfigPage.Values[0]) <> '') then
    Result := Result + NewLine + NewLine + 'Configuration de l''établissement :' + NewLine + Space + ConfigPage.Values[0];
  Warn := RamWarning();
  if Warn <> '' then
    Result := Result + NewLine + NewLine + Warn;
end;

{ the upgrade page's summary: what is kept, and the current choices (the ticks of the previous install) }
function UpgradeSummary(): String;
var
  Comps, Tasks, Warn: String;
begin
  Comps := 'Pépite';
  if WizardIsComponentSelected('images') then Comps := Comps + ', images des mots';
  if WizardIsComponentSelected('voix\siwis') then Comps := Comps + ', voix Siwis';
#ifdef HasPierre
  if WizardIsComponentSelected('voix\pierre') then Comps := Comps + ', voix Pierre';
#endif
  Tasks := '';
  if PrevTaskOn('demarrage') then Tasks := Tasks + ', démarrer avec Windows';
  if PrevTaskOn('pepite') then Tasks := Tasks + ', ouvrir les fichiers .pepite';
  if PrevTaskOn('bureau') then Tasks := Tasks + ', icône sur le Bureau';
  if Tasks = '' then Tasks := 'aucune' else Tasks := Copy(Tasks, 3, MaxInt);
  Result := 'Vos réglages, les profils des élèves et leurs mots sont gardés. Pépite sera fermé pendant la mise à jour.' + #13#10#13#10 +
    'Dossier : ' + WizardDirValue + #13#10 + 'Composants : ' + Comps + #13#10 + 'Tâches : ' + Tasks;
  if IsAdminInstallMode and (Trim(ConfigPage.Values[0]) <> '') then
    Result := Result + #13#10 + 'Configuration de l''établissement : ' + ConfigPage.Values[0];
  Warn := RamWarning();
  if Warn <> '' then
    Result := Result + #13#10#13#10 + Warn;
  Result := Result + #13#10#13#10 + 'Cliquez sur « ';
  if IsRepair then Result := Result + 'Réinstaller' else Result := Result + 'Mettre à jour';
  Result := Result + ' » pour garder ces choix, ou cochez la case ci-dessous pour les changer.';
end;

procedure CurPageChanged(CurPageID: Integer);
var
  H: Integer;
begin
  if CurPageID = wpSelectTasks then
    TasksPageSeen := True;
  if IsUpgrade and (CurPageID = UpgradePage.ID) then
  begin
    UpgradeText.Caption := UpgradeSummary();
    UpdateUpgradeNextButton();
  end;
  if IsUpgrade and (CurPageID = wpFinished) then
  begin
    WizardForm.FinishedHeadingLabel.Caption := 'Mise à jour terminée';
    if IsRepair then
      WizardForm.FinishedLabel.Caption := 'Pépite {#Version} a été réinstallé. Vos réglages et les profils sont gardés.'
    else
      WizardForm.FinishedLabel.Caption := 'Pépite a été mis à jour en {#Version}. Vos réglages et les profils sont gardés.';
    { the run list sits below the label: follow its new height }
    H := WizardForm.AdjustLabelHeight(WizardForm.FinishedLabel);
    WizardForm.IncTopDecHeight(WizardForm.RunList, H);
  end;
end;

{ All users: a direct child of Program Files only (owner decision 2026-11-15, plan 12 review). Elsewhere a parent
  folder (another drive, or a vendor folder inside Program Files) may let Users rename or replace the folder, or plant
  files and links in a folder that already exists, and every pupil would then run them. '' when Dir is
  <Program Files>\<name> and not a link, else the French refusal (exit 7). }
function AllUsersDirProblem(const Dir: String): String;
var
  Pf, App: String;
begin
  Result := '';
  Pf := RemoveBackslash(ExpandFileName(ExpandConstant('{commonpf}')));
  App := RemoveBackslash(ExpandFileName(Dir));
  if CompareText(ExtractFileDir(App), Pf) <> 0 then
  begin
    Result := 'Pour tous les utilisateurs, Pépite s''installe uniquement dans un dossier de Program Files (' + Pf +
      '\Pepite-Prediction). Relancez l''installation sans changer de dossier (ni /DIR).';
    Exit;
  end;
  if IsReparsePoint(App) then
    Result := 'Le dossier d''installation est un lien (jonction ou lien symbolique) : ' + App + '.';
end;

{ The folder page and the configuration page refuse early in the wizard only; a silent run goes on and is refused by
  PrepareToInstall (exit 7) or InstallMachineConfig (exit 2), as DEPLOIEMENT.txt says (a False here would abort it with
  another exit code, and the message box could block /VERYSILENT without /SUPPRESSMSGBOXES). }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Msg: String;
begin
  Result := True;
  if WizardSilent then
    Exit;
  if (CurPageID = wpSelectDir) and IsAdminInstallMode then
  begin
    Msg := AllUsersDirProblem(WizardDirValue);
    if Msg <> '' then
    begin
      MsgBox(Msg, mbError, MB_OK);
      Result := False;
    end;
  end;
  if (CurPageID = ConfigPage.ID) and (Trim(ConfigPage.Values[0]) <> '') and not FileExists(ConfigPage.Values[0]) then
  begin
    MsgBox('Ce fichier de configuration est introuvable : ' + ConfigPage.Values[0], mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if IsAdminInstallMode then
    Result := AllUsersDirProblem(ExpandConstant('{app}'));
  if Result <> '' then
  begin
    Log('install refused: ' + Result);
    Exit;
  end;
  CloseRunningPepite('install');
end;

// The school's machine file (D11): checked by the installed Pepite.exe --check-config, then copied to
// %PROGRAMDATA%\Pepite-Prediction\settings.json with an ACL only administrators may change (the app trusts it only
// then, SettingsLayering.cs). No /CONFIG and an existing file: left as it is.
function Icacls(const Args: String): Boolean;
var
  Rc: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\icacls.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Rc) and (Rc = 0);
  Log(Format('icacls %s -> %d', [Args, Rc]));
end;

procedure ConfigRefused(const Why, UserText: String);
begin
  ConfigFailed := True;
  Log('machine config refused: ' + Why);
  if not WizardSilent then
    MsgBox(UserText, mbError, MB_OK);
end;

procedure InstallMachineConfig();
var
  Raw, Src, Dir, Dst: String;
  Rc: Integer;
begin
  Raw := Trim(ConfigPage.Values[0]);
  if Raw = '' then
    Raw := CmdValue('CONFIG');
  if Raw = '' then
    Exit;
  Src := ExpandFileName(Raw);
  // a relative /CONFIG: the current folder first, then the installer's folder (an absolute path is recommended)
  if not FileExists(Src) and (ExtractFileDrive(Raw) = '') and FileExists(AddBackslash(ExpandConstant('{src}')) + Raw) then
    Src := AddBackslash(ExpandConstant('{src}')) + Raw;
  if not IsAdminInstallMode then
  begin
    ConfigRefused('/CONFIG needs an install for all users (/ALLUSERS)',
      'La configuration de l''établissement demande une installation pour tous les utilisateurs : elle n''a pas été installée.');
    Exit;
  end;
  if not Exec(ExpandConstant('{app}\Pepite.exe'), '--check-config "' + Src + '"', '', SW_HIDE, ewWaitUntilTerminated, Rc) then
    Rc := -1;
  Log(Format('machine config: --check-config "%s" -> %d', [Src, Rc]));
  if Rc <> 0 then
  begin
    ConfigFailed := True;
    if not WizardSilent then
      MsgBox('Le fichier de configuration n''est pas valable (' + Src + ') : il n''a pas été installé. ' +
        'Pépite est installé sans configuration de l''établissement. Vérifiez le fichier (voir DEPLOIEMENT.txt), puis relancez l''installation.',
        mbError, MB_OK);
    Exit;
  end;
  Dir := ExpandConstant('{commonappdata}\Pepite-Prediction');
  Dst := Dir + '\settings.json';
  // a folder a standard user created (ProgramData lets Users create folders), or a link, is never trusted nor
  // repaired: icacls would follow a junction planted inside it. IT deletes it, then runs the installation again.
  if DirExists(Dir) and (IsReparsePoint(Dir) or not OwnedByAdmins(Dir)) then
  begin
    ConfigRefused('the folder exists and is a link or is not owned by administrators: ' + Dir,
      'Le dossier ' + Dir + ' existe déjà mais n''a pas été créé par un administrateur. Par sécurité, la configuration ' +
      'n''a pas été installée. Supprimez ce dossier (compte administrateur), puis relancez l''installation.');
    Exit;
  end;
  if not DirExists(Dir) and not CreateDir(Dir) then
  begin
    ConfigRefused('cannot create ' + Dir, 'Le dossier ' + Dir + ' n''a pas pu être créé : la configuration n''a pas été installée.');
    Exit;
  end;
  if IsReparsePoint(Dir) or not Icacls('"' + Dir + '"' + ProtectedAcl) then
  begin
    ConfigRefused('cannot protect ' + Dir, 'Le dossier ' + Dir + ' n''a pas pu être protégé : la configuration n''a pas été installée.');
    Exit;
  end;
  if IsReparsePoint(Dst) then
  begin
    ConfigRefused(Dst + ' is a link', 'Le fichier ' + Dst + ' est un lien : la configuration n''a pas été installée. Supprimez-le, puis relancez l''installation.');
    Exit;
  end;
  // a file left by another account (before the folder was protected) is not overwritten in place: deleted, then copied
  if FileExists(Dst) and not OwnedByAdmins(Dst) then
  begin
    DeleteFile(Dst);
    Log('machine config: removed ' + Dst + ' (not owned by administrators)');
    if FileExists(Dst) then
    begin
      ConfigRefused('cannot remove ' + Dst, 'Le fichier ' + Dst + ' n''a pas pu être remplacé : la configuration n''a pas été installée.');
      Exit;
    end;
  end;
  if not FileCopy(Src, Dst, False) then
  begin
    ConfigRefused('copy failed', 'Le fichier de configuration n''a pas pu être copié dans ' + Dir + '.');
    Exit;
  end;
  // this one file only: inherit the folder's protection, owner Administrators
  if not Icacls('"' + Dst + '" /reset') or not Icacls('"' + Dst + '" /setowner *S-1-5-32-544') then
  begin
    ConfigRefused('cannot protect ' + Dst, 'Le fichier ' + Dst + ' n''a pas pu être protégé.');
    Exit;
  end;
  Log('machine config installed: ' + Dst);
end;

{ R6 (plan installer-upgrade): a silent upgrade closed the Pépite of this session (CloseRunningPepite); start it again,
  not elevated (ExecAsOriginalUser: the account that ran the setup; already elevated -> as it is, hence /NOLANCER).
  As SYSTEM the user's copy lives in another session's Local\ namespace: never seen as running, nothing restarted (the
  Run key starts the new version at the next logon). The exit code never depends on it. }
procedure RestartAfterSilentInstall();
var
  Rc: Integer;
begin
  if not WizardSilent or not IsUpgrade then
    Exit;
  if not PepiteWasRunning then
  begin
    Log('restart: Pepite was not running in this session');
    Exit;
  end;
  if CmdSwitch('NOLANCER') then
  begin
    Log('restart: skipped (/NOLANCER)');
    Exit;
  end;
  try
    if ExecAsOriginalUser(ExpandConstant('{app}\Pepite.exe'), '--autostart', '', SW_SHOWNORMAL, ewNoWait, Rc) then
      Log('restart: started')
    else
      Log(Format('restart: failed %d', [Rc]));
  except
    Log('restart: exception ' + GetExceptionMessage);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    InstallMachineConfig();
    RestartAfterSilentInstall();
  end;
end;

{ exit code 2 = the /CONFIG file was refused (DEPLOIEMENT.txt); Pépite itself is installed }
function GetCustomSetupExitCode(): Integer;
begin
  if ConfigFailed then
    Result := 2
  else
    Result := 0;
end;

{ ---------------------------------------------------------------- uninstall }

function InitializeUninstall(): Boolean;
begin
  CloseRunningPepite('uninstall');
  Result := True;
end;

procedure DeleteData(const Path: String);
begin
  if IsReparsePoint(Path) then
  begin
    Log('not deleted (a link): ' + Path);
    Exit;
  end;
  if DirExists(Path) then
  begin
    DelTree(Path, True, True, True);
    Log('deleted ' + Path);
  end else if FileExists(Path) then
  begin
    DeleteFile(Path);
    Log('deleted ' + Path);
  end;
end;

{ The pupils' data is never deleted without an explicit choice: interactive questions (default « Non »), or the silent
  switches /SUPPRIMERDONNEES (this account's settings, profiles, words, backups) and /SUPPRIMERCONFIG (the machine file). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data, Config: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  if UninstallSilent then
  begin
    Data := CmdSwitch('SUPPRIMERDONNEES');
    Config := CmdSwitch('SUPPRIMERCONFIG') and IsAdminInstallMode;
  end else begin
    Data := MsgBox('Supprimer aussi les réglages, les profils des élèves et leurs mots enregistrés sur ce compte Windows ?' + #13#10#13#10 +
      '(Les autres comptes ne sont pas concernés.)', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Config := IsAdminInstallMode and DirExists(ExpandConstant('{commonappdata}\Pepite-Prediction')) and
      (MsgBox('Supprimer aussi la configuration de l''établissement (réglages imposés à tous les comptes de cet ordinateur) ?',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES);
  end;
  Log(Format('uninstall: delete data %d, delete machine config %d', [Ord(Data), Ord(Config)]));
  if Data then
  begin
    DeleteData(ExpandConstant('{userappdata}\Pepite-Prediction'));
    DeleteData(ExpandConstant('{localappdata}\Pepite-Prediction'));
    DeleteData(ExpandConstant('{userappdata}\PredictKeyboard'));
    DeleteData(ExpandConstant('{%TEMP}\predict-keyboard.log'));
  end;
  if Config then
    DeleteData(ExpandConstant('{commonappdata}\Pepite-Prediction'));
end;
