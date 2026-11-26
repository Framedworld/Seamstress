import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// The key of the fit map the 3D view colours the cloth by: its colours from low to high, what the ends mean and the
// worst value shown now. Reads the "fitMap" object of App.viewportModels() (FitMapPainter.AppendJson).
Rectangle {
    id: legend

    property var info: ({ kind: "off" })
    readonly property string kind: info && info.kind ? info.kind : "off"
    readonly property var colours: info && info.colours ? info.colours : []

    function tr(text) { App.languageRevision; return App.tr(text) }
    function stop(i) { return i < colours.length ? colours[i] : "transparent" }
    function percent(v) { return (v >= 0 ? "+" : "") + Math.round(v * 100) + "%" }

    readonly property string title: kind === "stretch" ? tr("Stretch") : kind === "pressure" ? tr("Pressure") : tr("Distance to body")
    readonly property var ends: kind === "stretch" ? ["−10%", "0", "+10%"]
        : kind === "pressure" ? [tr("Free"), "", tr("Tight")]
        : ["0 cm", "2.5", "5+ cm"]
    // The 1st and 99th percentile of what is shown (FitMapPainter), not the single most extreme vertex.
    readonly property string worst: info === undefined || info.high === undefined ? ""
        : kind === "stretch" ? tr("Most stretched: %1").arg(percent(info.high))
        : kind === "pressure" ? tr("Tightest: %1").arg(Math.round(info.high * 100) + "%")
        : tr("Closest: %1 cm  ·  loosest: %2 cm").arg(info.low.toFixed(1)).arg(info.high >= info.top - 0.1 ? info.top.toFixed(0) + "+" : info.high.toFixed(1))

    visible: kind !== "off"
    implicitWidth: Math.max(176, column.implicitWidth + 16)
    implicitHeight: column.implicitHeight + 12
    radius: 6
    color: Qt.alpha(palette.window, 0.84)
    border.color: Qt.alpha(palette.windowText, 0.12)
    border.width: 1

    ColumnLayout {
        id: column
        anchors { left: parent.left; right: parent.right; top: parent.top; margins: 8; topMargin: 6 }
        spacing: 3
        Label { text: legend.title; font.pixelSize: 11; font.weight: Font.DemiBold; opacity: 0.92 }
        Rectangle {
            Layout.fillWidth: true
            implicitHeight: 9
            radius: 2
            gradient: Gradient {
                orientation: Gradient.Horizontal
                // Five evenly spaced stops (FitMaps.LegendColours).
                GradientStop { position: 0.0; color: legend.stop(0) }
                GradientStop { position: 0.25; color: legend.stop(1) }
                GradientStop { position: 0.5; color: legend.stop(2) }
                GradientStop { position: 0.75; color: legend.stop(3) }
                GradientStop { position: 1.0; color: legend.stop(4) }
            }
        }
        RowLayout {
            Layout.fillWidth: true
            Label { text: legend.ends[0]; font.pixelSize: 10; opacity: 0.8 }
            Item { Layout.fillWidth: true }
            Label { text: legend.ends[1]; font.pixelSize: 10; opacity: 0.8 }
            Item { Layout.fillWidth: true }
            Label { text: legend.ends[2]; font.pixelSize: 10; opacity: 0.8 }
        }
        Label {
            visible: text !== ""
            text: legend.worst
            font.pixelSize: 10
            opacity: 0.8
        }
    }
}
