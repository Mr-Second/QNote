; Chinese (Simplified) strings for QNote installer
; This file is UTF-8 with BOM. NSIS reads it correctly because Unicode true is set.
; Do NOT put Chinese directly in the parent specfile — xpack's io.gsub mangles non-ASCII.

LangString MsgRunningInstall ${LANG_SIMPCHINESE} "QNote 正在运行，必须关闭才能继续安装。$\r$\n$\r$\n点击「确定」自动关闭它，点击「取消」中止安装。"
LangString MsgRunningUninstall ${LANG_SIMPCHINESE} "QNote 正在运行，必须关闭才能卸载。$\r$\n$\r$\n点击「确定」自动关闭它，点击「取消」中止卸载。"
LangString MsgFinishRun ${LANG_SIMPCHINESE} "运行 QNote"
LangString MsgAskRemoveUserData ${LANG_SIMPCHINESE} "是否同时删除 QNote 的用户数据？$\r$\n$\r$\n这将清理所有便签、设置、日志（位于 %APPDATA%\QNote\），不可恢复。$\r$\n$\r$\n点击「是」删除，点击「否」保留。"

; English fallback (in case specfile referenced $(...) before English LangString defined)
LangString MsgRunningInstall ${LANG_ENGLISH} "QNote is currently running and must be closed to install.$\r$\n$\r$\nClick OK to close it automatically, Cancel to abort."
LangString MsgRunningUninstall ${LANG_ENGLISH} "QNote is currently running and must be closed to uninstall.$\r$\n$\r$\nClick OK to close it automatically, Cancel to abort."
LangString MsgFinishRun ${LANG_ENGLISH} "Run QNote"
LangString MsgAskRemoveUserData ${LANG_ENGLISH} "Also remove QNote user data?$\r$\n$\r$\nThis will delete all notes, settings and logs (in %APPDATA%\QNote\), cannot be undone.$\r$\n$\r$\nClick Yes to remove, No to keep."
