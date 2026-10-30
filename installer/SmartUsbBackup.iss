; =====================================================================
;  Smart USB Backup 2.0 — Inno Setup Script (Inno Setup 6.x)
;
;  Что делает:
;    • Устанавливает publish-папку self-contained WinUI 3-приложения в
;      {localappdata}\Programs\SmartUsbBackup (per-user, права админа не нужны).
;    • Чекбоксы установки: ярлык на рабочем столе, автозапуск с Windows
;      (HKCU\...\Run — добавляется также из самой программы).
;    • Обновление поверх старой версии: папка {app} чистится перед копированием.
;
;  При ДЕИНСТАЛЛЯЦИИ («Параметры Windows → Приложения → Smart USB Backup»):
;    • Pascal Script показывает КАСТОМНОЕ ОКНО со списком папок бэкапов
;      (читает базовый путь из HKCU\Software\SmartUsbBackup\BaseBackupPath —
;      Unicode-корректно, без парсинга JSON):
;        галочка   = удалить папку бэкапа с ПК
;        без галки = оставить файлы пользователя
;    • Удаляет ярлыки, автозапуск, конфиги; выбранные папки бэкапов удаляются,
;      оставшиеся (и конфиг) сохраняются на ПК.
; =====================================================================

#define MyAppName "Smart USB Backup"
#define MyAppVersion "3.0.6"
#define MyAppPublisher "Smart USB Backup"
#define MyAppExeName "SmartUsbBackup.exe"
#define MyAppId "{C7A5B9E4-2BFE-4D42-A27C-78B9A5C1E1F3}"

