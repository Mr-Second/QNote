#pragma once

#include <QLatin1String>
#include <QString>
#include <QtGlobal>
#include <QCoreApplication>

#define DBG(x) do { qDebug() << x; } while(0)

namespace Constants {

inline QString defaultCategoryKey() { return QStringLiteral("all"); }
inline QString workCategoryKey() { return QStringLiteral("work"); }
inline QString lifeCategoryKey() { return QStringLiteral("life"); }
inline QString importantCategoryKey() { return QStringLiteral("important"); }

inline QString defaultCategory() { return QCoreApplication::translate("Constants", "全部"); }
inline QString workCategory() { return QCoreApplication::translate("Constants", "工作"); }
inline QString lifeCategory() { return QCoreApplication::translate("Constants", "生活"); }
inline QString importantCategory() { return QCoreApplication::translate("Constants", "重要"); }
inline QString newNoteTitle() { return QCoreApplication::translate("Constants", "新建便签"); }
inline QString colorAll() { return QStringLiteral("#8B8B8B"); }
inline QString colorWork() { return QStringLiteral("#1677b3"); }
inline QString colorLife() { return QStringLiteral("#40a070"); }
inline QString colorImportant() { return QStringLiteral("#ed5126"); }
inline QString defaultPicgoHost() { return QStringLiteral("127.0.0.1"); }
inline QString defaultPicgoPort() { return QStringLiteral("36677"); }

namespace Settings {

inline constexpr QLatin1String uiFontFamily{"uiFontFamily"};
inline constexpr QLatin1String uiFontSize{"uiFontSize"};
inline constexpr QLatin1String editorFontFamily{"editorFontFamily"};
inline constexpr QLatin1String editorFontSize{"editorFontSize"};
inline constexpr QLatin1String imageStorageMode{"imageStorageMode"};
inline constexpr QLatin1String picgoHost{"picgoHost"};
inline constexpr QLatin1String picgoPort{"picgoPort"};
inline constexpr QLatin1String startMinimized{"startMinimized"};
inline constexpr QLatin1String defaultThemeMode{"defaultThemeMode"};
inline constexpr QLatin1String autoSaveInterval{"autoSaveInterval"};
inline constexpr QLatin1String rememberWindowGeometry{"rememberWindowGeometry"};
inline constexpr QLatin1String stickOnTopDefault{"stickOnTopDefault"};
inline constexpr QLatin1String windowX{"windowX"};
inline constexpr QLatin1String windowY{"windowY"};
inline constexpr QLatin1String windowWidth{"windowWidth"};
inline constexpr QLatin1String windowHeight{"windowHeight"};
inline constexpr QLatin1String listDensity{"listDensity"};
inline constexpr QLatin1String timeFormat{"timeFormat"};
inline constexpr QLatin1String noteSortOrder{"noteSortOrder"};
inline constexpr QLatin1String confirmBeforeDelete{"confirmBeforeDelete"};
inline constexpr QLatin1String launchAtStartup{"launchAtStartup"};
inline constexpr QLatin1String language{"language"};

}

}