[Setup]
AppId={{#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppName}

; 64-битная установка: только x64-совместимые процессоры.
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible

; Per-user установка в %LocalAppData%\Programs — НЕ требует прав администратора.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline

DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Отображение в «Параметры → Приложения»
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

WizardStyle=modern
DisableProgramGroupPage=yes
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

; Если процесс приложения ещё жив (пользователь забыл выйти), корректно завершаем его.
CloseApplications=yes
RestartApplications=no

; Результат сборки: SmartUsbBackup-Setup-2.0.0.exe в installer\dist
OutputDir=dist
OutputBaseFilename=SmartUsbBackup-Setup-{#MyAppVersion}
SetupIconFile=..\Assets\appicon.ico

; ---------- Файлы, которые ставим ----------
[Files]
; Вся publish-папка self-contained WinUI 3 приложения:
;   .exe + .NET 8 runtime + Windows App SDK (WindowsAppSDKSelfContained=true).
; Ничего дополнительно ставить на машину не нужно.
Source: "..\bin\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; ---------- Чистка предыдущей версии перед установкой ----------
[InstallDelete]
Type: filesandordirs; Name: "{app}"

; ---------- Ярлыки ----------
[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

; ---------- Задачи (галочки на этапе установки) ----------
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные значки:"; Flags: checkedonce
Name: "autorun"; Description: "Запускать Smart USB Backup вместе с Windows (скрытно, иконка в трее)"; GroupDescription: "Интеграция с Windows:"; Flags: checkedonce

; ---------- Реестр: автозапуск (HKCU — не требует прав администратора) ----------
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SmartUsbBackup"; ValueData: """{app}\{#MyAppExeName}"" -hidden"; Flags: uninsdeletevalue; Tasks: autorun

; ---------- Запуск после установки ----------
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить {#MyAppName}"; Flags: nowait postinstall  runasoriginaluser

; ---------- Файлы, удаляемые деинсталлятором автоматически ----------
; (Папки бэкапов тут НЕ удаляются — их судьбу решает пользователь в кастомном окне.)
[UninstallDelete]
; .tmp-файлы от прерванных копирований
Type: files; Name: "{localappdata}\SmartUsbBackup\*.tmp"

; =====================================================================
;  PASCAL SCRIPT — кастомное окно выбора папок бэкапов при удалении
; =====================================================================
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Code]

var
  BackupFolders: TArrayOfString;      // найденные папки бэкапов
  BackupChecked: TArrayOfBoolean;     // выбор пользователя: True = удалить
  BackupKeptCount: Integer;           // сколько папок ОСТАВЛЕНО

// ---------------------------------------------------------------------
// Базовая папка бэкапов: сначала реестр (пишется программой при каждом
// сохранении настроек), иначе стандартный путь.
// ---------------------------------------------------------------------
function GetBaseBackupPath: string;
var
  RegValue: string;
begin
  if RegQueryStringValue(HKEY_CURRENT_USER, 'Software\SmartUsbBackup',
       'BaseBackupPath', RegValue) and (Trim(RegValue) <> '') then
    Result := RegValue
  else
    Result := ExpandConstant('{localappdata}') + '\SmartUsbBackup\Backups';
end;

// ---------------------------------------------------------------------
// Сбор списка ПОДПАПОК базовой папки (это и есть копии флешек).
// ---------------------------------------------------------------------
procedure CollectBackupSubDirs;
var
  Root, Name: string;
  FindRec: TFindRec;
  Count: Integer;
begin
  Root := GetBaseBackupPath;
  SetArrayLength(BackupFolders, 0);
  Count := 0;

  if DirExists(Root) then
  begin
    if FindFirst(Root + '\*', FindRec) then
    try
      repeat
        Name := FindRec.Name;
        if (Name <> '.') and (Name <> '..') and (Copy(Name, 1, 4) = 'USB_') and DirExists(Root + '\' + Name) then
        begin
          SetArrayLength(BackupFolders, Count + 1);
          BackupFolders[Count] := Name;
          Count := Count + 1;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// ---------------------------------------------------------------------
// КАСТОМНОЕ ОКНО деинсталляции (TForm + TNewCheckListBox).
// Список папок зеркальных копий с чекбоксами:
//   галочка   = удалить с ПК вместе с приложением
//   без галки = ОСТАВИТЬ на ПК (безопасная политика: ничего не трогаем
//               без явного согласия пользователя)
// ---------------------------------------------------------------------
function ShowChooseBackupsDialog: Boolean;
var
  Form: TForm;
  Intro: TNewStaticText;
  ListBox: TNewCheckListBox;
  BtnDelete, BtnKeep, BtnCancel: TNewButton;
  I: Integer;
begin
  Result := True; // если спрашивать не о чем — продолжаем деинсталляцию

  CollectBackupSubDirs;
  if GetArrayLength(BackupFolders) = 0 then
    Exit;                                  // бэкапов нет — окно не показываем

  Form := TForm.Create(nil);
  try
    Form.Caption := 'Smart USB Backup — копии флешек';
    Form.Width := ScaleX(560);
    Form.Height := ScaleY(360);
    Form.BorderStyle := bsDialog;
    Form.Position := poScreenCenter;

    Intro := TNewStaticText.Create(Form);
    Intro.Parent := Form;
    Intro.Caption :=
      'Программа будет удалена. Что сделать с зеркальными копиями ваших флешек?' + #13#10 +
      'Отмеченные папки будут удалены с компьютера, неотмеченные — останутся.';
    Intro.Left := ScaleX(12);
    Intro.Top := ScaleY(12);
    Intro.Width := Form.ClientWidth - ScaleX(24);
    Intro.WordWrap := True;

    ListBox := TNewCheckListBox.Create(Form);
    ListBox.Parent := Form;
    ListBox.Left := ScaleX(12);
    ListBox.Top := Intro.Top + Intro.Height + ScaleY(10);
    ListBox.Width := Form.ClientWidth - ScaleX(24);
    // Высота — до кнопок: 46 DIP снизу под кнопки + 12 DIP отступ.
    ListBox.Height := Form.ClientHeight - ScaleY(46) - ListBox.Top - ScaleY(12);
    ListBox.Flat := True;

    for I := 0 to GetArrayLength(BackupFolders) - 1 do
      ListBox.AddCheckBox(BackupFolders[I], '', 0, False, True, False, True, nil);

    // Кнопки — стандартной высоты (30 DIP), иначе подпись обрезается,
    // и прижаты к правому нижнему углу, как в обычных диалогах Windows.
    BtnDelete := TNewButton.Create(Form);
    BtnDelete.Parent := Form;
    BtnDelete.Caption := 'Удалить отмеченные';
    BtnDelete.ModalResult := mrYes;
    BtnDelete.Width := ScaleX(160);
    BtnDelete.Height := ScaleY(30);
    BtnDelete.Top := Form.ClientHeight - ScaleY(46);

    BtnKeep := TNewButton.Create(Form);
    BtnKeep.Parent := Form;
    BtnKeep.Caption := 'Оставить все копии';
    BtnKeep.ModalResult := mrNo;
    BtnKeep.Width := ScaleX(150);
    BtnKeep.Height := ScaleY(30);
    BtnKeep.Top := BtnDelete.Top;
    BtnKeep.Default := True;   // Enter = безопасный выбор «оставить»

    BtnCancel := TNewButton.Create(Form);
    BtnCancel.Parent := Form;
    BtnCancel.Caption := 'Отмена';
    BtnCancel.ModalResult := mrCancel;
    BtnCancel.Width := ScaleX(90);
    BtnCancel.Height := ScaleY(30);
    BtnCancel.Top := BtnDelete.Top;
    BtnCancel.Cancel := True;  // Esc = отмена деинсталляции

    BtnCancel.Left := Form.ClientWidth - ScaleX(12) - BtnCancel.Width;
    BtnKeep.Left := BtnCancel.Left - ScaleX(10) - BtnKeep.Width;
    BtnDelete.Left := BtnKeep.Left - ScaleX(10) - BtnDelete.Width;

    SetArrayLength(BackupChecked, GetArrayLength(BackupFolders));
    for I := 0 to GetArrayLength(BackupFolders) - 1 do
      BackupChecked[I] := False;

    case Form.ShowModal of
      mrYes:
        begin
          for I := 0 to GetArrayLength(BackupFolders) - 1 do
            BackupChecked[I] := ListBox.Checked[I];
          Result := True;
        end;
      mrNo: Result := True;      // «оставить все» — ничего не удаляем
      mrCancel: Result := False; // пользователь отменил деинсталляцию
    end;
  finally
    Form.Free;
  end;
end;

// ---------------------------------------------------------------------
// Инициализация деинсталляции: кастомное окно ДО начала удаления.
// Отмена — деинсталляция не начинается.
// ---------------------------------------------------------------------
function InitializeUninstall: Boolean;
begin
  // Тихая деинсталляция (/VERYSILENT, используется при обновлениях и в
  // корпоративных сценариях) не должна ждать интерактивного окна:
  // ничего не удаляем без явного согласия пользователя.
  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;

  Result := ShowChooseBackupsDialog;
end;

// ---------------------------------------------------------------------
// Удаляем автозапуск из реестра (на случай, если ставили не через инсталлятор).
// ---------------------------------------------------------------------
procedure RemoveRunEntry;
begin
  RegDeleteValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', 'SmartUsbBackup');
  RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\SmartUsbBackup');
end;

// ---------------------------------------------------------------------
// Главная логика удаления выбранных папок и конфигов.
//   usUninstall     — до удаления файлов (запись реестра)
//   usPostUninstall — после удаления файлов приложения (папки бэкапов)
// ---------------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Root, Folder: string;
  I, DeletedCount, TotalCount: Integer;
begin
  case CurUninstallStep of
    usUninstall:
      RemoveRunEntry;

    usPostUninstall:
      begin
        Root := GetBaseBackupPath;
        TotalCount := GetArrayLength(BackupFolders);
        DeletedCount := 0;

        for I := 0 to TotalCount - 1 do
        begin
          if (I < GetArrayLength(BackupChecked)) and BackupChecked[I] then
          begin
            Folder := Root + '\' + BackupFolders[I];
            if DirExists(Folder) then
            begin
              DelTree(Folder, True, True, True);
              DeletedCount := DeletedCount + 1;
              Log('Удалена копия флешки: ' + Folder);
            end;
          end;
        end;

        BackupKeptCount := TotalCount - DeletedCount;

        // Если пользователь удалил ВСЕ копии (или их не было) — чистим и конфиг:
        // настройки settings.json, логи, саму папку SmartUsbBackup.
        // Если копии остались — конфиг сохраняем (при повторной установке
        // профили флешек и базовый путь продолжат работать).
        if BackupKeptCount = 0 then
        begin
          DelTree(ExpandConstant('{localappdata}\SmartUsbBackup'), True, True, True);
          Log('Копий не осталось → удалён весь каталог %LocalAppData%\SmartUsbBackup');
        end
        else
          Log('Осталось копий: ' + IntToStr(BackupKeptCount) + ' — конфигурация сохранена');
      end;
  end;
end;

// ---------------------------------------------------------------------
// Завершение деинсталляции.
// ---------------------------------------------------------------------
procedure DeinitializeUninstall;
begin
  Log('Smart USB Backup деинсталлирован');
end;
